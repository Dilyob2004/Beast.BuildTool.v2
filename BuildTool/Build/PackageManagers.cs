using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>Headers, libraries and runtime binaries provided by a package manager for one platform/configuration.</summary>
public sealed class PackageSet
{
    public required string Source { get; init; }
    public List<string> IncludeDirectories { get; } = [];
    public List<string> LibraryDirectories { get; } = [];
    public List<string> BinaryDirectories { get; } = [];
    /// <summary>Libraries linked into every EXE/DLL/.so (Conan); vcpkg libraries are requested per module.</summary>
    public List<string> Libraries { get; } = [];
    public List<string> SystemLibraries { get; } = [];
    public List<string> Definitions { get; } = [];
}

/// <summary>
/// vcpkg (manifest mode, vcpkg.json) and Conan 2 (conanfile.txt/conanfile.py) next to Source/.
/// Dependencies are installed before compilation, into Intermediate/, and only again when the
/// manifest, the triplet/profile or the tool changes. Package headers are external includes
/// (/external:I, -isystem); runtime DLLs are copied beside EXE/DLL targets; Android .so files go
/// into the APK.
/// </summary>
public static class PackageManagers
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PackageSet>> Prepared = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string project, Platform platform, TargetConfiguration configuration) =>
        Path.GetFullPath(project) + "|" + platform.PlatformName + "|" + configuration;

    public static bool UsesVcpkg(string project) => File.Exists(Path.Combine(project, "vcpkg.json")) && BuildConfigManager.Config.Vcpkg.Enabled;
    public static string? ConanFile(string project) => !BuildConfigManager.Config.Conan.Enabled ? null
        : new[] { "conanfile.py", "conanfile.txt" }.Select(n => Path.Combine(project, n)).FirstOrDefault(File.Exists);

    public static Task PrepareAsync(ProjectModel model, Platform platform, TargetConfiguration configuration) =>
        PrepareAsync(model.ProjectDirectory, platform, configuration, model.Targets.Any(t => t.Rules.StaticRuntime));

    public static async Task PrepareAsync(string project, Platform platform, TargetConfiguration configuration, bool staticRuntime)
    {
        bool vcpkg = UsesVcpkg(project);
        string? conan = ConanFile(project);
        if (!vcpkg && conan == null) { Prepared[Key(project, platform, configuration)] = []; return; }
        // One installer at a time per project: vcpkg/Conan share their install trees between configurations.
        using var installLock = await ProjectBuildLock.AcquireTargetAsync(project, "PackageManagers");
        var sets = new List<PackageSet>();
        if (vcpkg) sets.Add(await VcpkgAsync(project, platform, configuration, staticRuntime));
        if (conan != null) sets.Add(await ConanAsync(project, conan, platform, configuration, staticRuntime));
        Prepared[Key(project, platform, configuration)] = sets;
    }

    /// <summary>Package sets of this build, or (for IDE generation) of the last install found on disk.</summary>
    public static IReadOnlyList<PackageSet> For(string project, Platform platform, TargetConfiguration configuration)
    {
        if (Prepared.TryGetValue(Key(project, platform, configuration), out var sets)) return sets;
        var found = new List<PackageSet>();
        try
        {
            if (UsesVcpkg(project) && Directory.Exists(VcpkgTripletDirectory(project, VcpkgTriplet(platform, false))))
                found.Add(VcpkgSet(project, VcpkgTriplet(platform, false), configuration));
            string graph = Path.Combine(ConanDirectory(project, platform, configuration), "graph.json");
            if (ConanFile(project) != null && File.Exists(graph)) found.Add(ParseConanGraph(File.ReadAllText(graph)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { Log.TraceVerbose("Package metadata unavailable: " + ex.Message); }
        // Read once per process: IDE generation asks for every file and configuration.
        Prepared.TryAdd(Key(project, platform, configuration), found);
        return found;
    }

    internal static void ApplyCompile(ProjectModel model, Platform platform, TargetConfiguration configuration, CPPEnvironment environment)
    {
        foreach (var set in For(model.ProjectDirectory, platform, configuration))
        {
            environment.ExternalIncludePaths.AddRange(set.IncludeDirectories);
            environment.PreprocessorDefinitions.AddRange(set.Definitions);
        }
    }

    /// <summary>
    /// Adds package library directories, Conan libraries and the vcpkg libraries requested by
    /// <paramref name="modules"/>. Returns shared libraries that must ship with the product.
    /// </summary>
    internal static List<string> ApplyLink(string project, Platform platform, TargetConfiguration configuration, IEnumerable<ModuleRules> modules, LinkEnvironment link)
    {
        var sets = For(project, platform, configuration);
        var shipped = new List<string>();
        if (sets.Count == 0)
        {
            if (modules.Any(m => m.VcpkgLibraries.Count > 0))
                throw new InvalidOperationException("VcpkgLibraries are declared, but the project has no vcpkg.json (or vcpkg.enabled is false).");
            return shipped;
        }
        foreach (var set in sets)
        {
            link.LibraryPaths.AddRange(set.LibraryDirectories);
            foreach (string library in set.Libraries.Concat(set.SystemLibraries))
                link.InputLibraries.Add(platform.Type == PlatformType.Windows && !library.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ? library + ".lib" : library);
            if (platform.Type == PlatformType.Android)
                foreach (string library in set.Libraries)
                    if (Find(set.LibraryDirectories, "lib" + library + ".so") is { } so) shipped.Add(so);
        }
        var vcpkg = sets.FirstOrDefault(s => s.Source == "vcpkg");
        foreach (var module in modules)
            foreach (string name in module.VcpkgLibraries)
            {
                if (vcpkg == null) throw new InvalidOperationException($"{module.ModuleName}: VcpkgLibraries requires vcpkg.json in the project root.");
                string resolved = ResolveVcpkgLibrary(vcpkg, platform, configuration, name);
                link.InputLibraries.Add(resolved);
                if (resolved.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) shipped.Add(resolved);
            }
        return shipped;
    }

    /// <summary>
    /// Shared libraries of the packages, copied beside the EXE/program: Windows DLLs from bin/;
    /// Linux lib&lt;name&gt;.so* and macOS lib&lt;name&gt;*.dylib of linked libraries (found via rpath).
    /// </summary>
    internal static IEnumerable<string> RuntimeBinaries(string project, Platform platform, TargetConfiguration configuration) => platform switch
    {
        WindowsPlatform => For(project, platform, configuration)
            .SelectMany(s => s.BinaryDirectories).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll")).Distinct(StringComparer.OrdinalIgnoreCase),
        UnixPlatform => For(project, platform, configuration)
            .SelectMany(s => s.Libraries.SelectMany(library => s.LibraryDirectories.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "lib" + library + (platform.Type == PlatformType.Mac ? "*.dylib" : ".so*")))))
            .Where(f => platform.Type == PlatformType.Mac || f.EndsWith(".so") || System.Text.RegularExpressions.Regex.IsMatch(f, @"\.so(\.\d+)+$"))
            .Distinct(StringComparer.Ordinal),
        _ => []
    };

    /// <summary>vcpkg naming: fmt.lib / fmtd.lib (Debug), libfmt.a / libfmt.so; an explicit file name is used as is.</summary>
    internal static string ResolveVcpkgLibrary(PackageSet set, Platform platform, TargetConfiguration configuration, string name)
    {
        var candidates = Path.HasExtension(name) ? [name]
            : platform.Type == PlatformType.Windows
                ? configuration == TargetConfiguration.Debug ? new[] { name + "d.lib", name + ".lib" } : [name + ".lib"]
                : ["lib" + name + ".a", "lib" + name + ".so"];
        foreach (string candidate in candidates)
            if (Find(set.LibraryDirectories, candidate) is { } path) return path;
        var available = set.LibraryDirectories.Where(Directory.Exists).SelectMany(Directory.EnumerateFiles)
            .Select(Path.GetFileName).Where(f => f!.EndsWith(".lib") || f.EndsWith(".a") || f.EndsWith(".so")).Order().Take(40);
        throw new FileNotFoundException($"vcpkg library '{name}' was not found in {string.Join(", ", set.LibraryDirectories)}. Available: {string.Join(", ", available)}");
    }

    private static string? Find(IEnumerable<string> directories, string file) =>
        directories.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);

    // ---------------------------------------------------------------- vcpkg

    internal static string VcpkgTriplet(Platform platform, bool staticRuntime)
    {
        string configured = BuildConfigManager.Config.Vcpkg.Triplet;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return platform switch
        {
            AndroidPlatform android => android.Abi == "x86_64" ? "x64-android" : "arm64-android",
            LinuxPlatform => platform.Architecture == PlatformArch.ARM64 ? "arm64-linux" : "x64-linux",
            MacPlatform => platform.Architecture == PlatformArch.ARM64 ? "arm64-osx" : "x64-osx",
            IosPlatform ios => !ios.Simulator ? "arm64-ios" : ios.Architecture == PlatformArch.ARM64 ? "arm64-ios-simulator" : "x64-ios",
            _ => (platform.Architecture == PlatformArch.ARM64 ? "arm64-windows" : "x64-windows") + (staticRuntime ? "-static" : "")
        };
    }

    private static string VcpkgInstallRoot(string project) => Path.Combine(project, "Intermediate", "vcpkg", "installed");
    private static string VcpkgTripletDirectory(string project, string triplet) => Path.Combine(VcpkgInstallRoot(project), triplet);

    private static PackageSet VcpkgSet(string project, string triplet, TargetConfiguration configuration)
    {
        string root = VcpkgTripletDirectory(project, triplet);
        string prefix = configuration == TargetConfiguration.Debug ? Path.Combine(root, "debug") : root;
        var set = new PackageSet { Source = "vcpkg" };
        set.IncludeDirectories.Add(Path.Combine(root, "include"));
        set.LibraryDirectories.Add(Path.Combine(prefix, "lib"));
        set.BinaryDirectories.Add(Path.Combine(prefix, "bin"));
        return set;
    }

    internal static string FindVcpkg()
    {
        var settings = BuildConfigManager.Config.Vcpkg;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Root)) roots.Add(Environment.ExpandEnvironmentVariables(settings.Root));
        if (Environment.GetEnvironmentVariable("VCPKG_ROOT") is { Length: > 0 } environmentRoot) roots.Add(environmentRoot);
        // Visual Studio 2022 17.6+ ships vcpkg in VC/vcpkg.
        if (HostPlatform.IsWindows && VisualStudioInstance.VCToolChainPath is { } vc) roots.Add(Path.GetFullPath(Path.Combine(vc, "..", "..", "..", "vcpkg")));
        foreach (string root in roots)
        {
            string executable = Path.Combine(root, HostPlatform.Executable("vcpkg"));
            if (File.Exists(executable)) return executable;
        }
        throw new FileNotFoundException("vcpkg.json is present, but vcpkg was not found. Set vcpkg.root in Config/BuildConfig.json or VCPKG_ROOT.");
    }

    private static async Task<PackageSet> VcpkgAsync(string project, Platform platform, TargetConfiguration configuration, bool staticRuntime)
    {
        string triplet = VcpkgTriplet(platform, staticRuntime);
        string executable = FindVcpkg();
        string installRoot = VcpkgInstallRoot(project);
        string state = Path.Combine(project, "Intermediate", "vcpkg", triplet + ".state");
        string fingerprint = BuildCacheSession.HashText(string.Join('\n', new[] { "vcpkg.json", "vcpkg-configuration.json" }
            .Select(n => Path.Combine(project, n)).Select(p => p + ":" + BuildCacheSession.HashFile(p))
            .Append(triplet).Append(executable).Append(BuildCacheSession.HashFile(executable))
            .Concat(BuildConfigManager.Config.Vcpkg.Arguments)));
        if (File.Exists(state) && File.ReadAllText(state) == fingerprint && Directory.Exists(VcpkgTripletDirectory(project, triplet)))
            Log.TraceVerbose($"vcpkg packages for {triplet} are up to date.");
        else
        {
            if (File.Exists(state)) File.Delete(state);
            var arguments = new List<string> { "install", "--triplet", triplet, "--x-manifest-root=" + project, "--x-install-root=" + installRoot };
            arguments.AddRange(BuildConfigManager.Config.Vcpkg.Arguments);
            var environment = new Dictionary<string, string>();
            if (platform is AndroidPlatform android) environment["ANDROID_NDK_HOME"] = android.NdkRoot;
            Log.TraceInfo($"Installing vcpkg packages ({triplet})...");
            var (exit, output) = await RunAsync(executable, arguments, project, environment);
            if (exit != 0) throw new InvalidOperationException($"vcpkg install failed ({exit}):{Environment.NewLine}{output}");
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            File.WriteAllText(state, fingerprint);
        }
        return VcpkgSet(project, triplet, configuration);
    }

    // ---------------------------------------------------------------- Conan

    private static string ConanDirectory(string project, Platform platform, TargetConfiguration configuration) =>
        Path.Combine(project, "Intermediate", "conan", platform.PlatformName + "-" + configuration);

    /// <summary>Host profile for Android: NDK, API level, ABI and libc++ flavor from the Beast configuration.</summary>
    internal static string AndroidProfile(AndroidPlatform android, TargetConfiguration configuration, bool staticRuntime, int clangMajor)
    {
        string arch = android.Abi == "x86_64" ? "x86_64" : "armv8";
        return string.Join('\n',
            "[settings]", "os=Android", $"os.api_level={BuildConfigManager.Config.Android.MinSdk}", $"arch={arch}",
            "compiler=clang", $"compiler.version={clangMajor}", $"compiler.libcxx={(staticRuntime ? "c++_static" : "c++_shared")}",
            "compiler.cppstd=20", $"build_type={ConanBuildType(configuration)}",
            "[conf]", $"tools.android:ndk_path={android.NdkRoot.Replace('\\', '/')}") + "\n";
    }

    private static string ConanBuildType(TargetConfiguration configuration) => configuration == TargetConfiguration.Debug ? "Debug" : "Release";

    internal static List<string> ConanArguments(string project, string output, Platform platform, TargetConfiguration configuration, bool staticRuntime, string? hostProfile)
    {
        var settings = BuildConfigManager.Config.Conan;
        var arguments = new List<string> { "install", project, "--output-folder", output, "--build=missing", "--format=json" };
        if (hostProfile != null) arguments.AddRange(["-pr:h", hostProfile]);
        else if (!string.IsNullOrWhiteSpace(settings.Profile)) arguments.AddRange(["-pr:h", Environment.ExpandEnvironmentVariables(settings.Profile)]);
        arguments.AddRange(["-pr:b", "default"]);
        if (platform.Type == PlatformType.Windows)
        {
            arguments.AddRange(["-s", "build_type=" + ConanBuildType(configuration)]);
            arguments.AddRange(["-s", "compiler.runtime=" + (staticRuntime ? "static" : "dynamic")]);
            arguments.AddRange(["-s", "compiler.runtime_type=" + ConanBuildType(configuration)]);
            if (platform.Architecture == PlatformArch.ARM64) arguments.AddRange(["-s", "arch=armv8"]);
        }
        else if (platform is UnixPlatform)
        {
            // The default profile describes this machine; the target architecture may differ.
            arguments.AddRange(["-s", "build_type=" + ConanBuildType(configuration)]);
            arguments.AddRange(["-s", "arch=" + (platform.Architecture == PlatformArch.ARM64 ? "armv8" : "x86_64")]);
        }
        arguments.AddRange(settings.Arguments);
        return arguments;
    }

    private static async Task<PackageSet> ConanAsync(string project, string conanfile, Platform platform, TargetConfiguration configuration, bool staticRuntime)
    {
        var settings = BuildConfigManager.Config.Conan;
        string executable = string.IsNullOrWhiteSpace(settings.Executable) ? HostPlatform.Executable("conan") : Environment.ExpandEnvironmentVariables(settings.Executable);
        string output = ConanDirectory(project, platform, configuration);
        Directory.CreateDirectory(output);
        string? hostProfile = null;
        if (platform is AndroidPlatform android && string.IsNullOrWhiteSpace(settings.Profile))
        {
            hostProfile = Path.Combine(output, "android.profile");
            ModuleCompilation.WriteIfChanged(hostProfile, AndroidProfile(android, configuration, staticRuntime, await ClangMajorAsync(android)));
        }
        var arguments = ConanArguments(project, output, platform, configuration, staticRuntime, hostProfile);
        string graph = Path.Combine(output, "graph.json"), state = Path.Combine(output, "install.state");
        string? profileText = hostProfile ?? (string.IsNullOrWhiteSpace(settings.Profile) ? null : Environment.ExpandEnvironmentVariables(settings.Profile));
        string fingerprint = BuildCacheSession.HashText(string.Join('\n', arguments) + "\n" + BuildCacheSession.HashFile(conanfile) +
            "\n" + (profileText != null ? BuildCacheSession.HashFile(profileText) : "") +
            "\n" + BuildCacheSession.HashFile(Path.Combine(project, "conan.lock")));
        if (File.Exists(state) && File.Exists(graph) && File.ReadAllText(state) == fingerprint)
            Log.TraceVerbose($"Conan packages for {platform.PlatformName} {configuration} are up to date.");
        else
        {
            if (File.Exists(state)) File.Delete(state);
            Log.TraceInfo($"Installing Conan packages ({platform.PlatformName} {configuration})...");
            var (exit, stdout, stderr) = await RunCaptureAsync(executable, arguments, project);
            if (exit != 0) throw new InvalidOperationException($"conan install failed ({exit}):{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
            ParseConanGraph(stdout); // validate before saving
            File.WriteAllText(graph, stdout);
            File.WriteAllText(state, fingerprint);
        }
        return ParseConanGraph(File.ReadAllText(graph));
    }

    /// <summary>Reads `conan install --format=json` (Conan 2): host packages' cpp_info, components included.</summary>
    public static PackageSet ParseConanGraph(string json)
    {
        using var document = JsonDocument.Parse(json);
        var set = new PackageSet { Source = "conan" };
        if (!document.RootElement.TryGetProperty("graph", out var graph) || !graph.TryGetProperty("nodes", out var nodes))
            throw new InvalidOperationException("Unexpected Conan output: graph.nodes is missing (Conan 2 is required).");
        foreach (var node in nodes.EnumerateObject().OrderBy(n => int.TryParse(n.Name, out int i) ? i : int.MaxValue).Select(n => n.Value))
        {
            if (node.TryGetProperty("context", out var context) && context.GetString() == "build") continue;
            if (!node.TryGetProperty("cpp_info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
            string? packageFolder = node.TryGetProperty("package_folder", out var folder) ? folder.GetString() : null;
            if (string.IsNullOrEmpty(packageFolder)) continue; // the consumer (conanfile) itself
            foreach (var component in info.EnumerateObject())
            {
                var item = component.Value;
                if (item.ValueKind != JsonValueKind.Object) continue;
                void Collect(string property, List<string> into, bool path)
                {
                    if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
                    foreach (var value in values.EnumerateArray())
                        if (value.GetString() is { Length: > 0 } text)
                        {
                            string entry = path ? Path.GetFullPath(text, packageFolder) : text;
                            if (!into.Contains(entry, StringComparer.Ordinal)) into.Add(entry);
                        }
                }
                Collect("includedirs", set.IncludeDirectories, true);
                Collect("libdirs", set.LibraryDirectories, true);
                Collect("bindirs", set.BinaryDirectories, true);
                Collect("libs", set.Libraries, false);
                Collect("system_libs", set.SystemLibraries, false);
                Collect("defines", set.Definitions, false);
            }
        }
        return set;
    }

    private static async Task<int> ClangMajorAsync(AndroidPlatform android)
    {
        var (exit, stdout, _) = await RunCaptureAsync(android.Tool("clang++"), ["--version"], Environment.CurrentDirectory);
        var match = Regex.Match(stdout, @"clang version (\d+)");
        if (exit != 0 || !match.Success) throw new InvalidOperationException("Could not read the NDK clang version for the Conan profile.");
        return int.Parse(match.Groups[1].Value);
    }

    // ---------------------------------------------------------------- processes

    private static async Task<(int Exit, string Output)> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var (exit, stdout, stderr) = await RunCaptureAsync(executable, arguments, workingDirectory, environment, echo: true);
        return (exit, stdout + stderr);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null, bool echo = false)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value;
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new FileNotFoundException($"Could not start {executable}: {ex.Message}", executable, ex); }
        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (stdout) stdout.AppendLine(e.Data); if (echo) Log.TraceVerbose(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (stderr) stderr.AppendLine(e.Data); if (echo) Log.TraceVerbose(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}

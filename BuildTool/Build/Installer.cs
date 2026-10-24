using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>What a successful build produced, written beside the target's build trace for install/package.</summary>
public sealed record InstallManifest(string Target, TargetType Type, string Platform, string Configuration,
    List<string> Products, List<string> Symbols, List<string> Runtime, List<InstallManifest.HeaderRoot> Headers, string Binaries, ExportInfo? Export = null)
{
    public sealed record HeaderRoot(string Root, List<string> Files);

    private static readonly string[] HeaderExtensions = [".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".inc", ".tcc", ".tpp"];

    public static string PathFor(string buildPath) => Path.Combine(buildPath, "Install.json");

    internal static void Write(ProjectModel model, BuildTarget target, Platform platform, TargetConfiguration configuration,
        string buildPath, string outputPath, string baseName, LinkEnvironment link, List<string> runtime, BuildStepPlan steps)
    {
        var products = new List<string>();
        var symbols = new List<string>();
        string Output(string extension) => Path.Combine(outputPath, baseName + extension);
        switch (target.Rules.Type)
        {
            case TargetType.StaticLibrary:
                products.Add(Output(platform.StaticLibraryFileExtension));
                break;
            case TargetType.SharedLibrary when platform.Type == PlatformType.Windows:
                products.Add(Output(".dll"));
                if (File.Exists(Output(".lib"))) products.Add(Output(".lib")); // a DLL without exports has no import library
                symbols.Add(Output(".pdb"));
                break;
            case TargetType.Application when platform.Type == PlatformType.Windows:
                products.Add(Output(".exe"));
                symbols.Add(Output(".pdb"));
                break;
            case TargetType.Application when platform.Type == PlatformType.Mac && !target.Rules.ConsoleApplication && !target.Rules.IsTest:
                products.Add(MacPlatform.BundlePath(outputPath, baseName)); // the .app directory
                symbols.Add(MacPlatform.BundlePath(outputPath, baseName) + ".dSYM");
                break;
            case TargetType.Application when platform.Type != PlatformType.Android || target.Rules.IsTest:
                // Linux/macOS programs and Android test executables.
                products.Add(platform.ExecutablePath(outputPath, baseName));
                if (platform.Type == PlatformType.Mac) symbols.Add(MacPlatform.DsymPath(products[^1]));
                break;
            default: // .so/.dylib; an Android application is its .so, installed as the APK (written by the packager)
                products.Add(Output(platform.SharedLibraryFileExtension));
                if (platform.Type == PlatformType.Mac) symbols.Add(MacPlatform.DsymPath(products[^1]));
                break;
        }
        // Library targets publish their own modules' public headers.
        var headers = new List<HeaderRoot>();
        if (target.Rules.Type != TargetType.Application)
            foreach (var module in model.OwnModules(target))
                foreach (string include in module.PublicIncludePaths.Concat(module.PublicSystemIncludePaths))
                {
                    string root = ProjectModel.ResolvePath(module, steps.Expand(module, include));
                    if (!Directory.Exists(root)) continue;
                    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(f => HeaderExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    if (files.Count > 0) headers.Add(new(root, files));
                }
        var manifest = new InstallManifest(target.Name, target.Rules.Type, platform.PlatformName, configuration.ToString(),
            products, symbols.Where(s => File.Exists(s) || Directory.Exists(s)).ToList(), runtime.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), headers, outputPath,
            target.Rules.Type == TargetType.Application ? null : ExportInfo.Create(model, target, platform, link));
        File.WriteAllText(PathFor(buildPath), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>test, install and package commands.</summary>
public static class Installer
{
    /// <summary>
    /// Builds the targets (all non-test targets by default) and copies them into a prefix:
    /// bin/ (EXE, DLL, runtime files), lib/ (LIB, import libraries; lib/&lt;abi&gt;/ for Android),
    /// include/ (public headers of library targets), and the APK of an Android application.
    /// </summary>
    public static async Task<List<string>> InstallAsync(string project, IReadOnlyList<Platform> platforms, TargetConfiguration configuration,
        IReadOnlyCollection<string> targets, string prefix, bool symbols)
    {
        string projectName = new DirectoryInfo(project).Name;
        prefix = Path.GetFullPath(prefix);
        var names = ResolveTargets(project, platforms[0], configuration, targets, tests: false);
        var installed = new List<string>();
        foreach (string name in names)
        {
            await new GenericProjectBuilder().BuildAsync(project, projectName, platforms, configuration, name);
            bool apkInstalled = false;
            InstallManifest? exported = null;
            foreach (var platform in platforms)
            {
                string manifestPath = InstallManifest.PathFor(Path.Combine(project, "Intermediate", "Build", platform.PlatformName, configuration.ToString(), name));
                var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath))
                    ?? throw new InvalidDataException($"Invalid install manifest: {manifestPath}");
                bool android = platform is AndroidPlatform;
                string abi = (platform as AndroidPlatform)?.Abi ?? "";
                if (android && manifest.Type == TargetType.Application)
                {
                    if (apkInstalled) continue; // one APK holds every ABI
                    string apk = AndroidApplication.Apk(project, manifest.Target, configuration);
                    installed.Add(Copy(apk, Path.Combine(prefix, Path.GetFileName(apk))));
                    apkInstalled = true;
                    continue;
                }
                foreach (string product in manifest.Products)
                {
                    string extension = Path.GetExtension(product).ToLowerInvariant();
                    // Programs and DLLs in bin/; archives, import libraries and Unix shared libraries in lib/
                    // (Linux/macOS programs find them through their rpath $ORIGIN/../lib, @loader_path/../lib).
                    string directory = android ? Path.Combine("lib", abi) : manifest.Type == TargetType.Application || extension is ".exe" or ".dll" ? "bin" : "lib";
                    string destination = Path.Combine(prefix, directory, Path.GetFileName(product));
                    installed.Add(Copy(product, destination));
                }
                if (symbols)
                    foreach (string symbol in manifest.Symbols)
                        installed.Add(Copy(symbol, Path.Combine(prefix, manifest.Type == TargetType.Application || !android ? "bin" : Path.Combine("lib", abi), Path.GetFileName(symbol))));
                foreach (string runtime in manifest.Runtime.Where(File.Exists))
                    installed.Add(Copy(runtime, Path.Combine(prefix, android ? Path.Combine("lib", abi) : "bin", Path.GetRelativePath(manifest.Binaries, runtime))));
                foreach (var root in manifest.Headers)
                    foreach (string header in root.Files)
                        installed.Add(Copy(Path.Combine(root.Root, header), Path.Combine(prefix, "include", header), headers: true));
                exported ??= manifest.Export != null ? manifest : null;
            }
            // CMake and pkg-config files of a library target (Android: one file set for every ABI).
            if (exported?.Export is { } export)
            {
                string library = Path.GetFileName(exported.Products.First(p => !p.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || export.Type == TargetType.StaticLibrary));
                string? importLibrary = exported.Products.Select(Path.GetFileName).FirstOrDefault(p => export.Type == TargetType.SharedLibrary && p!.EndsWith(".lib", StringComparison.OrdinalIgnoreCase));
                installed.AddRange(PackageExport.Write(prefix, export, library, importLibrary));
            }
        }
        // macOS: sign installed dylibs, then bundles as a whole (a bundle's signature covers its contents).
        if (platforms[0] is MacPlatform)
        {
            foreach (string file in installed.Distinct(StringComparer.Ordinal).Where(f => f.EndsWith(".dylib", StringComparison.Ordinal) && File.Exists(f)))
                if (!string.IsNullOrWhiteSpace(BuildConfigManager.Config.Mac.SigningIdentity)) await MacPlatform.SignAsync(file);
            foreach (string bundle in installed.Distinct(StringComparer.Ordinal).Where(f => f.EndsWith(".app", StringComparison.Ordinal) && Directory.Exists(f)))
                await MacPlatform.SignAsync(bundle);
        }
        Log.TraceInfo($"Installed {installed.Distinct(StringComparer.OrdinalIgnoreCase).Count()} files into {prefix}");
        return installed;
    }

    /// <summary>install into a fresh staging directory, then a zip: Binaries/Packages/Project-Platform-Configuration.zip.</summary>
    public static async Task<string> PackageAsync(string project, IReadOnlyList<Platform> platforms, TargetConfiguration configuration,
        IReadOnlyCollection<string> targets, string? output, bool symbols)
    {
        string projectName = new DirectoryInfo(project).Name;
        string label = platforms[0] is AndroidPlatform ? "Android" : platforms[0].PlatformName;
        string staging = Path.Combine(project, "Intermediate", "Package", label + "-" + configuration);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        await InstallAsync(project, platforms, configuration, targets, staging, symbols);
        output = Path.GetFullPath(output ?? Path.Combine(project, "Binaries", "Packages", $"{projectName}-{label}-{configuration}.zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        ZipFile.CreateFromDirectory(staging, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(temporary, output, true);
        Log.TraceInfo($"Package: {output}");
        return output;
    }

    /// <summary>
    /// Builds and runs test targets (TargetRules.IsTest); returns the number of failed tests.
    /// Windows: the EXE runs locally. Android: an executable for the device's ABI is pushed with adb
    /// (with libc++_shared and dependency .so files) and run in /data/local/tmp/beast.
    /// </summary>
    public static async Task<int> TestAsync(string project, IReadOnlyList<Platform> platforms, TargetConfiguration configuration,
        IReadOnlyCollection<string> targets, string? device = null, string? avd = null)
    {
        var platform = platforms[0];
        string adb = "", serial = "";
        if (platform is AndroidPlatform)
        {
            (adb, serial) = await AndroidRunner.ConnectAsync(avd, device);
            string abi = await AndroidRunner.DeviceAbiAsync(adb, serial);
            if (!AndroidPlatform.SupportedAbis.Contains(abi))
                throw new InvalidOperationException($"Device {serial} reports ABI '{abi}'; Beast builds {string.Join(", ", AndroidPlatform.SupportedAbis)}.");
            platform = new AndroidPlatform(abi);
            Log.TraceInfo($"Running Android tests on {serial} ({abi}).");
        }
        else if (!platform.CanRunOnHost)
            throw new InvalidOperationException($"{platform.PlatformName} test executables do not run on this machine. Build them with 'build', run them on a matching host.");
        string projectName = new DirectoryInfo(project).Name;
        var names = ResolveTargets(project, platform, configuration, targets, tests: true);
        if (names.Count == 0) { Log.TraceWarning("No test targets (TargetRules.IsTest = true)."); return 0; }
        var failed = new List<string>();
        int timeout = Math.Max(1, BuildConfigManager.Config.TestTimeoutSeconds);
        foreach (string name in names)
        {
            await new GenericProjectBuilder().BuildAsync(project, projectName, platform, configuration, name);
            ProjectModel model;
            using (await ProjectBuildLock.AcquireAsync(project)) model = ProjectModel.Load(project, platform, configuration);
            var target = model.SelectTarget(name);
            string binaries = Path.Combine(project, "Binaries", platform.PlatformName, configuration.ToString());
            var timer = Stopwatch.StartNew();
            int exit;
            try
            {
                if (platform is AndroidPlatform android)
                {
                    var libraries = model.TargetDependencies(target).Where(t => t.Rules.Type == TargetType.SharedLibrary)
                        .Select(t => Path.Combine(binaries, "lib" + t.Name + ".so")).ToList();
                    if (!target.Rules.StaticRuntime)
                        libraries.Add(Path.Combine(android.ToolchainRoot, "sysroot", "usr", "lib", android.Triple, "libc++_shared.so"));
                    exit = await AndroidRunner.RunTestAsync(adb, serial, Path.Combine(binaries, name), libraries, target.Rules.TestArguments, timeout);
                }
                else exit = await RunLocalAsync(platform.ExecutablePath(binaries, name), binaries, target.Rules.TestArguments, timeout);
            }
            catch (TimeoutException ex)
            {
                Log.TraceError($"FAIL {name}: {ex.Message}");
                failed.Add(name);
                continue;
            }
            if (exit == 0) Log.TraceInfo($"PASS {name} ({timer.Elapsed.TotalSeconds:F2} s)");
            else { Log.TraceError($"FAIL {name}: exit code {exit} ({timer.Elapsed.TotalSeconds:F2} s)"); failed.Add(name); }
        }
        Log.TraceInfo($"Tests: {names.Count - failed.Count} passed, {failed.Count} failed.");
        return failed.Count;
    }

    /// <summary>Runs a program in the foreground (console inherited); returns its exit code.</summary>
    internal static async Task<int> RunLocalAsync(string executable, string workingDirectory, IEnumerable<string> arguments, int timeoutSeconds = 0)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Executable was not built.", executable);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var job = ChildProcessJob.Attach(process);
        if (timeoutSeconds <= 0) { await process.WaitForExitAsync(); return process.ExitCode; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"timed out after {timeoutSeconds} s");
        }
        return process.ExitCode;
    }

    private static List<string> ResolveTargets(string project, Platform platform, TargetConfiguration configuration, IReadOnlyCollection<string> requested, bool tests)
    {
        ProjectModel model;
        using (ProjectBuildLock.AcquireAsync(project).GetAwaiter().GetResult()) model = ProjectModel.Load(project, platform, configuration);
        if (requested.Count > 0) return requested.Select(n => model.SelectTarget(n).Name).ToList();
        return model.Targets.Where(t => tests ? t.Rules.IsTest && t.Rules.Type == TargetType.Application : !t.Rules.IsTest).Select(t => t.Name).ToList();
    }

    private static string Copy(string source, string destination, bool headers = false)
    {
        // Directories: macOS .app and .dSYM bundles (symbolic links inside are recreated, not followed).
        if (Directory.Exists(source))
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            CopyDirectory(new DirectoryInfo(source), destination);
            return destination;
        }
        if (!File.Exists(source)) throw new FileNotFoundException("Build product to install was not found.", source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Two libraries publishing different headers under the same path would silently overwrite each other.
        if (headers && File.Exists(destination) && BuildCacheSession.HashFile(destination) != BuildCacheSession.HashFile(source) &&
            File.GetLastWriteTimeUtc(destination) >= Process.GetCurrentProcess().StartTime.ToUniversalTime())
            throw new InvalidOperationException($"Two installed headers map to {destination}; give their include directories distinct subfolders.");
        File.Copy(source, destination, true);
        return destination;
    }

    private static void CopyDirectory(DirectoryInfo source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            string target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget != null) File.CreateSymbolicLink(target, entry.LinkTarget);
            else if (entry is DirectoryInfo directory) CopyDirectory(directory, target);
            else File.Copy(entry.FullName, target, true);
        }
    }
}

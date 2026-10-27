using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Beast.BuildTool;

/// <summary>
/// Android NDK/Clang backend for one ABI (arm64-v8a or x86_64). It builds a shared library for
/// NativeActivity. Runs on Windows, Linux and macOS hosts.
/// </summary>
public sealed class AndroidPlatform : Platform
{
    public static readonly IReadOnlyList<string> SupportedAbis = ["arm64-v8a", "x86_64"];
    public override string SharedLibraryFileExtension => ".so";
    public override string StaticLibraryFileExtension => ".a";
    /// <summary>arm64-v8a keeps the historical "Android" directory names; other ABIs get a suffix.</summary>
    public override string PlatformName => _x64 ? "Android-x86_64" : "Android";
    public override PlatformArch Architecture => _x64 ? PlatformArch.X64 : PlatformArch.ARM64;
    private readonly bool _x64;
    public string Abi => _x64 ? "x86_64" : "arm64-v8a";
    /// <summary>Clang/sysroot triple: aarch64-linux-android or x86_64-linux-android.</summary>
    public string Triple => (_x64 ? "x86_64" : "aarch64") + "-linux-android";
    /// <summary>ELF e_machine of this ABI's shared objects (EM_AARCH64 = 183, EM_X86_64 = 62).</summary>
    public ushort ElfMachine => (ushort)(_x64 ? 62 : 183);
    public string ToolchainRoot => _toolchainRoot;
    public override PlatformType Type => PlatformType.Android;
    public override string? ModuleInterfaceExtension => ".pcm";
    /// <summary>The NDK's libc++ provides import std when it ships share/libc++/v1/std.cppm.</summary>
    public override string? StandardModuleSource(string name) => UnixPlatform.LibcxxModuleSource(_clang, name);
    public override string StandardModuleUnsupportedReason => "this NDK does not ship libc++'s std.cppm (share/libc++/v1)";

    private readonly string _toolchainRoot;
    private readonly string _clang;
    private readonly int _apiLevel;
    public string NdkRoot { get; }
    /// <summary>API level of the --target triple (android.minSdk).</summary>
    public int ApiLevel => _apiLevel;
    public string Tool(string name) => Path.Combine(_toolchainRoot, "bin", HostPlatform.Executable(name));

    public static string NormalizeAbi(string value) => value.ToLowerInvariant() switch
    {
        "arm64-v8a" or "arm64" or "aarch64" => "arm64-v8a",
        "x86_64" or "x64" or "x86-64" => "x86_64",
        _ => throw new ArgumentException($"Unsupported Android ABI '{value}'. Supported: {string.Join(", ", SupportedAbis)}.")
    };

    public AndroidPlatform(string abi = "arm64-v8a")
    {
        _x64 = NormalizeAbi(abi) == "x86_64";
        var android = BuildConfigManager.Config.Android;
        _apiLevel = android.MinSdk;
        NdkRoot = ResolveNdk(android.NdkRoot, android.NdkVersion);
        _toolchainRoot = Path.Combine(NdkRoot, "toolchains", "llvm", "prebuilt", HostPlatform.NdkHostTag);
        _clang = Tool("clang++");
        if (!File.Exists(_clang))
            throw new FileNotFoundException("Android NDK Clang was not found.", _clang);

        SystemIncludePaths.Add(Path.Combine(_toolchainRoot, "sysroot", "usr", "include"));
        Log.TraceInfo($"Android NDK: {NdkRoot}");
    }

    public override bool IsValid() => File.Exists(_clang);

    public override void SetupCPPEnvironment(CPPEnvironment environment)
    {
        environment.PreprocessorDefinitions.Add("PLATFORM_ANDROID");
        environment.PreprocessorDefinitions.Add("__ANDROID__");
        // Clang derives __ANDROID_API__ from the API-qualified --target.
    }

    public override void SetupLinkEnvironment(LinkEnvironment environment)
    {
        environment.InputLibraries.Add("android");
        environment.InputLibraries.Add("log");
        environment.InputLibraries.Add("c");
        environment.InputLibraries.Add("m");
        environment.InputLibraries.Add("dl");
    }

    public override CPPOutput CompileCppFiles(CommandExecutor executor, CPPEnvironment environment, List<string> sourceFiles, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var output = new CPPOutput();
        bool hasPch = sourceFiles.Any(s => ModuleCompilation.UsesPch(environment, s));
        if (hasPch && environment.PrecompiledHeaderUsage != PrecompiledHeaderFileUsage.CreateManual)
            throw new InvalidOperationException("UseManual PCH is unsupported; let the module create its PCH.");
        string? pch = hasPch ? Path.Combine(outputPath, "BeastPch.pch") : null;
        output.PrecompiledHeaderFile = pch;
        var compileSources = hasPch ? sourceFiles.Prepend(environment.PrecompiledHeaderSource) : sourceFiles;
        foreach (string source in compileSources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool creatingPch = hasPch && source == environment.PrecompiledHeaderSource;
            bool usesPch = !creatingPch && hasPch && ModuleCompilation.UsesPch(environment, source);
            string sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(GlobalEnvironment.WorkspacePath, source))))[..12];
            string objectFile = creatingPch ? pch! : Path.Combine(outputPath, Path.GetFileNameWithoutExtension(source) + "_" + sourceId + ".o");
            if (!creatingPch) output.ObjectFiles.Add(objectFile);
            var args = new List<string> { "-c", "-fPIC", "-ffunction-sections", "-fdata-sections", TargetArgument(), "--sysroot=\"" + Path.Combine(_toolchainRoot, "sysroot") + "\"" };
            args.Add(!environment.Optimization ? "-O0" : environment.FavorSizeOrSpeed switch
            {
                FavorSizeOrSpeed.SmallCode => "-Os", FavorSizeOrSpeed.FastCode => "-O3", _ => "-O2"
            });
            // Retain line information in optimized originals; strip deployment copies only.
            args.Add("-g");
            bool portable = BuildConfigManager.Config.Acceleration.PortableClangCache;
            if (portable)
            {
                foreach (var mapping in new[] { (GlobalEnvironment.WorkspacePath, "/__beast_project"), (NdkRoot, "/__beast_ndk") })
                    foreach (string spelling in new[] { mapping.Item1, mapping.Item1.Replace('\\', '/') }.Distinct())
                        args.Add($"-ffile-prefix-map=\"{spelling}={mapping.Item2}\"");
                args.Add("-fdebug-compilation-dir=/__beast_project");
            }
            bool isC = Path.GetExtension(source).Equals(".c", StringComparison.OrdinalIgnoreCase);
            // .S is preprocessed (includes, macros, depfile); .s is passed to the assembler as is.
            string extension = Path.GetExtension(source);
            bool isAssembly = extension is ".S" or ".s";
            bool hasDependencies = !isAssembly || extension == ".S";
            if (isAssembly) args.AddRange(["-x", extension == ".S" ? "assembler-with-cpp" : "assembler"]);
            else if (isC) args.AddRange(["-x", "c", environment.CStandard == CLanguageStandard.C17 ? "-std=c17" : "-std=c11"]);
            else
            {
                args.Add(environment.CppVersion switch
                {
                    CppVersion.Cpp14 => "-std=c++14", CppVersion.Cpp17 => "-std=c++17",
                    CppVersion.Latest => "-std=c++2b", _ => "-std=c++20"
                });
                args.Add(environment.EnableExceptions ? "-fexceptions" : "-fno-exceptions");
                args.Add(environment.RuntimeTypeInfo ? "-frtti" : "-fno-rtti");
            }
            if (!isAssembly) AddLanguageOptions(environment, args, creatingPch);
            if (creatingPch) args.Add("-x c++-header -Xclang -fno-pch-timestamp");
            if (usesPch) args.Add($"-include-pch \"{pch}\"");
            foreach (string define in environment.PreprocessorDefinitions) args.Add("-D" + CommandUtilities.QuoteArgument(define));
            foreach (string include in environment.IncludePaths.Concat(SystemIncludePaths).Where(Directory.Exists)) args.Add("-I\"" + include + "\"");
            foreach (string include in environment.ExternalIncludePaths.Where(Directory.Exists)) args.Add("-isystem \"" + include + "\"");
            args.AddRange(environment.CustomArgs);
            string dependencyFile = objectFile + ".d";
            if (hasDependencies) args.Add("-MD -MF \"" + dependencyFile + "\"");
            var produced = hasDependencies ? new List<string> { objectFile, dependencyFile } : [objectFile];
            if (environment.ModuleUnits.GetValueOrDefault(source) is { } moduleUnit) CppModules.AddClangArguments(source, moduleUnit, args, produced);
            args.Add("-o \"" + objectFile + "\"");
            args.Add("\"" + source + "\"");
            var prerequisites = new List<string> { source, typeof(AndroidPlatform).Assembly.Location };
            prerequisites.AddRange(environment.AdditionalPrerequisites);
            prerequisites.AddRange(environment.SourcePrerequisites.GetValueOrDefault(source) ?? []);
            if (usesPch) prerequisites.Add(pch!);
            executor.AddCommand(new CommandContext(_clang, string.Join(" ", args), creatingPch ? CommandType.GeneratePCH : CommandType.Compile,
                produced, prerequisites, true,
                creatingPch ? $"Creating PCH {Path.GetFileName(source)} for {Abi}..." : $"Compiling {Path.GetFileName(source)} for {Abi}...",
                dependencyFile: hasDependencies ? dependencyFile : null, dependencyFormat: hasDependencies ? DependencyFormat.Makefile : DependencyFormat.None,
                searchDirectories: hasDependencies ? environment.IncludePaths.Concat(environment.ExternalIncludePaths).Concat(SystemIncludePaths).Append(Path.GetDirectoryName(source)!).Append(Path.Combine(_toolchainRoot, "lib", "clang")) : [],
                portableToolchainRoot: portable && !environment.PreprocessorDefinitions.Any(d => d.IndexOfAny([':', '/', '\\']) >= 0) ? NdkRoot : null,
                allowArtifactCache: !creatingPch && !usesPch));
        }
        return output;
    }

    private static void AddLanguageOptions(CPPEnvironment environment, List<string> args, bool creatingPch)
    {
            args.AddRange(environment.Warnings switch
            {
                WarningLevel.Off => ["-w"],
                WarningLevel.High => ["-Wall", "-Wextra"],
                WarningLevel.Pedantic => ["-Wall", "-Wextra", "-Wpedantic"],
                _ => Array.Empty<string>()
            });
            if (environment.TreatWarningsAsErrors) args.Add("-Werror");
            if (environment.Sanitizers != Sanitizer.None)
            {
                args.Add("-fsanitize=" + SanitizerSupport.ClangList(environment.Sanitizers));
                args.Add("-fno-omit-frame-pointer");
                // UBSan alone uses the minimal static runtime: nothing to package.
                if (!environment.Sanitizers.HasFlag(Sanitizer.Address)) args.Add("-fsanitize-minimal-runtime");
            }
            if (environment.WholeProgramOptimization && environment.Optimization && !creatingPch) args.Add("-flto=thin");
            if (environment.FloatingPoint == FloatingPointModel.Fast) args.Add("-ffast-math");
            else if (environment.FloatingPoint == FloatingPointModel.Strict) args.Add("-ffp-model=strict");
    }

    public override void LinkFiles(CommandExecutor executor, LinkEnvironment environment, string outputFilePath)
    {
        // Applications and shared libraries are .so files (NativeActivity loads them); test targets
        // are position-independent executables pushed to a device with adb.
        bool executable = environment.Output == LinkerOutput.Executable;
        string output = executable || outputFilePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ? outputFilePath : outputFilePath + ".so";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var args = new List<string> { executable ? "-pie" : "-shared", "-fPIC", TargetArgument(), "--sysroot=\"" + Path.Combine(_toolchainRoot, "sysroot") + "\"", "-Wl,--gc-sections", "-Wl,--no-undefined", "-o \"" + output + "\"" };
        args.Add("-Wl,--build-id=sha1");
        // Consumers record this name (not the build path) in DT_NEEDED, so the APK can load it.
        if (!executable) args.Add("-Wl,-soname," + Path.GetFileName(output));
        if (environment.StaticRuntime) args.Add("-static-libstdc++");
        if (environment.Sanitizers != Sanitizer.None)
        {
            args.Add("-fsanitize=" + SanitizerSupport.ClangList(environment.Sanitizers));
            if (!environment.Sanitizers.HasFlag(Sanitizer.Address)) args.Add("-fsanitize-minimal-runtime");
        }
        if (environment.LinkTimeCodeGeneration) args.Add("-flto=thin");
        args.AddRange(environment.CustomArgs);
        args.AddRange(environment.InputFiles.Select(x => "\"" + x + "\""));
        foreach (string path in environment.LibraryPaths) args.Add("-L\"" + path + "\"");
        foreach (string library in environment.InputLibraries) args.Add(library.IndexOfAny(['/', '\\']) >= 0 ? "\"" + library + "\"" : "-l" + library);
        executor.AddCommand(new CommandContext(_clang, string.Join(" ", args), CommandType.Link, new List<string> { output }, environment.InputFiles.Concat(environment.InputLibraries.Where(p => File.Exists(p) || Path.IsPathFullyQualified(p))).ToList(), false, $"Linking {Path.GetFileName(output)}...",
            contentDirectories: environment.LibraryPaths.Append(Path.Combine(_toolchainRoot, "sysroot", "usr", "lib", Triple)).Append(Path.Combine(_toolchainRoot, "lib", "clang"))));
    }

    public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string args = "rcs \"" + outputPath + "\" " + string.Join(" ", objects.Select(x => "\"" + x + "\""));
        string response = CommandUtilities.WriteResponseFile(outputPath, args);
        executor.AddCommand(new CommandContext(Tool("llvm-ar"), $"@\"{response}\"",
            CommandType.Link, new List<string> { outputPath }, objects.Append(response).ToList(), false, $"Archiving {Path.GetFileName(outputPath)}...", recreateOutputs: true));
    }

    private string TargetArgument() => "--target=" + (_x64 ? "x86_64" : "aarch64") + "-linux-android" + _apiLevel;

    private static string ResolveNdk(string configuredRoot, string configuredVersion)
    {
        string root = Environment.ExpandEnvironmentVariables(configuredRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Android NDK path does not exist: {root}");
        if (File.Exists(Path.Combine(root, "source.properties"))) return root;
        var candidates = Directory.GetDirectories(root).OrderByDescending(Path.GetFileName).ToList();
        if (!string.IsNullOrWhiteSpace(configuredVersion)) candidates = candidates.Where(x => string.Equals(Path.GetFileName(x), configuredVersion, StringComparison.OrdinalIgnoreCase)).ToList();
        string? selected = candidates.FirstOrDefault(x => File.Exists(Path.Combine(x, "source.properties")));
        return selected ?? throw new DirectoryNotFoundException($"No Android NDK installation was found in: {root}");
    }
}

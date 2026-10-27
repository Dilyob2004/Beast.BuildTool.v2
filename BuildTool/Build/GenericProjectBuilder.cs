using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Beast.BuildTool;

public sealed class GenericProjectBuilder
{
    public Task BuildAsync(string projectPath, string projectName, Platform platform, TargetConfiguration configuration, string? targetName = null) =>
        BuildAsync(projectPath, projectName, [platform], configuration, targetName);

    /// <summary>
    /// Builds a target (and its dependency targets) for one or more platforms, e.g. several Android
    /// ABIs. An Android application is packaged once, after every ABI is built.
    /// </summary>
    public async Task BuildAsync(string projectPath, string projectName, IReadOnlyList<Platform> platforms, TargetConfiguration configuration, string? targetName = null)
    {
        var plans = new List<(Platform Platform, ProjectModel Model, BuildTarget Target)>();
        // The project lock covers rules compilation only; targets and configurations build in parallel.
        using (await ProjectBuildLock.AcquireAsync(projectPath))
            foreach (var platform in platforms)
            {
                // Rules are evaluated per platform/architecture (ModulesInfo.Architecture differs by ABI).
                var model = ProjectModel.Load(projectPath, platform, configuration);
                plans.Add((platform, model, model.SelectTarget(targetName)));
            }
        using var buildLock = await ProjectBuildLock.AcquireBuildAsync(projectPath, exclusive: false);
        var lockNames = new List<string>();
        foreach (var (platform, model, target) in plans)
        {
            var closure = model.TargetDependencies(target).Append(target).ToList();
            lockNames.AddRange(closure.Select(t => TargetLockName(platform.PlatformName, configuration, t.Name)));
            if (platform.Type == PlatformType.Android && target.Rules.Type == TargetType.Application && !target.Rules.IsTest) lockNames.Add("AndroidPackage-" + target.Name);
        }
        using var targetLocks = await ProjectBuildLock.AcquireTargetsAsync(projectPath, lockNames);
        var androidOutputs = new List<AndroidAbiOutput>();
        foreach (var (platform, model, target) in plans)
        {
            await PackageManagers.PrepareAsync(model, platform, configuration);
            // Dependency targets first, in the same invocation and model: their modules are compiled
            // once, by their own target, and their generated paths are already expanded for consumers.
            foreach (var dependency in model.TargetDependencies(target))
                await BuildTargetAsync(model, dependency, projectPath, projectName, platform, configuration);
            if (await BuildTargetAsync(model, target, projectPath, projectName, platform, configuration) is { } output) androidOutputs.Add(output);
        }
        if (androidOutputs.Count > 0) await AndroidPackager.PackageAsync(projectPath, androidOutputs[0].Target, configuration, androidOutputs);
        if (MacPlatform.IsUniversal(platforms)) await MacPlatform.MergeUniversalAsync(projectPath, configuration);
    }

    internal static string TargetLockName(string platform, TargetConfiguration configuration, string target) => $"{platform}-{configuration}-{target}";

    /// <summary>What a consumer links: the archive, the import library (Windows) or the shared library.</summary>
    internal static string TargetOutput(Platform platform, string outputPath, BuildTarget target) => Path.Combine(outputPath,
        platform.LibraryPrefix + target.Name + (target.Rules.Type == TargetType.StaticLibrary
            ? platform.StaticLibraryFileExtension
            : platform.Type == PlatformType.Windows ? ".lib" /* import library */ : platform.SharedLibraryFileExtension));

    private static async Task<AndroidAbiOutput?> BuildTargetAsync(ProjectModel model, BuildTarget target, string projectPath, string projectName, Platform platform, TargetConfiguration configuration)
    {
        var modules = model.OwnModules(target);
        // Android test targets are plain executables run through adb, not APKs.
        bool androidApplication = platform.Type == PlatformType.Android && target.Rules.Type == TargetType.Application && !target.Rules.IsTest;
        Log.TraceInfo($"Building target {target.Name} ({target.Rules.Type}), {platform.PlatformName} {configuration}");
        // An external project's target builds into this project's Intermediate/.../External/<name>/<target>.
        string buildPath = model.BuildDirectory(platform.PlatformName, configuration, target.Name);
        string outputPath = Path.Combine(projectPath, "Binaries", platform.PlatformName, configuration.ToString());
        var executor = new CommandExecutor(BuildConfigManager.Config.MaxParallelJobs, Path.Combine(buildPath, "BuildTrace.json"));
        var steps = new BuildStepPlan(projectPath, buildPath, outputPath, platform.PlatformName, configuration.ToString(), target.Name, modules);
        steps.AddGenerators(executor);
        var link = CreateLinkEnvironment(platform, configuration, modules);
        link.LinkAsConsoleProgram = target.Rules.ConsoleApplication;
        link.StaticRuntime = target.Rules.StaticRuntime;
        link.Sanitizers = SanitizerSupport.Validate(platform.Type, target.Rules.Sanitizers, target.Name);
        link.LinkTimeCodeGeneration = target.Rules.LinkTimeOptimization && configuration != TargetConfiguration.Debug;
        platform.LinkTimeOptimization = link.LinkTimeCodeGeneration;
        // MSVC AddressSanitizer does not support incremental linking.
        if (link.Sanitizers.HasFlag(Sanitizer.Address)) link.UseIncrementalLinking = false;
        foreach (var module in modules) link.CustomArgs.AddRange(module.LinkerArguments.Select(a => steps.Expand(module, a)));
        link.CustomArgs.AddRange(target.Rules.LinkerArguments);
        if (!target.Legacy)
        {
            link.InputLibraries.Clear();
            platform.SetupLinkEnvironment(link);
            foreach (var module in modules)
                link.InputLibraries.AddRange(module.InputLibraries.Select(p => steps.Expand(module, p)).Select(p => p.IndexOfAny(['/', '\\']) >= 0 ? ProjectModel.ResolvePath(module, p) : p));
        }
        if (platform.Type is PlatformType.Mac or PlatformType.IOS)
            foreach (var module in modules) link.InputLibraries.AddRange(module.Frameworks.Select(f => "-framework " + f));
        var modulePlan = CppModulePlan.Create(model, target, platform, configuration);
        var objectsByModule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var archives = new List<string>();
        foreach (var module in modules)
        {
            var sources = model.Sources(module);
            sources.AddRange(steps.GeneratedSources(module));
            sources.AddRange(steps.AdditionalSources(module).Where(f => !sources.Contains(f, StringComparer.OrdinalIgnoreCase)));
            if (sources.Count == 0) continue;
            var cpp = model.CompileEnvironment(target, module, platform, configuration);
            steps.ConfigureCompilation(model, module, cpp);
            modulePlan.Apply(module, cpp, sources);
            sources = ModuleCompilation.Prepare(module, cpp, sources, Path.Combine(buildPath, "Generated", module.ModuleName), steps.DeclaredOutputs);
            var objects = platform.CompileCppFiles(executor, cpp, sources, Path.Combine(buildPath, "Objects", module.ModuleName));
            objectsByModule.Add(module.ModuleName, objects.ObjectFiles.ToList());
            // Root modules supply entry points directly (NativeActivity exports must not be discarded).
            if (target.Rules.Type == TargetType.StaticLibrary || target.Rules.Modules.Contains(module.ModuleName))
                link.InputFiles.AddRange(objects.ObjectFiles);
            else
            {
                string archive = Path.Combine(buildPath, "Libraries", module.ModuleName + platform.StaticLibraryFileExtension);
                platform.ArchiveFiles(executor, objects.ObjectFiles.ToList(), archive);
                archives.Add(archive);
            }
        }
        if (objectsByModule.Count == 0) throw new InvalidOperationException($"Target '{target.Name}' has no C/C++ sources.");
        // import std: the standard library modules this target provides, linked like its own objects.
        link.InputFiles.AddRange(modulePlan.CompileStandardModules(platform, executor, buildPath));
        // Dependents precede their dependencies for one-pass static archive resolution on Android.
        archives.Reverse();
        link.InputFiles.AddRange(archives);
        // Like external libraries, dependency targets are not merged into a static library target:
        // its consumer links them. EXE/DLL/.so targets link them here.
        var packageModules = modules.ToList();
        if (target.Rules.Type != TargetType.StaticLibrary)
            foreach (var dependency in model.LinkedTargets(target))
            {
                link.InputLibraries.Add(TargetOutput(platform, outputPath, dependency));
                if (dependency.Rules.Type != TargetType.StaticLibrary) continue;
                // A static library does not carry its modules' external libraries or linker options.
                var dependencyPaths = new BuildStepPlan(projectPath, model.BuildDirectory(platform.PlatformName, configuration, dependency.Name), outputPath,
                    platform.PlatformName, configuration.ToString(), dependency.Name, []);
                foreach (var module in model.OwnModules(dependency))
                {
                    packageModules.Add(module);
                    link.InputLibraries.AddRange(module.InputLibraries.Select(p => dependencyPaths.Expand(module, p)).Select(p => p.IndexOfAny(['/', '\\']) >= 0 ? ProjectModel.ResolvePath(module, p) : p));
                    link.DelayLoadLibraries.AddRange(module.DelayLoadLibraries);
                    if (platform.Type is PlatformType.Mac or PlatformType.IOS) link.InputLibraries.AddRange(module.Frameworks.Select(f => "-framework " + f));
                    link.CustomArgs.AddRange(module.LinkerArguments.Select(a => dependencyPaths.Expand(module, a)));
                }
            }
        // vcpkg/Conan libraries; shared ones must ship with the product.
        var shippedPackages = target.Rules.Type == TargetType.StaticLibrary ? []
            : PackageManagers.ApplyLink(projectPath, platform, configuration, packageModules, link);
        if (platform is WindowsPlatform windows && target.Rules.Type == TargetType.Application)
            WindowsResourceCompiler.AddCompileCommand(executor, windows, projectPath, target.Name, configuration, link);
        Directory.CreateDirectory(outputPath);
        bool androidExecutable = platform.Type == PlatformType.Android && target.Rules.Type == TargetType.Application && target.Rules.IsTest;
        // Libraries (and Android applications, which are .so files) are lib<Name> except on Windows.
        string baseName = target.Rules.Type != TargetType.Application || androidApplication ? platform.LibraryPrefix + target.Name : target.Name;
        // Resources of the application and its dependency targets: next to the program, inside the
        // macOS/iOS bundle, or (Android) in the APK's assets through the package plan below.
        var resourceModules = modules.Concat(model.TargetDependencies(target).SelectMany(model.OwnModules)).Distinct().ToList();
        bool macBundle = platform is MacPlatform && target.Rules.Type == TargetType.Application && !target.Rules.ConsoleApplication && !target.Rules.IsTest;
        bool iosBundle = platform is IosPlatform && target.Rules.Type == TargetType.Application && !target.Rules.IsTest;
        string? resourceRoot = target.Rules.Type != TargetType.Application || androidApplication ? null
            : macBundle ? Path.Combine(MacPlatform.BundlePath(outputPath, target.Name), "Contents", "Resources")
            : iosBundle ? IosPlatform.BundlePath(outputPath, target.Name)
            : outputPath;
        if (resourceRoot != null) steps.AddResourceCopies(executor, resourceModules, resourceRoot);
        else if (target.Rules.Type != TargetType.Application && modules.Any(m => m.Resources.Count > 0))
            Log.TraceVerbose($"{target.Name}: Resources are placed by the applications that depend on it.");
        if (target.Rules.Type == TargetType.StaticLibrary)
            platform.ArchiveFiles(executor, link.InputFiles.ToList(), Path.Combine(outputPath, baseName + platform.StaticLibraryFileExtension));
        else
        {
            link.Output = target.Rules.Type == TargetType.SharedLibrary || androidApplication ? LinkerOutput.SharedLibrary : LinkerOutput.Executable;
            string linkOutput = Path.Combine(outputPath, baseName);
            // macOS: a windowed application (ConsoleApplication = false) is an .app bundle.
            if (platform is MacPlatform mac && target.Rules.Type == TargetType.Application && !target.Rules.ConsoleApplication && !target.Rules.IsTest)
            {
                mac.WriteBundleInfo(outputPath, target.Name, target.Rules.Version);
                linkOutput = MacPlatform.BundleExecutable(outputPath, target.Name);
            }
            // iOS: every application is an .app; the program is linked in the build directory and
            // copied into the bundle together with the dylibs of shared dependency targets, then signed.
            bool iosApplication = platform is IosPlatform && target.Rules.Type == TargetType.Application && !target.Rules.IsTest;
            if (iosApplication) linkOutput = Path.Combine(buildPath, "Program", target.Name);
            platform.LinkFiles(executor, link, linkOutput);
            if (iosApplication)
            {
                var embedded = model.TargetDependencies(target).Where(t => t.Rules.Type == TargetType.SharedLibrary)
                    .Select(t => TargetOutput(platform, outputPath, t)).Concat(PackageManagers.RuntimeBinaries(projectPath, platform, configuration)).Distinct().ToList();
                ((IosPlatform)platform).AddBundle(executor, outputPath, buildPath, target.Name, target.Rules.Version, linkOutput, embedded, steps.ResourceOutputs);
            }
        }
        steps.AddRuntimeCopies(executor);
        var runtimeFiles = new List<string>(steps.RuntimeOutputs);
        // Bundle resources travel with the .app (install copies the bundle); others are runtime files.
        List<string> bundleResources = macBundle || iosBundle ? steps.ResourceOutputs : [];
        if (!macBundle && !iosBundle) runtimeFiles.AddRange(steps.ResourceOutputs);
        // The MSVC AddressSanitizer runtime DLL must sit beside the EXE/DLL that loads it.
        if (platform is WindowsPlatform asanPlatform && link.Sanitizers.HasFlag(Sanitizer.Address) && target.Rules.Type != TargetType.StaticLibrary)
            foreach (string runtime in asanPlatform.AddressSanitizerRuntimes())
            {
                BuildStepPlan.AddCopyCommand(executor, runtime, Path.Combine(outputPath, Path.GetFileName(runtime)), outputPath,
                    Path.Combine(buildPath, "RuntimeStates"), $"Copying {Path.GetFileName(runtime)}...");
                runtimeFiles.Add(Path.Combine(outputPath, Path.GetFileName(runtime)));
            }
        if (target.Rules.Type != TargetType.StaticLibrary)
            foreach (string binary in PackageManagers.RuntimeBinaries(projectPath, platform, configuration).DistinctBy(p => Path.GetFileName(p).ToUpperInvariant()))
            {
                string destination = Path.Combine(outputPath, Path.GetFileName(binary));
                BuildStepPlan.AddCopyCommand(executor, binary, destination, outputPath, Path.Combine(buildPath, "RuntimeStates"), $"Copying package runtime {Path.GetFileName(binary)}...");
                runtimeFiles.Add(destination);
            }
        AndroidPackagePlan? package = null;
        var sharedDependencies = new List<string>();
        if (androidApplication)
        {
            AndroidApplication.Ensure(projectPath, target.Name);
            // APK files (AndroidAssets, AndroidNativeLibraries, Resources) come from the application's
            // modules and those of its dependency targets, including external projects' targets.
            foreach (var dependency in model.TargetDependencies(target))
                if (dependency.Rules.Type == TargetType.SharedLibrary) sharedDependencies.Add(TargetOutput(platform, outputPath, dependency));
            sharedDependencies.AddRange(shippedPackages.Distinct(StringComparer.OrdinalIgnoreCase));
            package = new AndroidPackagePlan(buildPath, target.Name, modules, steps, (AndroidPlatform)platform, resourceModules);
            package.AddCopies(executor);
        }
        if (BuildConfigManager.Config.GenerateCompileCommands)
            CompileCommandsGenerator.Write(projectPath, buildPath, executor.GetResults());
        await executor.ExecuteAsync();
        RemoveStaleRuntimeFiles(buildPath, outputPath, runtimeFiles.Concat(bundleResources).ToList());
        InstallManifest.Write(model, target, platform, configuration, buildPath, outputPath, baseName, link, runtimeFiles, steps);

        return platform is AndroidPlatform android && androidApplication
            ? new AndroidAbiOutput(android, target.Name, Path.Combine(outputPath, baseName + ".so"), package!, sharedDependencies, target.Rules.StaticRuntime, link.Sanitizers)
            : null;
    }

    /// <summary>
    /// Deletes runtime files this target copied into Binaries earlier but no longer declares
    /// (removed RuntimeDependencies, packages, ASan). A file another target of the same
    /// platform/configuration still copies is kept.
    /// </summary>
    private static void RemoveStaleRuntimeFiles(string buildPath, string outputPath, List<string> current)
    {
        string list = Path.Combine(buildPath, "RuntimeFiles.txt");
        var keep = current.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(list))
        {
            var others = Directory.GetDirectories(Path.GetDirectoryName(buildPath)!)
                .Where(d => !Path.GetFullPath(d).Equals(Path.GetFullPath(buildPath), StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "RuntimeFiles.txt")).Where(File.Exists)
                .SelectMany(File.ReadAllLines).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath)) + Path.DirectorySeparatorChar;
            foreach (string stale in File.ReadAllLines(list).Where(f => f.Length > 0 && !keep.Contains(f) && !others.Contains(f)))
                if (stale.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(stale))
                {
                    File.Delete(stale);
                    string state = stale + ".state.json";
                    if (File.Exists(state)) File.Delete(state);
                    Log.TraceInfo($"Removed runtime file no longer declared: {Path.GetRelativePath(outputPath, stale)}");
                }
        }
        Directory.CreateDirectory(buildPath);
        File.WriteAllLines(list, keep.Order(StringComparer.OrdinalIgnoreCase));
    }

    internal static CPPEnvironment CreateCppEnvironment(Platform platform, TargetConfiguration configuration, IEnumerable<ModuleRules> modules)
    {
        var environment = new CPPEnvironment { CppVersion = CppVersion.Cpp20 };
        platform.SetupCPPEnvironment(environment);
        foreach (ModuleRules module in modules)
        {
            environment.IncludePaths.AddRange(module.PublicIncludePaths);
            environment.IncludePaths.AddRange(module.PrivateIncludePaths);
            environment.PreprocessorDefinitions.AddRange(module.Definitions);
            environment.PreprocessorDefinitions.AddRange(module.PublicDefinitions);
        }
        // Debug: no optimization, debug CRT, run-time checks. Development: optimized with asserts
        // (no NDEBUG). Release: optimized, NDEBUG. Every configuration has debug information
        // (MSVC: /Z7 objects and a linker PDB; Clang: -g, stripped only in the APK copy).
        environment.DebugInformation = true;
        foreach (string definition in ConfigurationDefinitions(configuration)) environment.PreprocessorDefinitions.Add(definition);
        if (configuration == TargetConfiguration.Debug)
        {
            environment.UseDebugCRT = platform.Type == PlatformType.Windows;
            environment.RuntimeChecks = platform.Type == PlatformType.Windows;
        }
        else environment.Optimization = true;
        return environment;
    }

    /// <summary>Preprocessor definitions that identify the configuration (also used for IntelliSense).</summary>
    internal static IEnumerable<string> ConfigurationDefinitions(TargetConfiguration configuration) => configuration switch
    {
        TargetConfiguration.Debug => ["DEBUG", "_DEBUG", "BEAST_BUILD_DEBUG=1"],
        TargetConfiguration.Development => ["BEAST_BUILD_DEVELOPMENT=1"],
        _ => ["NDEBUG", "BEAST_BUILD_RELEASE=1"]
    };

    internal static LinkEnvironment CreateLinkEnvironment(Platform platform, TargetConfiguration configuration, IEnumerable<ModuleRules> modules)
    {
        var environment = new LinkEnvironment { DebugInformation = true, Optimization = configuration != TargetConfiguration.Debug, UseIncrementalLinking = configuration == TargetConfiguration.Debug };
        platform.SetupLinkEnvironment(environment);
        foreach (ModuleRules module in modules)
        {
            environment.InputLibraries.AddRange(module.InputLibraries);
            environment.DelayLoadLibraries.AddRange(module.DelayLoadLibraries);
        }
        return environment;
    }
}

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
        var plans = LoadPlans(projectPath, platforms, configuration, targetName, out var locks);
        using var _ = locks;
        // One build graph for everything this invocation builds: the target, its dependency targets
        // and every platform/ABI. Targets are planned dependencies first (their modules' rules are
        // expanded by their own target), then all actions run as one DAG: a consumer's compilation
        // only waits for what it reads (generated headers), its link for the libraries it links.
        var first = plans[0];
        var executor = new CommandExecutor(BuildConfigManager.Config.MaxParallelJobs,
            Path.Combine(first.Model.BuildDirectory(first.Platform.PlatformName, configuration, first.Target.Name), "BuildGraph.json"));
        var planned = await PlanAsync(executor, plans, projectPath, projectName, configuration);
        foreach (var item in planned) executor.AddTrace(item.TracePath, item.Actions);
        await executor.ExecuteAsync();
        var androidOutputs = new List<AndroidAbiOutput>();
        foreach (var item in planned)
        {
            item.Finish();
            if (item.Android is { } output) androidOutputs.Add(output);
        }
        if (androidOutputs.Count > 0) await AndroidPackager.PackageAsync(projectPath, androidOutputs[0].Target, configuration, androidOutputs);
        if (MacPlatform.IsUniversal(platforms)) await MacPlatform.MergeUniversalAsync(projectPath, configuration);
    }

    /// <summary>
    /// 'beast graph': plans the same graph as build (target, dependency targets, platforms) without
    /// running an action, and describes it as JSON or Graphviz DOT.
    /// </summary>
    public async Task<string> DescribeGraphAsync(string projectPath, string projectName, IReadOnlyList<Platform> platforms, TargetConfiguration configuration, string? targetName, string format)
    {
        if (format is not ("json" or "dot")) throw new ArgumentException($"Unknown graph format '{format}'. Use json or dot.");
        var plans = LoadPlans(projectPath, platforms, configuration, targetName, out var locks);
        using var _ = locks;
        var executor = new CommandExecutor(BuildConfigManager.Config.MaxParallelJobs);
        var planned = await PlanAsync(executor, plans, projectPath, projectName, configuration);
        return BuildGraphDescription.Describe(executor.PlanGraph(), planned.Select(p => (p.Platform.PlatformName + "/" + p.Target.Name, p.Actions)).ToList(), format);
    }

    private sealed class Locks(List<IDisposable> held) : IDisposable
    {
        public void Dispose() { foreach (var item in Enumerable.Reverse(held)) item.Dispose(); }
    }

    private static List<(Platform Platform, ProjectModel Model, BuildTarget Target)> LoadPlans(string projectPath, IReadOnlyList<Platform> platforms,
        TargetConfiguration configuration, string? targetName, out IDisposable locks)
    {
        var plans = new List<(Platform Platform, ProjectModel Model, BuildTarget Target)>();
        // The project lock covers rules compilation only; targets and configurations build in parallel.
        using (ProjectBuildLock.AcquireAsync(projectPath).GetAwaiter().GetResult())
            foreach (var platform in platforms)
            {
                // Rules are evaluated per platform/architecture (ModulesInfo.Architecture differs by ABI).
                var model = ProjectModel.Load(projectPath, platform, configuration);
                plans.Add((platform, model, model.SelectTarget(targetName)));
            }
        // An Android application needs a JDK and the SDK for Gradle: stop now, before compiling, when they are missing.
        if (plans.Any(p => p.Platform is AndroidPlatform && p.Target.Rules.Type == TargetType.Application && !p.Target.Rules.IsTest))
        {
            var (javaHome, sdkRoot) = AndroidToolchain.RequireForPackaging();
            Log.TraceVerbose($"Android JDK: {javaHome}; SDK: {sdkRoot}");
        }
        // Targets lock in the tree they build into: a shared external target in its own project, where
        // that project's own builds and other applications take the same lock.
        var lockNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(projectPath)] = [] };
        foreach (var (platform, model, target) in plans)
        {
            foreach (var item in model.TargetDependencies(target).Append(target))
            {
                string root = Path.GetFullPath(model.TargetRoot(item));
                if (!lockNames.TryGetValue(root, out var names)) lockNames[root] = names = [];
                names.Add(TargetLockName(platform.PlatformName, configuration, item.Name));
            }
            if (platform.Type == PlatformType.Android && target.Rules.Type == TargetType.Application && !target.Rules.IsTest) lockNames[Path.GetFullPath(projectPath)].Add("AndroidPackage-" + target.Name);
        }
        var held = new List<IDisposable>();
        try
        {
            // A fixed order (by path) avoids lock-order inversion between two applications sharing a project.
            foreach (string root in lockNames.Keys.Order(StringComparer.OrdinalIgnoreCase))
                held.Add(ProjectBuildLock.AcquireBuildAsync(root, exclusive: false).GetAwaiter().GetResult());
            foreach (var (root, names) in lockNames.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                held.Add(ProjectBuildLock.AcquireTargetsAsync(root, names).GetAwaiter().GetResult());
        }
        catch { foreach (var item in Enumerable.Reverse(held)) item.Dispose(); throw; }
        locks = new Locks(held);
        return plans;
    }

    /// <summary>
    /// Adds the actions of every requested target and its dependency targets to <paramref name="executor"/>
    /// without running them (build, and 'beast graph' to inspect the plan).
    /// </summary>
    internal static async Task<List<PlannedTarget>> PlanAsync(CommandExecutor executor, IReadOnlyList<(Platform Platform, ProjectModel Model, BuildTarget Target)> plans,
        string projectPath, string projectName, TargetConfiguration configuration)
    {
        var planned = new List<PlannedTarget>();
        foreach (var (platform, model, target) in plans)
        {
            await PackageManagers.PrepareAsync(model, platform, configuration);
            var byTarget = new Dictionary<BuildTarget, PlannedTarget>();
            foreach (var item in model.TargetDependencies(target).Append(target))
            {
                var plan = PlanTarget(executor, model, item, projectPath, projectName, platform, configuration, byTarget);
                byTarget[item] = plan;
                planned.Add(plan);
            }
        }
        return planned;
    }

    internal static string TargetLockName(string platform, TargetConfiguration configuration, string target) => $"{platform}-{configuration}-{target}";

    /// <summary>What a consumer links: the archive, the import library (Windows) or the shared library.</summary>
    internal static string TargetOutput(Platform platform, string outputPath, BuildTarget target) => Path.Combine(outputPath,
        platform.LibraryPrefix + target.Name + (target.Rules.Type == TargetType.StaticLibrary
            ? platform.StaticLibraryFileExtension
            : platform.Type == PlatformType.Windows ? ".lib" /* import library */ : platform.SharedLibraryFileExtension));

    /// <summary>A target's part of the build graph, and what to do with its products after the graph ran.</summary>
    internal sealed class PlannedTarget(BuildTarget target, Platform platform, string tracePath, IReadOnlyList<CommandContext> actions,
        IReadOnlyList<string> finalOutputs, AndroidAbiOutput? android, BuildStepPlan steps, Action finish)
    {
        public BuildStepPlan Steps { get; } = steps;
        public BuildTarget Target { get; } = target;
        public Platform Platform { get; } = platform;
        /// <summary>BuildTrace.json of the target: its own actions of the shared graph.</summary>
        public string TracePath { get; } = tracePath;
        public IReadOnlyList<CommandContext> Actions { get; } = actions;
        /// <summary>Products a consumer links or loads (EXE/DLL/.so, import library, archive).</summary>
        public IReadOnlyList<string> FinalOutputs { get; } = finalOutputs;
        /// <summary>What a program loads at run time: the shared library and its debug symbols (Windows PDB).</summary>
        public List<string> LoadedProducts { get; } = [];
        public AndroidAbiOutput? Android { get; } = android;
        /// <summary>After a successful graph: stale runtime files, install manifest.</summary>
        public void Finish() => finish();
    }

    private static PlannedTarget PlanTarget(CommandExecutor executor, ProjectModel model, BuildTarget target, string projectPath, string projectName, Platform platform,
        TargetConfiguration configuration, IReadOnlyDictionary<BuildTarget, PlannedTarget> plannedTargets)
    {
        int firstAction = executor.Count;
        // Response and link files of a shared external target live in its own project, as when it builds itself.
        using var planningRoot = GlobalEnvironment.PlanFor(model.TargetRoot(target));
        bool sharedTarget = model.SharedProjectOf(target) != null;
        var modules = model.OwnModules(target);
        // Android test targets are plain executables run through adb, not APKs.
        bool androidApplication = platform.Type == PlatformType.Android && target.Rules.Type == TargetType.Application && !target.Rules.IsTest;
        Log.TraceInfo($"Planning target {target.Name} ({target.Rules.Type}), {platform.PlatformName} {configuration}");
        // An external project's target builds into this project's Intermediate/.../External/<name>/<target>.
        string buildPath = model.BuildDirectory(platform.PlatformName, configuration, target.Name);
        string outputPath = model.BinariesDirectory(platform.PlatformName, configuration, target);
        string BinariesOf(BuildTarget item) => model.BinariesDirectory(platform.PlatformName, configuration, item);
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
            steps.ConfigureCompilation(model, module, cpp, model.TargetDependencies(target).Select(t => plannedTargets.GetValueOrDefault(t)?.Steps).OfType<BuildStepPlan>());
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
                link.InputLibraries.Add(TargetOutput(platform, BinariesOf(dependency), dependency));
                if (dependency.Rules.Type != TargetType.StaticLibrary) continue;
                // A static library does not carry its modules' external libraries or linker options.
                var dependencyPaths = new BuildStepPlan(projectPath, model.BuildDirectory(platform.PlatformName, configuration, dependency.Name), BinariesOf(dependency),
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
        var shippedPackages = target.Rules.Type == TargetType.StaticLibrary || sharedTarget ? []
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
            // The link waits for the products of the targets it links: the archive or import library
            // is an input already; the DLL/.so itself is an ordering edge (an import library is not
            // always declared as an output, and the program needs the DLL beside it to run).
            var linked = model.LinkedTargets(target).Select(t => plannedTargets.GetValueOrDefault(t)).OfType<PlannedTarget>().SelectMany(t => t.FinalOutputs).ToList();
            foreach (var action in executor.GetResults().Skip(firstAction).Where(a => a.Type == CommandType.Link && IsFinalOutput(a)))
                foreach (string product in linked) action.AddOrderDependency(product);
            if (iosApplication)
            {
                var embedded = model.TargetDependencies(target).Where(t => t.Rules.Type == TargetType.SharedLibrary)
                    .Select(t => TargetOutput(platform, BinariesOf(t), t)).Concat(PackageManagers.RuntimeBinaries(projectPath, platform, configuration)).Distinct().ToList();
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
        // Shared libraries of shared external targets are built in their project's Binaries: a copy goes
        // beside this target's products (the program loads them from its directory; rpath $ORIGIN).
        if (target.Rules.Type != TargetType.StaticLibrary && !sharedTarget && platform.Type != PlatformType.Android)
            foreach (var dependency in model.TargetDependencies(target).Where(t => t.Rules.Type == TargetType.SharedLibrary && model.SharedProjectOf(t) != null))
                foreach (string product in plannedTargets.GetValueOrDefault(dependency)?.LoadedProducts ?? [])
                {
                    string destination = Path.Combine(outputPath, Path.GetFileName(product));
                    BuildStepPlan.AddCopyCommand(executor, product, destination, outputPath, Path.Combine(buildPath, "RuntimeStates"), $"Copying {Path.GetFileName(product)} of {dependency.Name}...");
                    runtimeFiles.Add(destination);
                }
        if (target.Rules.Type != TargetType.StaticLibrary && !sharedTarget)
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
                if (dependency.Rules.Type == TargetType.SharedLibrary) sharedDependencies.Add(TargetOutput(platform, BinariesOf(dependency), dependency));
            sharedDependencies.AddRange(shippedPackages.Distinct(StringComparer.OrdinalIgnoreCase));
            package = new AndroidPackagePlan(buildPath, target.Name, modules, steps, (AndroidPlatform)platform, resourceModules);
            package.AddCopies(executor);
        }
        var actions = executor.GetResults().Skip(firstAction).ToList();
        if (BuildConfigManager.Config.GenerateCompileCommands)
            CompileCommandsGenerator.Write(projectPath, buildPath, actions);
        var finalOutputs = actions.Where(a => a.Type == CommandType.Link && IsFinalOutput(a)).SelectMany(a => a.ProducedFiles)
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).ToList();
        var android = platform is AndroidPlatform androidPlatform && androidApplication
            ? new AndroidAbiOutput(androidPlatform, target.Name, Path.Combine(outputPath, baseName + ".so"), package!, sharedDependencies, target.Rules.StaticRuntime, link.Sanitizers)
            : null;
        var result = new PlannedTarget(target, platform, Path.Combine(buildPath, "BuildTrace.json"), actions, finalOutputs, android, steps, () =>
        {
            RemoveStaleRuntimeFiles(buildPath, outputPath, runtimeFiles.Concat(bundleResources).ToList());
            InstallManifest.Write(model, target, platform, configuration, buildPath, outputPath, baseName, link, runtimeFiles, steps);
        });
        if (target.Rules.Type == TargetType.SharedLibrary)
            result.LoadedProducts.AddRange(actions.Where(a => a.Type == CommandType.Link && IsFinalOutput(a)).SelectMany(a => a.ProducedFiles)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb" or ".so" or ".dylib"));
        return result;

        // Products of the target itself, not the per-module archives in its build directory.
        bool IsFinalOutput(CommandContext action) => action.ProducedFiles.Any(f =>
            !Path.GetFullPath(f).StartsWith(Path.Combine(Path.GetFullPath(buildPath), "Libraries") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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

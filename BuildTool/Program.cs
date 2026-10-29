using Beast.BuildTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "version" or "--version" or "-v")
        {
            Console.WriteLine("Beast BuildTool " + Version);
            return 0;
        }
        Log.Init();
        try
        {
            var commandLine = new CommandLine(args);
            foreach (string option in commandLine.UnknownOptions)
                Log.TraceWarning($"Unknown option '--{option}' is ignored. Run 'help' for the list of options.");
            string command = commandLine.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "";
            return command switch
            {
                "new" => CreateProject(commandLine),
                "module" => CreateModule(commandLine),
                "generate" => Generate(commandLine),
                "migrate" => Migrate(commandLine),
                "build" => await Build(commandLine),
                "run" => await Run(commandLine),
                "clean" => Clean(commandLine),
                "test" => await Test(commandLine),
                "watch" => await Watch(commandLine),
                "install" => await Install(commandLine, package: false),
                "package" => await Install(commandLine, package: true),
                "worker" => await SharedQueueWorker.RunAsync(commandLine),
                "help" or "--help" or "-h" or "" => Help(),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Log.TraceError(ex.Message);
            return 1;
        }
        finally
        {
            Log.Cleanup();
        }
    }

    /// <summary>Informational version: "2.0.0" (release tag) or "2.0.0-dev+&lt;commit&gt;".</summary>
    internal static string Version => BuildToolVersion.Informational;

    private static int CreateProject(CommandLine args)
    {
        string? name = args.Positionals.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return Usage("Project name is required.");
        string location = args.HasArgument("path") ? args.GetArgument("path") : Environment.CurrentDirectory;
        string? template = GetOptionalArgument(args, "template");
        return (template != null ? ProjectGenerator.CreateFromTemplate(name, location, template) : ProjectGenerator.CreateProject(name, location)) ? 0 : 1;
    }

    private static int CreateModule(CommandLine args)
    {
        string? name = args.Positionals.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return Usage("Module name is required: beast module <Name> [project-path].");
        bool explicitProject = args.HasArgument("project") || args.Positionals.Count > 2;
        string? project = explicitProject ? ResolveProject(args, 2) : Environment.CurrentDirectory;
        if (project == null || !Directory.Exists(Path.Combine(project, "Source")) ||
            !File.Exists(Path.Combine(project, "Config", "BuildConfig.json")))
            return Usage("Beast project not found. Run this command in the project root (beside its .sln and Source), or supply a project path.");
        if (!ProjectGenerator.CreateModule(project, name)) return 1;
        IdeGenerator.TryGenerate(project);
        return 0;
    }

    private static int Migrate(CommandLine args)
    {
        string? project = ResolveProject(args, 1);
        if (project == null) return Usage("Project path is required.");
        string file = ProjectModel.Migrate(project);
        Log.TraceInfo($"Created {file}. Build it and add module dependencies where compilation reports missing headers or definitions.");
        return 0;
    }

    private static int Generate(CommandLine args)
    {
        string? project = ResolveProject(args, 1);
        if (project == null) return Usage("Project path is required.");
        foreach (string file in IdeGenerator.Generate(project)) Log.TraceInfo("Generated " + file);
        Log.TraceInfo("compile_commands.json (clangd, CLion, VS Code) is written by 'build'.");
        return 0;
    }

    private static async Task<int> Build(CommandLine args)
    {
        var (project, platforms, configuration, targets) = Prepare(args, null);
        if (targets.Count > 1) throw new ArgumentException("build takes one --target; use install/package for several.");
        string? targetName = targets.FirstOrDefault();
        string? runTarget = args.HasFlag("run") ? ResolveRunnable(project, platforms[0], configuration, targetName) : null;
        await new GenericProjectBuilder().BuildAsync(project, new DirectoryInfo(project).Name, platforms, configuration, targetName);
        return runTarget == null ? 0 : await Launch(args, project, platforms[0], configuration, runTarget);
    }

    /// <summary>run: builds (unless --no-build) and starts an application: Windows EXE or Android APK.</summary>
    private static async Task<int> Run(CommandLine args)
    {
        // Like build, run uses --platform or the configured default platform (the host by default).
        var (project, platforms, configuration, targets) = Prepare(args, null);
        if (targets.Count > 1) throw new ArgumentException("run takes one --target.");
        string target = ResolveRunnable(project, platforms[0], configuration, targets.FirstOrDefault());
        if (!args.HasFlag("no-build"))
            await new GenericProjectBuilder().BuildAsync(project, new DirectoryInfo(project).Name, platforms, configuration, target);
        return await Launch(args, project, platforms[0], configuration, target);
    }

    private static async Task<int> Launch(CommandLine args, string project, Platform platform, TargetConfiguration configuration, string target)
    {
        if (platform is AndroidPlatform)
        {
            await AndroidRunner.RunAsync(project, target, configuration, GetOptionalArgument(args, "avd"), GetOptionalArgument(args, "device"), args.HasFlag("wait"));
            return 0;
        }
        if (platform is IosPlatform ios)
        {
            string bundle = IosPlatform.BundlePath(Path.Combine(project, "Binaries", platform.PlatformName, configuration.ToString()), target);
            // A console program (ConsoleApplication, the default) runs in the simulator as a process
            // (simctl spawn: its output and exit code come back); a UIKit app is launched as an app.
            bool console = ProjectModel.Load(project, platform, configuration).Targets.Single(t => t.Name == target).Rules.ConsoleApplication;
            int code = ios.Simulator && console ? await IosRunner.SpawnAsync(IosPlatform.BundleExecutable(Path.GetDirectoryName(bundle)!, target), [], 0)
                : ios.Simulator ? await IosRunner.RunSimulatorAppAsync(bundle, IosPlatform.BundleIdentifier(target))
                : await IosRunner.RunDeviceAppAsync(bundle, IosPlatform.BundleIdentifier(target), GetOptionalArgument(args, "device"));
            Log.TraceInfo($"{target} exited with code {code}.");
            return code;
        }
        if (!platform.CanRunOnHost && platform.Emulator == null)
            throw new InvalidOperationException($"{platform.PlatformName} executables do not run on this machine ({(HostPlatform.IsWindows ? "Windows" : HostPlatform.IsMacOS ? "macOS" : "Linux")} {HostPlatform.Architecture}).");
        string binaries = Path.Combine(project, "Binaries", platform.PlatformName, configuration.ToString());
        int exit = await platform.RunProgramAsync(platform.ExecutablePath(binaries, target), binaries, []);
        Log.TraceInfo($"{target} exited with code {exit}.");
        return exit;
    }

    /// <summary>The application target to start: --target, else the default target.</summary>
    private static string ResolveRunnable(string project, Platform platform, TargetConfiguration configuration, string? name)
    {
        BuildTarget target;
        using (ProjectBuildLock.AcquireAsync(project).GetAwaiter().GetResult())
            target = ProjectModel.Load(project, platform, configuration).SelectTarget(name);
        if (target.Rules.Type != TargetType.Application)
            throw new InvalidOperationException($"Target '{target.Name}' is a {target.Rules.Type}; run starts Application targets.");
        if (target.Rules.IsTest && platform is AndroidPlatform or IosPlatform)
            throw new InvalidOperationException($"'{target.Name}' is a test executable; use the test command.");
        return target.Name;
    }

    private static (string Project, IReadOnlyList<Platform> Platforms, TargetConfiguration Configuration, List<string> Targets) Prepare(
        CommandLine args, string? defaultConfiguration, string? platformOverride = null)
    {
        string project = ResolveProject(args, 1) ?? throw new ArgumentException("Beast project not found. Pass the project path.");
        SetProject(project);
        BuildConfigManager.LoadConfig(project);
        if (args.HasFlag("stop-on-error")) BuildConfigManager.Config.StopOnFirstError = true;
        var platforms = PlatformFactory.Create(platformOverride ?? GetOptionalArgument(args, "platform"), GetOptionalArgument(args, "arch"));
        var configuration = ParseConfiguration(GetOptionalArgument(args, "config") ?? defaultConfiguration ?? BuildConfigManager.Config.DefaultConfiguration);
        var targets = (GetOptionalArgument(args, "target") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (project, platforms, configuration, targets);
    }

    /// <summary>watch: rebuilds on source changes until Ctrl+C (see Watcher).</summary>
    private static async Task<int> Watch(CommandLine args)
    {
        var (project, platforms, configuration, targets) = Prepare(args, null);
        if (targets.Count > 1) throw new ArgumentException("watch takes one --target.");
        int debounce = int.TryParse(GetOptionalArgument(args, "debounce"), out int ms) && ms >= 0 ? ms : 300;
        int? maxBuilds = int.TryParse(GetOptionalArgument(args, "max-builds"), out int max) && max > 0 ? max : null;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var options = new Watcher.Options(project, platforms, configuration, targets.FirstOrDefault(), GetOptionalArgument(args, "on-success"),
            args.HasFlag("versioned"), debounce, GetOptionalArgument(args, "device"), maxBuilds);
        return await new Watcher(options).RunAsync(cancel.Token);
    }

    private static async Task<int> Test(CommandLine args)
    {
        var (project, platforms, configuration, targets) = Prepare(args, null);
        return await Installer.TestAsync(project, platforms, configuration, targets, GetOptionalArgument(args, "device"), GetOptionalArgument(args, "avd")) == 0 ? 0 : 1;
    }

    private static async Task<int> Install(CommandLine args, bool package)
    {
        // Distribution defaults to Release; --config overrides.
        var (project, platforms, configuration, targets) = Prepare(args, "Release");
        if (package)
            await Installer.PackageAsync(project, platforms, configuration, targets, GetOptionalArgument(args, "output"), args.HasFlag("symbols"),
                args.HasFlag("dmg"), args.HasFlag("notarize"));
        else
        {
            string prefix = GetOptionalArgument(args, "prefix") ?? throw new ArgumentException("install requires --prefix <directory>.");
            await Installer.InstallAsync(project, platforms, configuration, targets, prefix, args.HasFlag("symbols"));
        }
        return 0;
    }

    private static int Clean(CommandLine args)
    {
        string? project = ResolveProject(args, 1);
        if (project == null) return Usage("Project path is required.");
        string intermediate = Path.Combine(project, "Intermediate", "Build");
        string? target = GetOptionalArgument(args, "target");
        if (target == null)
        {
            // Whole project: wait for every running build, then remove all intermediate files.
            using var buildLock = ProjectBuildLock.AcquireBuildAsync(project, exclusive: true).GetAwaiter().GetResult();
            using var projectLock = ProjectBuildLock.AcquireAsync(project).GetAwaiter().GetResult();
            if (Directory.Exists(intermediate)) Directory.Delete(intermediate, true);
            Log.TraceInfo($"Cleaned {intermediate}");
            return 0;
        }
        if (target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || target is "." or "..") return Usage($"Invalid target name '{target}'.");
        // One target, every platform and configuration: Intermediate/Build/<platform>/<configuration>/<target>.
        using var sharedLock = ProjectBuildLock.AcquireBuildAsync(project, exclusive: false).GetAwaiter().GetResult();
        // An external project's target: Intermediate/Build/<platform>/<configuration>/External/<project>/<target>.
        var directories = Directory.Exists(intermediate)
            ? Directory.GetDirectories(intermediate).SelectMany(Directory.GetDirectories)
                .Where(d => Enum.TryParse<TargetConfiguration>(Path.GetFileName(d), true, out _))
                .Select(d => ProjectModel.ExistingBuildDirectory(project, Path.GetFileName(Path.GetDirectoryName(d))!, Enum.Parse<TargetConfiguration>(Path.GetFileName(d), true), target))
                .Where(Directory.Exists).ToList()
            : [];
        using var targetLocks = ProjectBuildLock.AcquireTargetsAsync(project, directories.Select(d =>
        {
            string configurationDirectory = Path.GetFileName(Path.GetDirectoryName(d)!) is var parent && Enum.TryParse<TargetConfiguration>(parent, true, out _)
                ? Path.GetDirectoryName(d)! : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(d)!)!)!;
            return GenericProjectBuilder.TargetLockName(Path.GetFileName(Path.GetDirectoryName(configurationDirectory))!, Enum.Parse<TargetConfiguration>(Path.GetFileName(configurationDirectory), true), target);
        })).GetAwaiter().GetResult();
        foreach (string directory in directories) Directory.Delete(directory, true);
        Log.TraceInfo(directories.Count == 0 ? $"Nothing to clean for target {target}." : $"Cleaned target {target}: {string.Join(", ", directories)}");
        return 0;
    }

    private static TargetConfiguration ParseConfiguration(string value) => value.ToLowerInvariant() switch
    {
        "debug" => TargetConfiguration.Debug,
        "development" => TargetConfiguration.Development,
        "release" => TargetConfiguration.Release,
        _ => throw new ArgumentException($"Unsupported configuration '{value}'. Use Debug, Development, or Release.")
    };

    private static string? ResolveProject(CommandLine args, int positionalIndex)
    {
        string? raw = args.HasArgument("project") ? args.GetArgument("project") : args.Positionals.Skip(positionalIndex).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) raw = Environment.CurrentDirectory;
        string path = Path.GetFullPath(raw);
        return Directory.Exists(Path.Combine(path, "Source")) &&
            File.Exists(Path.Combine(path, "Config", "BuildConfig.json")) ? path : null;
    }

    private static void SetProject(string path)
    {
        GlobalEnvironment.ProjectPath = path;
        GlobalEnvironment.ProjectName = new DirectoryInfo(path).Name;
    }

    private static string? GetOptionalArgument(CommandLine args, string name) => args.HasArgument(name) ? args.GetArgument(name) : null;

    private static int UnknownCommand(string command) => Usage($"Unknown command '{command}'.");
    private static int Usage(string error)
    {
        Log.TraceError(error);
        Help();
        return 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
Beast BuildTool
  beast version
  beast worker --queue <shared-directory> --project-root <allowed-directory> --compiler-root <allowed-directory> [--key-env BEAST_WORKER_KEY] [--once] [--idle-timeout <seconds>] [--reserve-mb <MB>]
  beast new <Name> [--path <directory>] [--template <directory>]   (custom template: files copied, {{ProjectName}} replaced)
  beast module <Name> [project-path]   (defaults to the current project root; also accepts --project <path>)
  beast generate [project-path]   (Visual Studio on Windows, Xcode on macOS, .vscode everywhere)
  beast migrate [project-path]    (legacy project without *.Target.cs -> explicit target)
  beast build [project-path] [--target <name>] [--platform Windows|Linux|Mac|IOS|IOSSimulator|Android] [--arch x64|arm64|arm64-v8a|x86_64] [--config Debug|Development|Release] [--stop-on-error] [--run --avd <name> --wait]
  beast run [project-path] [--target <app>] [--platform Windows|Linux|Mac|IOS|IOSSimulator|Android] [--config ...] [--avd <name>] [--device <serial>] [--no-build] [--wait]
  beast test [project-path] [--target <a,b>] [--platform Windows|Linux|Mac|IOSSimulator|Android] [--device <serial>] [--config ...]   (builds and runs TargetRules.IsTest applications)
  beast install [project-path] --prefix <directory> [--target <a,b>] [--platform ...] [--config Release] [--symbols]
  beast package [project-path] [--output <file.zip>] [--target <a,b>] [--platform ...] [--config Release] [--symbols] [--dmg [--notarize]]
  beast watch [project-path] [--target <name>] [--platform ...] [--config ...] [--on-success "<command>"] [--versioned] [--device <serial>] [--debounce <ms>]
        (rebuilds on changes in Source/ and Config/ of the project and its external projects; Ctrl+C stops)
  beast clean [project-path] [--target <name>]   (without --target: the whole Intermediate/Build)
""");
        return 0;
    }
}

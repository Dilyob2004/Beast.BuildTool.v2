namespace Beast.BuildTool;

public enum TargetType { Application, StaticLibrary, SharedLibrary }

/// <summary>A product, independent of the IDE that invokes its build.</summary>
public class TargetRules
{
    public TargetType Type = TargetType.Application;
    public HashSet<string> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ConsoleApplication = true;
    public CppVersion CppVersion = CppVersion.Cpp20;
    /// <summary>Defaults for every module of this target; ModuleRules.CompileOptions overrides them.</summary>
    public CompileOptions CompileOptions { get; } = new();
    /// <summary>Preprocessor definitions applied to every module compiled for this target.</summary>
    public HashSet<string> Definitions { get; } = new(StringComparer.Ordinal);
    /// <summary>Extra linker arguments for this target's EXE/DLL/.so.</summary>
    public List<string> LinkerArguments { get; } = [];
    /// <summary>Static C/C++ runtime: MSVC /MT(/MTd), Android -static-libstdc++ (no libc++_shared.so in the APK).</summary>
    public bool StaticRuntime = false;
    /// <summary>
    /// Sanitizers for every module of the target. Windows (MSVC): Address. Android (Clang): Address
    /// and/or Undefined; the ASan runtime and wrap.sh are packaged into the APK (Debug builds).
    /// </summary>
    public Sanitizer Sanitizers = Sanitizer.None;
    /// <summary>Whole-program optimization in Development/Release: MSVC /GL + /LTCG, Clang -flto=thin.</summary>
    public bool LinkTimeOptimization = false;
    /// <summary>
    /// Other targets of this project (StaticLibrary or SharedLibrary) built before this one and linked into it.
    /// Their modules are compiled once, by their own target; this target only sees their public settings.
    /// </summary>
    public HashSet<string> TargetDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>An Application run by the test command (exit code 0 = passed); install and package skip it.</summary>
    /// <summary>Version written into installed CMake (ConfigVersion) and pkg-config files, e.g. "1.2.0".</summary>
    public string Version = "";
    public bool IsTest = false;
    public List<string> TestArguments { get; } = [];
}

public sealed record BuildTarget(string Name, string? RulesFile, TargetRules Rules, bool Legacy);

/// <summary>Evaluated rules shared by command-line builds and IDE generation.</summary>
public sealed class ProjectModel
{
    public string ProjectDirectory { get; }
    public IReadOnlyDictionary<string, ModuleRules> Modules { get; }
    public IReadOnlyList<BuildTarget> Targets { get; }
    public IReadOnlyList<string> RulesFiles { get; }
    public PlatformType Platform { get; }

    private ProjectModel(string directory, Dictionary<string, ModuleRules> modules,
        List<BuildTarget> targets, List<string> rulesFiles, PlatformType platform)
    {
        Platform = platform;
        ProjectDirectory = directory;
        Modules = modules;
        Targets = targets;
        RulesFiles = rulesFiles;
    }

    public static ProjectModel Load(string directory, Platform platform, TargetConfiguration configuration)
    {
        directory = Path.GetFullPath(directory);
        GlobalEnvironment.ProjectPath = directory;
        GlobalEnvironment.ProjectName = new DirectoryInfo(directory).Name;
        var files = Directory.EnumerateFiles(Path.Combine(directory, "Source"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (!files.Any(f => f.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("No *.Build.cs module definitions found in Source.");
        var assembly = new Assembler(files, Path.Combine(directory, "Intermediate", "Build", "Rules")).Build();
        var info = new ModulesInfo(configuration, platform);
        var modules = new Dictionary<string, ModuleRules>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<BuildTarget>();
        foreach (string file in files)
        {
            bool isTarget = file.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(file)[..^(isTarget ? ".Target.cs".Length : ".Build.cs".Length)];
            var baseType = isTarget ? typeof(TargetRules) : typeof(ModuleRules);
            var types = assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(baseType) &&
                (t.Name == name || (isTarget && t.Name == name + "Target"))).ToList();
            if (types.Count != 1)
                throw new InvalidOperationException($"{file}: expected exactly one {baseType.Name} class named '{name}'{(isTarget ? $" or '{name}Target'" : "")}.");
            object rules;
            try { rules = Activator.CreateInstance(types[0], info)!; }
            catch (System.Reflection.TargetInvocationException ex) { throw new InvalidOperationException($"{file}: {ex.InnerException?.Message}", ex.InnerException); }
            if (rules is ModuleRules module)
            {
                module.ModuleName = name;
                module.ModulePath = file;
                if (!modules.TryAdd(name, module)) throw new InvalidOperationException($"Duplicate module '{name}'.");
            }
            else
            {
                if (targets.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Duplicate target '{name}'.");
                targets.Add(new(name, file, (TargetRules)rules, false));
            }
        }
        if (targets.Count == 0)
        {
            // Old projects auto-discovered every module and merged their compile settings.
            if (!modules.ContainsKey("Launch"))
                throw new InvalidOperationException("Legacy project requires Launch. Add a *.Target.cs to build a library or choose another entry module.");
            var rules = new TargetRules();
            rules.Modules.UnionWith(modules.Keys);
            targets.Add(new(new DirectoryInfo(directory).Name, null, rules, true));
            if (!_legacyWarned)
            {
                _legacyWarned = true;
                Log.TraceWarning("This project has no *.Target.cs (legacy mode: all modules, merged settings). Legacy mode is deprecated; run 'beast migrate' to create a target.");
            }
        }
        var model = new ProjectModel(directory, modules, targets, files, platform.Type);
        foreach (var target in targets) model.ResolveModules(target);
        foreach (var target in targets) model.ValidateTargetDependencies(target);
        return model;
    }

    /// <summary>Transitive dependency targets, dependencies first.</summary>
    public IReadOnlyList<BuildTarget> TargetDependencies(BuildTarget target)
    {
        var result = new List<BuildTarget>();
        var stack = new List<string>();
        void Visit(BuildTarget item)
        {
            foreach (string name in item.Rules.TargetDependencies.Order(StringComparer.OrdinalIgnoreCase))
            {
                var dependency = Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Target '{item.Name}': unknown dependency target '{name}'. Available: {string.Join(", ", Targets.Select(t => t.Name))}");
                if (dependency.Rules.Type == TargetType.Application)
                    throw new InvalidOperationException($"Target '{item.Name}': dependency '{name}' is an Application; only StaticLibrary/SharedLibrary targets can be linked.");
                if (dependency.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase) || stack.Contains(dependency.Name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Target dependency cycle: {string.Join(" -> ", stack.Prepend(target.Name).Append(dependency.Name))}");
                if (result.Contains(dependency)) continue;
                stack.Add(dependency.Name);
                Visit(dependency);
                stack.RemoveAt(stack.Count - 1);
                result.Add(dependency);
            }
        }
        Visit(target);
        return result;
    }

    /// <summary>
    /// Dependency targets this target links directly: its declared dependencies and, through static
    /// libraries, theirs. A shared library already contains its own dependencies.
    /// </summary>
    public IReadOnlyList<BuildTarget> LinkedTargets(BuildTarget target)
    {
        var result = new List<BuildTarget>();
        void Visit(BuildTarget item)
        {
            foreach (string name in item.Rules.TargetDependencies.Order(StringComparer.OrdinalIgnoreCase))
            {
                var dependency = Targets.First(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (result.Contains(dependency)) continue;
                result.Add(dependency);
                if (dependency.Rules.Type == TargetType.StaticLibrary) Visit(dependency);
            }
        }
        Visit(target);
        return result;
    }

    /// <summary>The dependency target that compiles <paramref name="module"/> for <paramref name="target"/>, or null.</summary>
    public BuildTarget? Provider(BuildTarget target, ModuleRules module)
    {
        // Nearest first: a direct dependency wins over the dependencies it brings along.
        foreach (var dependency in TargetDependencies(target).Reverse())
            if (ResolveModules(dependency).Contains(module)) return dependency;
        return null;
    }

    /// <summary>Modules compiled by this target itself (not provided by a dependency target).</summary>
    public IReadOnlyList<ModuleRules> OwnModules(BuildTarget target) => ResolveModules(target).Where(m => Provider(target, m) == null).ToList();

    private void ValidateTargetDependencies(BuildTarget target)
    {
        if (target.Rules.TargetDependencies.Count == 0) return;
        TargetDependencies(target);
        foreach (string root in target.Rules.Modules)
            if (Provider(target, Modules[root]) is { } provider)
                throw new InvalidOperationException($"Target '{target.Name}': root module '{root}' is already built by dependency target '{provider.Name}'. Remove it from Modules.");
    }

    /// <summary>Value of a module's API macro (MODULE_API) as seen by code compiled for <paramref name="target"/>.</summary>
    public string ApiMacroValue(BuildTarget target, ModuleRules module, PlatformType platform)
    {
        var provider = Provider(target, module);
        bool exported = provider == null ? target.Rules.Type == TargetType.SharedLibrary : provider.Rules.Type == TargetType.SharedLibrary;
        if (!exported) return "";
        if (platform != PlatformType.Windows) return "__attribute__((visibility(\"default\")))";
        return provider == null ? "__declspec(dllexport)" : "__declspec(dllimport)";
    }

    public static string? ApiMacroName(ModuleRules module) => module.ApiMacro == null
        ? new string(module.ModuleName.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray()) + "_API"
        : module.ApiMacro.Length == 0 ? null : module.ApiMacro;

    /// <summary>Build directory of a target (Intermediate/Build/platform/configuration/target).</summary>
    public string BuildDirectory(string platformName, TargetConfiguration configuration, string targetName) =>
        Path.Combine(ProjectDirectory, "Intermediate", "Build", platformName, configuration.ToString(), targetName);

    private static bool _legacyWarned;

    /// <summary>
    /// Writes Source/&lt;Project&gt;.Target.cs for a legacy project: an Application with every Source
    /// module as a root, so that nothing stops being compiled. Settings are no longer merged between
    /// modules: declare dependencies and move exported definitions to PublicDefinitions.
    /// </summary>
    public static string Migrate(string directory)
    {
        directory = Path.GetFullPath(directory);
        string source = Path.Combine(directory, "Source");
        if (Directory.EnumerateFiles(source, "*.Target.cs", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("The project already has a *.Target.cs; nothing to migrate.");
        var modules = Directory.EnumerateFiles(source, "*.Build.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)[..^".Build.cs".Length]).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (modules.Count == 0) throw new InvalidOperationException("No *.Build.cs modules found in Source.");
        string name = new DirectoryInfo(directory).Name;
        string identifier = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (char.IsDigit(identifier[0])) identifier = "_" + identifier;
        string file = Path.Combine(source, name + ".Target.cs");
        var text = new System.Text.StringBuilder();
        text.AppendLine("using Beast.BuildTool;");
        text.AppendLine();
        text.AppendLine("// Created by 'beast migrate' from a legacy project. Every module is a root module, as before.");
        text.AppendLine("// Unlike legacy mode, a module now sees only its own settings and those of its dependencies:");
        text.AppendLine("// add PublicDependencies/PrivateDependencies and move exported definitions to PublicDefinitions.");
        text.AppendLine($"public sealed class {identifier}Target : TargetRules");
        text.AppendLine("{");
        text.AppendLine($"    public {identifier}Target(ModulesInfo info)");
        text.AppendLine("    {");
        text.AppendLine("        Type = TargetType.Application;");
        foreach (string module in modules) text.AppendLine($"        Modules.Add(\"{module}\");");
        text.AppendLine("    }");
        text.AppendLine("}");
        File.WriteAllText(file, text.ToString(), new System.Text.UTF8Encoding(false));
        return file;
    }

    public BuildTarget SelectTarget(string? name)
    {
        if (name != null) return Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown target '{name}'. Available: {string.Join(", ", Targets.Select(t => t.Name))}");
        if (Targets.Count == 1) return Targets[0];
        return Targets.FirstOrDefault(t => t.Name.Equals(new DirectoryInfo(ProjectDirectory).Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Select --target <name>. Available: {string.Join(", ", Targets.Select(t => t.Name))}");
    }

    // Dependency-first ordering. Private dependencies are linked too, but are not re-exported to consumers.
    private readonly Dictionary<BuildTarget, IReadOnlyList<ModuleRules>> _resolved = [];
    public IReadOnlyList<ModuleRules> ResolveModules(BuildTarget target)
    {
        if (_resolved.TryGetValue(target, out var cached)) return cached;
        var resolved = ResolveModulesUncached(target);
        _resolved[target] = resolved;
        return resolved;
    }
    private IReadOnlyList<ModuleRules> ResolveModulesUncached(BuildTarget target)
    {
        if (target.Rules.Modules.Count == 0) throw new InvalidOperationException($"Target '{target.Name}' has no root modules.");
        var result = new List<ModuleRules>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        void Visit(string name)
        {
            if (!Modules.TryGetValue(name, out var module))
                throw new InvalidOperationException($"Target '{target.Name}': unknown module '{name}' (required by {stack.LastOrDefault() ?? target.Name}).");
            if (stack.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Module dependency cycle: {string.Join(" -> ", stack.Append(name))}");
            if (done.Contains(name)) return;
            stack.Add(name);
            foreach (string dependency in module.PublicDependencies.Concat(module.PrivateDependencies).Order(StringComparer.OrdinalIgnoreCase)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            done.Add(name);
            result.Add(module);
        }
        foreach (string root in target.Rules.Modules.Order(StringComparer.OrdinalIgnoreCase)) Visit(root);
        return result;
    }

    public IEnumerable<ModuleRules> PublicClosure(ModuleRules module)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModuleRules>();
        void Visit(ModuleRules item)
        {
            if (!seen.Add(item.ModuleName)) return;
            result.Add(item);
            foreach (string name in item.PublicDependencies) Visit(Modules[name]);
        }
        Visit(module);
        return result;
    }

    public List<string> Sources(ModuleRules module)
    {
        if (module.Type != ModuleType.Source) return [];
        var excluded = SourceFilter.CreateExclusion(module);
        // Nested modules own their files, rather than also compiling them in their parent.
        return OwnedSourceFiles(module)
            .Where(f => !module.UsePlatformDirectories || !SourceFilter.IsOtherPlatform(Path.GetRelativePath(module.ModuleDirectory, f), Platform))
            .Where(f => !excluded(f))
            .Where(f => SourceFilter.IsCompiledFor(f, Platform))
            .ToList();
    }

    /// <summary>All C/C++ files owned by the module, before platform and ExcludedSources filtering.</summary>
    public List<string> OwnedSourceFiles(ModuleRules module) =>
        Directory.EnumerateFiles(module.ModuleDirectory, "*", SearchOption.AllDirectories)
            .Where(f => SourceFilter.IsCompilable(f))
            .Where(f => Modules.Values.Where(m => f.StartsWith(m.ModuleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.ModuleDirectory.Length).First().ModuleName == module.ModuleName)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static string ResolvePath(ModuleRules module, string path) => Path.GetFullPath(path, module.ModuleDirectory);

    /// <summary>
    /// Intermediate/Beast/Include: headers Beast provides to every module (BeastResources.h), copied
    /// from the tool's templates when they change, so the include path does not depend on where the
    /// tool is installed.
    /// </summary>
    public static string BeastIncludeDirectory(string projectDirectory)
    {
        string directory = Path.Combine(projectDirectory, "Intermediate", "Beast", "Include");
        string templates = Path.Combine(GlobalEnvironment.TemplatesPath, "Include");
        if (Directory.Exists(templates))
            foreach (string header in Directory.GetFiles(templates))
            {
                Directory.CreateDirectory(directory);
                ModuleCompilation.WriteIfChanged(Path.Combine(directory, Path.GetFileName(header)), File.ReadAllText(header));
            }
        return directory;
    }

    public CPPEnvironment CompileEnvironment(BuildTarget target, ModuleRules module, Platform platform, TargetConfiguration configuration)
    {
        var env = GenericProjectBuilder.CreateCppEnvironment(platform, configuration, target.Legacy ? Modules.Values : []);
        var paths = new BuildStepPlan(ProjectDirectory,
            Path.Combine(ProjectDirectory, "Intermediate", "Build", platform.PlatformName, configuration.ToString(), target.Name),
            Path.Combine(ProjectDirectory, "Binaries", platform.PlatformName, configuration.ToString()), platform.PlatformName, configuration.ToString(), target.Name, []);
        // Tokens such as {GeneratedDir} of a module built by a dependency target refer to that target.
        BuildStepPlan PathsFor(ModuleRules item) => Provider(target, item) is { } provider
            ? new BuildStepPlan(ProjectDirectory, BuildDirectory(platform.PlatformName, configuration, provider.Name),
                Path.Combine(ProjectDirectory, "Binaries", platform.PlatformName, configuration.ToString()), platform.PlatformName, configuration.ToString(), provider.Name, [])
            : paths;
        string Resolve(ModuleRules item, string value) => ResolvePath(item, PathsFor(item).Expand(item, value));
        env.CppVersion = target.Rules.CppVersion;
        env.StaticRuntime = target.Rules.StaticRuntime;
        env.Sanitizers = SanitizerSupport.Validate(platform.Type, target.Rules.Sanitizers, target.Name);
        // /RTC1 cannot be combined with AddressSanitizer.
        if (env.Sanitizers.HasFlag(Sanitizer.Address)) env.RuntimeChecks = false;
        env.WholeProgramOptimization = target.Rules.LinkTimeOptimization && configuration != TargetConfiguration.Debug;
        CompileOptions.Apply(env, target.Rules.CompileOptions, module.CompileOptions);
        env.PreprocessorDefinitions.AddRange(target.Rules.Definitions);
        env.IncludePaths.Add(Path.Combine(ProjectDirectory, "Source"));
        env.IncludePaths.Add(BeastIncludeDirectory(ProjectDirectory));
        PackageManagers.ApplyCompile(this, platform, configuration, env);
        if (target.Legacy)
        {
            foreach (var item in Modules.Values.Where(m => m.BuildSteps.Count > 0)) env.IncludePaths.Add(paths.GeneratedDirectory(item));
            return env;
        }
        var visible = new List<ModuleRules> { module };
        foreach (string dep in module.PublicDependencies.Concat(module.PrivateDependencies)) visible.AddRange(PublicClosure(Modules[dep]));
        foreach (var item in visible.Distinct())
        {
            env.IncludePaths.AddRange(item.PublicIncludePaths.Select(p => Resolve(item, p)));
            env.ExternalIncludePaths.AddRange(item.PublicSystemIncludePaths.Select(p => Resolve(item, p)));
            if (item.BuildSteps.Count > 0)
            {
                // Generated headers of a module built by a dependency target live in that target's directory.
                env.IncludePaths.Add(PathsFor(item).GeneratedDirectory(item));
            }
            env.PreprocessorDefinitions.AddRange(item.PublicDefinitions);
            if (ApiMacroName(item) is { } macro) env.PreprocessorDefinitions.Add(macro + "=" + ApiMacroValue(target, item, platform.Type));
        }
        env.IncludePaths.AddRange(module.PrivateIncludePaths.Select(p => Resolve(module, p)));
        env.ExternalIncludePaths.AddRange(module.PrivateSystemIncludePaths.Select(p => Resolve(module, p)));
        env.PreprocessorDefinitions.AddRange(module.Definitions);
        return env;
    }
}

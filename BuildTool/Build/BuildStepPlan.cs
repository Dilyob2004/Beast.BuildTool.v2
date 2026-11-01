using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>Resolves trusted rules into file-based graph actions before any process runs.</summary>
public sealed class BuildStepPlan
{
    private readonly string project, build, binaries, platform, configuration, target;
    private readonly IReadOnlyList<ModuleRules> modules;
    private readonly Dictionary<string, List<string>> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> compileInputs = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeclaredOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files placed in Binaries by RuntimeDependencies (after AddRuntimeCopies).</summary>
    public List<string> RuntimeOutputs { get; } = [];

    public BuildStepPlan(string project, string build, string binaries, string platform, string configuration, string target, IReadOnlyList<ModuleRules> modules)
    { this.project = project; this.build = build; this.binaries = binaries; this.platform = platform; this.configuration = configuration; this.target = target; this.modules = modules; }

    public string GeneratedDirectory(ModuleRules module) => Path.Combine(build, "Generated", module.ModuleName, "Custom");
    public string Expand(ModuleRules module, string value)
    {
        // {ProjectDir} is the root of the module's own project: an external project's modules refer to it.
        var tokens = new Dictionary<string, string> { ["ProjectDir"] = module.ProjectDirectory.Length > 0 ? module.ProjectDirectory : project, ["ModuleDir"] = module.ModuleDirectory,
            ["GeneratedDir"] = GeneratedDirectory(module), ["BuildDir"] = build, ["BinaryDir"] = binaries,
            ["Platform"] = platform, ["Configuration"] = configuration, ["TargetName"] = target };
        return Regex.Replace(value, @"\{(ProjectDir|ModuleDir|GeneratedDir|BuildDir|BinaryDir|Platform|Configuration|TargetName)\}", m => tokens[m.Groups[1].Value]);
    }
    private string Source(ModuleRules module, string path) => Path.GetFullPath(Expand(module, path), module.ModuleDirectory);

    /// <summary>AdditionalSources of a module: expanded, absolute, existing compilable files.</summary>
    public List<string> AdditionalSources(ModuleRules module)
    {
        var result = new List<string>();
        foreach (string item in module.AdditionalSources)
        {
            string path = Source(module, item);
            if (!File.Exists(path)) throw new FileNotFoundException($"{module.ModuleName}: AdditionalSources file not found: {item}", path);
            if (!SourceFilter.IsCompilable(path)) throw new InvalidOperationException($"{module.ModuleName}: AdditionalSources must be C/C++/assembly sources: {path}");
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }
        return result;
    }

    public static void ValidateOutput(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Output escapes its allowed directory '{root}': {path}");
        // Do not follow a junction/symlink when writing generated artifacts.
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Output path contains a symlink/junction: {part}");
        if (Directory.Exists(path)) throw new InvalidOperationException($"Expected a file output, found directory: {path}");
    }

    private string Executable(ModuleRules module, string value)
    {
        value = Expand(module, value);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{module.ModuleName}: BuildStep.Executable is required.");
        if (Path.GetExtension(value).ToLowerInvariant() is ".bat" or ".cmd" or ".ps1")
            throw new InvalidOperationException("BuildStep.Executable must be a program; invoke the script interpreter explicitly.");
        if (Path.IsPathFullyQualified(value) || value.IndexOfAny(['/', '\\']) >= 0) return Path.GetFullPath(value, module.ModuleDirectory);
        string? found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => new[] { Path.Combine(p.Trim('"'), value), Path.Combine(p.Trim('"'), value + ".exe") }).FirstOrDefault(File.Exists);
        return found == null ? throw new FileNotFoundException($"Build step executable not found in PATH: {value}") : Path.GetFullPath(found);
    }

    public void AddGenerators(CommandExecutor executor)
    {
        foreach (var module in modules)
        {
            outputs[module.ModuleName] = [];
            compileInputs[module.ModuleName] = [];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in module.BuildSteps)
            {
                if (string.IsNullOrWhiteSpace(step.Name) || !names.Add(step.Name))
                    throw new InvalidOperationException($"{module.ModuleName}: build step names must be nonempty and unique: '{step.Name}'.");
                if (step.Outputs.Count == 0) throw new InvalidOperationException($"{module.ModuleName}/{step.Name}: declare at least one file output.");
                var produced = step.Outputs.Select(p => Path.GetFullPath(Expand(module, p), GeneratedDirectory(module))).ToList();
                foreach (string path in produced)
                {
                    ValidateOutput(GeneratedDirectory(module), path);
                    if (!DeclaredOutputs.Add(path)) throw new InvalidOperationException($"Multiple build steps produce '{path}'.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }
                outputs[module.ModuleName].AddRange(produced);
                if (step.AffectsCompilation) compileInputs[module.ModuleName].AddRange(produced);
                string executable = Executable(module, step.Executable);
                var inputs = step.Inputs.Select(p => Source(module, p)).Append(executable).Append(typeof(BuildStepPlan).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string working = string.IsNullOrWhiteSpace(step.WorkingDirectory) ? module.ModuleDirectory : Source(module, step.WorkingDirectory);
                if (!Directory.Exists(working)) throw new DirectoryNotFoundException($"Build step working directory does not exist: {working}");
                var environment = step.Environment.ToDictionary(p => p.Key, p => p.Value == null ? null : Expand(module, p.Value), StringComparer.OrdinalIgnoreCase);
                executor.AddCommand(new CommandContext(executable, "", CommandType.Custom, produced, inputs, true,
                    $"Generating {module.ModuleName}/{step.Name}...", recreateOutputs: true, forceExecute: step.AlwaysRun,
                    argumentList: step.Arguments.Select(a => Expand(module, a)).ToArray(), workingDirectory: working, environmentOverrides: environment,
                    statePath: Path.Combine(build, "StepStates", module.ModuleName, BuildCacheSession.HashText(step.Name) + ".json")));
            }
            // Existing include/PCH settings may reference this module's generated directory.
            foreach (var paths in new[] { module.PublicIncludePaths, module.PrivateIncludePaths, module.PublicSystemIncludePaths, module.PrivateSystemIncludePaths,
                module.UnityExcludedFiles, module.PrecompiledHeaderExcludedFiles })
            {
                var expanded = paths.Select(p => Expand(module, p)).ToArray(); paths.Clear(); paths.UnionWith(expanded);
            }
            module.PrecompiledHeaderSource = Expand(module, module.PrecompiledHeaderSource);
        }
    }

    public List<string> GeneratedSources(ModuleRules module)
    {
        var sources = module.GeneratedSources.Select(p => Path.GetFullPath(Expand(module, p), GeneratedDirectory(module))).ToList();
        foreach (string path in sources)
            if (!outputs[module.ModuleName].Contains(path, StringComparer.OrdinalIgnoreCase) || Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx"))
                throw new InvalidOperationException($"GeneratedSources must name C/C++ outputs of this module's BuildSteps: {path}");
        if (sources.Count != 0 && module.Type != ModuleType.Source) throw new InvalidOperationException($"{module.ModuleName}: only Source modules may compile GeneratedSources.");
        return sources;
    }

    public void ConfigureCompilation(ProjectModel model, ModuleRules module, CPPEnvironment environment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ModuleRules item)
        {
            if (!seen.Add(item.ModuleName)) return;
            // Modules built by a dependency target are not part of this plan; their generated
            // files already exist and reach the compiler through its dependency output.
            if (compileInputs.TryGetValue(item.ModuleName, out var inputs)) environment.AdditionalPrerequisites.AddRange(inputs);
            foreach (string name in item.PublicDependencies.Concat(item.PrivateDependencies)) Visit(model.Modules[name]);
        }
        Visit(module);
        var visible = new[] { module }.Concat(module.PublicDependencies.Concat(module.PrivateDependencies).SelectMany(n => model.PublicClosure(model.Modules[n])));
        foreach (var item in visible.Distinct())
            if (outputs.TryGetValue(item.ModuleName, out var produced) && produced.Count > 0) environment.IncludePaths.Add(GeneratedDirectory(item));
    }

    /// <summary>Files placed by Resources (after AddResourceCopies), below the resource root.</summary>
    public List<string> ResourceOutputs { get; } = [];
    private readonly Dictionary<string, string> copyDestinations = new(StringComparer.OrdinalIgnoreCase);

    public void AddRuntimeCopies(CommandExecutor executor) =>
        AddCopies(executor, modules.SelectMany(m => m.RuntimeDependencies.Select(i => (m, i))), binaries, "runtime file", RuntimeOutputs);

    /// <summary>Copies Resources of the given modules below 'root' (inside Binaries).</summary>
    public void AddResourceCopies(CommandExecutor executor, IEnumerable<ModuleRules> resourceModules, string root) =>
        AddCopies(executor, resourceModules.SelectMany(m => m.Resources.Select(i => (m, i))), root, "resource", ResourceOutputs);

    private void AddCopies(CommandExecutor executor, IEnumerable<(ModuleRules Module, RuntimeDependency Item)> items, string root, string kind, List<string> produced)
    {
        var destinations = copyDestinations;
        foreach (var (module, item) in items)
        {
            string source = Source(module, item.Source);
            string relative = item.Destination ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException($"{kind} destination must be relative: {relative}");
            string destination = Path.GetFullPath(relative, root);
            ValidateOutput(root, Path.Combine(destination, ".beast-path-check"));
            ValidateOutput(binaries, Path.Combine(destination, ".beast-path-check"));
            if (Directory.Exists(source))
            {
                if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Runtime destination is inside its source directory: {destination}");
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"Runtime directory is a symlink/junction: {source}");
                // Check directory links before recursive enumeration, so cycles cannot be traversed.
                void CopyDirectory(string directory)
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"Runtime directory contains a symlink/junction: {entry}");
                        if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry);
                        else Add(entry, Path.Combine(destination, Path.GetRelativePath(source, entry)));
                    }
                }
                CopyDirectory(source);
            }
            else Add(source, destination);

            void Add(string input, string output)
            {
                ValidateOutput(binaries, output);
                if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Runtime source equals destination: {input}");
                if (destinations.TryGetValue(output, out var previous))
                {
                    if (!input.Equals(previous, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Conflicting runtime files for {output}: {previous} and {input}");
                    return;
                }
                destinations.Add(output, input);
                produced.Add(output);
                AddCopyCommand(executor, input, output, binaries, Path.Combine(build, "RuntimeStates"), $"Copying {kind} {Path.GetRelativePath(binaries, output)}...");
            }
        }
    }

    internal static void AddCopyCommand(CommandExecutor executor, string input, string output, string allowedRoot, string states, string description)
    {
        executor.AddCommand(new CommandContext(typeof(BuildStepPlan).Assembly.Location, "copy-v1 " + JsonSerializer.Serialize(new[] { input, output }),
            CommandType.Copy, [output], [input, typeof(BuildStepPlan).Assembly.Location], true, description,
            statePath: Path.Combine(states, BuildCacheSession.HashText(output.ToUpperInvariant()) + ".json"),
            execute: async token =>
            {
                ValidateOutput(allowedRoot, output);
                string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var from = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await from.CopyToAsync(to, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, output, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }));
    }
}

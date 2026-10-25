using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>What a translation unit declares about C++20 modules.</summary>
public sealed record ModuleUnitInfo(string? Name, string? Partition, bool Exported, IReadOnlyList<string> Imports)
{
    public static readonly ModuleUnitInfo None = new(null, null, false, []);
    /// <summary>"Math" or "Math:Ops".</summary>
    public string? FullName => Name == null ? null : Partition == null ? Name : Name + ":" + Partition;
    public bool IsModuleUnit => Name != null;
    /// <summary>Interface units, partition interfaces and internal partitions produce a BMI; implementation units do not.</summary>
    public bool ProducesInterface => Name != null && (Exported || Partition != null);
    /// <summary>Modules this unit needs: its imports and, for an implementation unit, its own interface.</summary>
    public IEnumerable<string> Requires => Name != null && !Exported && Partition == null ? Imports.Prepend(Name) : Imports;
    public bool UsesModules => IsModuleUnit || Imports.Count > 0;
}

/// <summary>How one source is compiled with modules: the BMI it writes and the BMIs it reads.</summary>
public sealed record ModuleCompileUnit(ModuleUnitInfo Info, string? InterfaceOutput, IReadOnlyList<(string Name, string Path)> References);

/// <summary>
/// C++20 named modules: scanning, the module graph of a target and BMI paths. Interface units are
/// .ixx/.cppm files (or any source with "export module"); "module X;" implementation units and
/// "import X;" consumers may be any C++ source. Header units (import &lt;vector&gt;;) and import std
/// are not supported. The scanner ignores the preprocessor: conditionally compiled imports count.
/// </summary>
public static partial class CppModules
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Stamp, ModuleUnitInfo Info)> Cache = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^\s*(export\s+)?module\s+([A-Za-z_][\w.]*)\s*(?::\s*([A-Za-z_][\w.]*))?\s*;", RegexOptions.Multiline)]
    private static partial Regex ModuleDeclaration();
    [GeneratedRegex(@"^\s*(?:export\s+)?import\s+(?:([A-Za-z_][\w.]*)\s*(?::\s*([A-Za-z_][\w.]*))?|:\s*([A-Za-z_][\w.]*)|([<""][^>""]*[>""]))\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportDeclaration();

    public static bool IsInterfaceExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ixx" or ".cppm";

    /// <summary>Scans a source (cached by size and timestamp within the process).</summary>
    public static ModuleUnitInfo Scan(string path)
    {
        if (!File.Exists(path)) return ModuleUnitInfo.None;
        var file = new FileInfo(path);
        if (Cache.TryGetValue(path, out var known) && known.Length == file.Length && known.Stamp == file.LastWriteTimeUtc) return known.Info;
        var info = Parse(File.ReadAllText(path), path);
        Cache[path] = (file.Length, file.LastWriteTimeUtc, info);
        return info;
    }

    public static ModuleUnitInfo Parse(string text, string path = "<source>")
    {
        // Cheap prefilter: most sources mention neither keyword.
        if (!text.Contains("module") && !text.Contains("import"))
            return IsInterfaceExtension(path) ? throw new InvalidOperationException($"{path}: a .ixx/.cppm file must declare a module (export module Name;).") : ModuleUnitInfo.None;
        string code = StripCommentsAndStrings(text);
        string? name = null, partition = null;
        bool exported = false;
        foreach (Match match in ModuleDeclaration().Matches(code))
        {
            if (name != null) throw new InvalidOperationException($"{path}: more than one module declaration.");
            exported = match.Groups[1].Success;
            name = match.Groups[2].Value;
            partition = match.Groups[3].Success ? match.Groups[3].Value : null;
        }
        if (name == null && IsInterfaceExtension(path))
            throw new InvalidOperationException($"{path}: a .ixx/.cppm file must declare a module (export module Name;).");
        // MSVC rejects an internal partition in an interface file (C7622); keep projects portable.
        if (name != null && !exported && IsInterfaceExtension(path))
            throw new InvalidOperationException($"{path}: '{(partition != null ? $"module {name}:{partition};" : $"module {name};")}' is not an interface; " +
                "put internal partitions and implementation units in .cpp files (.ixx/.cppm are for 'export module').");
        var imports = new List<string>();
        foreach (Match match in ImportDeclaration().Matches(code))
        {
            if (match.Groups[4].Success)
                throw new InvalidOperationException($"{path}: header units ({match.Groups[4].Value.Trim()}) are not supported; use #include.");
            if (match.Groups[3].Success)
            {
                if (name == null) throw new InvalidOperationException($"{path}: 'import :{match.Groups[3].Value};' is only valid inside a module.");
                imports.Add(name + ":" + match.Groups[3].Value);
            }
            else
            {
                string imported = match.Groups[1].Value;
                if (match.Groups[2].Success) throw new InvalidOperationException($"{path}: partitions of another module cannot be imported ({imported}:{match.Groups[2].Value}).");
                imports.Add(imported);
            }
        }
        if (name == null && imports.Count == 0) return ModuleUnitInfo.None;
        return new ModuleUnitInfo(name, partition, exported, imports.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Replaces comments and string/character literals with spaces, keeping line structure.</summary>
    internal static string StripCommentsAndStrings(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; ++i)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') ++i;
                result.Append('\n');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) { result.Append(text[i] == '\n' ? '\n' : ' '); ++i; }
                ++i;
                result.Append(' ');
            }
            else if (c == 'R' && i + 1 < text.Length && text[i + 1] == '"' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                // Raw string R"delim( ... )delim"
                int open = text.IndexOf('(', i + 2);
                if (open < 0) { result.Append(c); continue; }
                string close = ")" + text[(i + 2)..open] + "\"";
                int end = text.IndexOf(close, open, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                foreach (char skipped in text[i..end]) result.Append(skipped == '\n' ? '\n' : ' ');
                i = end - 1;
            }
            else if (c is '"' or '\'' && !(c == '\'' && i > 0 && char.IsLetterOrDigit(text[i - 1]))) // digit separators: 1'000
            {
                // Keep the quote of a header unit (import "x.h";) so that it can be reported.
                int start = i;
                ++i;
                while (i < text.Length && text[i] != c && text[i] != '\n') { if (text[i] == '\\') ++i; ++i; }
                result.Append(c == '"' && IsImportBefore(result) ? text[start..Math.Min(i + 1, text.Length)] : new string(' ', Math.Min(i + 1, text.Length) - start));
            }
            else result.Append(c);
        }
        return result.ToString();
    }

    private static bool IsImportBefore(StringBuilder text)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) --end;
        return end >= 6 && text.ToString(end - 6, 6) == "import" && (end == 6 || !char.IsLetterOrDigit(text[end - 7]));
    }

    /// <summary>Clang (16+): one compile writes the object and, for interface units, the BMI.</summary>
    public static void AddClangArguments(string source, ModuleCompileUnit unit, List<string> args, List<string> produced)
    {
        if (unit.InterfaceOutput != null)
        {
            args.AddRange(["-x", "c++-module"]);
            args.Add($"-fmodule-output=\"{unit.InterfaceOutput}\"");
            produced.Add(unit.InterfaceOutput);
        }
        // An implementation unit in a .cppm/.ixx file is not an interface (Clang assumes one).
        else if (IsInterfaceExtension(source)) args.AddRange(["-x", "c++"]);
        foreach (var (name, path) in unit.References) args.Add($"-fmodule-file={name}=\"{path}\"");
    }

    /// <summary>File name of a module's BMI: "Math:Ops" becomes "Math-Ops.pcm".</summary>
    public static string InterfaceFileName(string moduleName, string extension) => moduleName.Replace(':', '-') + extension;
}

/// <summary>
/// The C++20 module graph of one target: which source provides each module (in this target or in
/// a dependency target), where its BMI is written, and what every unit must reference.
/// </summary>
public sealed class CppModulePlan
{
    /// <summary>Owner null: a standard library module (std, std.compat), visible to every module.</summary>
    private sealed record Provider(string Name, string Source, ModuleRules? Owner, string Interface, ModuleUnitInfo Info, bool Local);
    public static readonly IReadOnlyList<string> StandardModules = ["std", "std.compat"];
    private readonly List<string> _standardSources = [];
    /// <summary>The compile environment of the first unit importing std: the std BMI must match its flags.</summary>
    private CPPEnvironment? _standardEnvironment;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly ProjectModel _model;
    private readonly BuildTarget _target;
    public bool IsEmpty => _providers.Count == 0 && !_anyConsumers;
    private bool _anyConsumers;

    private CppModulePlan(ProjectModel model, BuildTarget target) { _model = model; _target = target; }

    public static CppModulePlan Create(ProjectModel model, BuildTarget target, Platform platform, TargetConfiguration configuration)
    {
        var plan = new CppModulePlan(model, target);
        void Add(BuildTarget owner, ModuleRules module, bool local)
        {
            foreach (string source in model.Sources(module))
            {
                var info = CppModules.Scan(source);
                if (!info.UsesModules) continue;
                if (local) plan._anyConsumers = true;
                if (!info.ProducesInterface) continue;
                string extension = platform.ModuleInterfaceExtension
                    ?? throw new InvalidOperationException($"{source}: C++20 modules are not available: {platform.ModulesUnsupportedReason}.");
                string bmi = Path.Combine(model.BuildDirectory(platform.PlatformName, configuration, owner.Name), "Modules", CppModules.InterfaceFileName(info.FullName!, extension));
                if (plan._providers.TryGetValue(info.FullName!, out var existing))
                    throw new InvalidOperationException($"Module '{info.FullName}' is declared twice: {existing.Source} and {source}.");
                plan._providers[info.FullName!] = new(info.FullName!, source, module, bmi, info, local);
            }
        }
        var dependencies = model.TargetDependencies(target);
        foreach (var dependency in dependencies)
            foreach (var module in model.OwnModules(dependency)) Add(dependency, module, false);
        foreach (var module in model.OwnModules(target)) Add(target, module, true);
        // import std / std.compat: built from the compiler's own module source (MSVC modules/std.ixx,
        // libc++ share/libc++/v1/std.cppm) by the deepest target that imports it, so that a target and
        // its dependency targets share one std BMI.
        IEnumerable<string> Imports(BuildTarget owner) => model.OwnModules(owner).SelectMany(model.Sources).Select(CppModules.Scan).SelectMany(i => i.Requires);
        var needed = new HashSet<string>(Imports(target), StringComparer.Ordinal);
        foreach (var dependency in dependencies) needed.UnionWith(Imports(dependency));
        if (needed.Contains("std.compat")) needed.Add("std");
        foreach (string name in StandardModules.Where(needed.Contains))
        {
            if (plan._providers.ContainsKey(name)) continue;
            string source = platform.StandardModuleSource(name)
                ?? throw new InvalidOperationException($"'import {name};' is not available: {platform.StandardModuleUnsupportedReason}.");
            string extension = platform.ModuleInterfaceExtension
                ?? throw new InvalidOperationException($"'import {name};' is not available: {platform.ModulesUnsupportedReason}.");
            var owner = dependencies.FirstOrDefault(d => Imports(d).Contains(name)) ?? target;
            string bmi = Path.Combine(model.BuildDirectory(platform.PlatformName, configuration, owner.Name), "Modules", CppModules.InterfaceFileName(name, extension));
            plan._providers[name] = new(name, source, null, bmi, CppModules.Scan(source), owner == target);
            if (owner == target) plan._standardSources.Add(source);
        }
        if (!plan.IsEmpty && model.Targets.Any(t => t.Legacy))
            throw new InvalidOperationException("C++20 modules need an explicit target (*.Target.cs). Run 'beast migrate'.");
        return plan;
    }

    /// <summary>Marks the module sources of <paramref name="module"/> in its compile environment.</summary>
    public void Apply(ModuleRules module, CPPEnvironment environment, IEnumerable<string> sources)
    {
        if (IsEmpty) return;
        // Visible modules: the module's own and those of its dependencies' public closures.
        var visible = new HashSet<ModuleRules> { module };
        foreach (string dependency in module.PublicDependencies.Concat(module.PrivateDependencies))
            visible.UnionWith(_model.PublicClosure(_model.Modules[dependency]));
        foreach (string source in sources)
        {
            var info = CppModules.Scan(source);
            if (!info.UsesModules) continue;
            var references = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string name, string from, bool direct)
            {
                if (!seen.Add(name)) return;
                if (!_providers.TryGetValue(name, out var provider))
                {
                    string hint = " Add a .ixx/.cppm interface that declares 'export module " + name + ";'.";
                    throw new InvalidOperationException($"{from}: module '{name}' was not found in target '{_target.Name}' or its dependency targets.{hint}");
                }
                if (direct && provider.Owner != null && !visible.Contains(provider.Owner))
                    throw new InvalidOperationException($"{from}: module '{name}' belongs to module rules '{provider.Owner.ModuleName}'. " +
                        $"Add PrivateDependencies.Add(\"{provider.Owner.ModuleName}\") (or PublicDependencies) to '{module.ModuleName}'.");
                if (provider.Source == source) return; // an interface referencing itself
                if (provider.Owner == null && provider.Local) _standardEnvironment ??= (CPPEnvironment)environment.Clone();
                references.Add((name, provider.Interface));
                // BMIs reference the BMIs they were built with.
                foreach (string next in provider.Info.Requires) Visit(next, provider.Source, false);
            }
            foreach (string name in info.Requires) Visit(name, source, true);
            string? output = info.ProducesInterface ? _providers[info.FullName!].Interface : null;
            if (output != null) Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            environment.ModuleUnits[source] = new ModuleCompileUnit(info, output, references);
            if (!environment.SourcePrerequisites.TryGetValue(source, out var prerequisites))
                environment.SourcePrerequisites[source] = prerequisites = [];
            prerequisites.AddRange(references.Select(r => r.Item2));
        }
    }

    /// <summary>
    /// Compiles the standard library modules this target provides (after Apply for every module),
    /// with the flags of their first importer but without warnings; returns the objects to link.
    /// </summary>
    public IReadOnlyList<string> CompileStandardModules(Platform platform, CommandExecutor executor, string buildPath)
    {
        if (_standardSources.Count == 0 || _standardEnvironment == null) return [];
        var environment = (CPPEnvironment)_standardEnvironment.Clone();
        environment.ModuleUnits.Clear();
        environment.SourcePrerequisites.Clear();
        environment.PrecompiledHeaderUsage = PrecompiledHeaderFileUsage.None;
        environment.Warnings = WarningLevel.Off;
        environment.TreatWarningsAsErrors = false;
        foreach (string source in _standardSources)
        {
            var provider = _providers.Values.Single(p => p.Source == source);
            var references = provider.Info.Requires.Where(_providers.ContainsKey).Select(n => (n, _providers[n].Interface)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(provider.Interface)!);
            environment.ModuleUnits[source] = new ModuleCompileUnit(provider.Info, provider.Interface, references);
            environment.SourcePrerequisites[source] = references.Select(r => r.Interface).ToList();
        }
        return platform.CompileCppFiles(executor, environment, _standardSources.ToList(), Path.Combine(buildPath, "Objects", "__StandardModules")).ObjectFiles;
    }
}

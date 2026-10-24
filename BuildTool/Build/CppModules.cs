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
    private sealed record Provider(string Name, string Source, ModuleRules Owner, string Interface, ModuleUnitInfo Info, bool Local);
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
                    ?? throw new InvalidOperationException($"{source}: C++20 modules are not supported by the {platform.PlatformName} compiler configuration (clang-cl). Use MSVC, Clang or GCC.");
                string bmi = Path.Combine(model.BuildDirectory(platform.PlatformName, configuration, owner.Name), "Modules", CppModules.InterfaceFileName(info.FullName!, extension));
                if (plan._providers.TryGetValue(info.FullName!, out var existing))
                    throw new InvalidOperationException($"Module '{info.FullName}' is declared twice: {existing.Source} and {source}.");
                plan._providers[info.FullName!] = new(info.FullName!, source, module, bmi, info, local);
            }
        }
        foreach (var dependency in model.TargetDependencies(target))
            foreach (var module in model.OwnModules(dependency)) Add(dependency, module, false);
        foreach (var module in model.OwnModules(target)) Add(target, module, true);
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
                    string hint = name == "std" || name.StartsWith("std.", StringComparison.Ordinal)
                        ? " 'import std' is not supported yet; use #include."
                        : " Add a .ixx/.cppm interface that declares 'export module " + name + ";'.";
                    throw new InvalidOperationException($"{from}: module '{name}' was not found in target '{_target.Name}' or its dependency targets.{hint}");
                }
                if (direct && !visible.Contains(provider.Owner))
                    throw new InvalidOperationException($"{from}: module '{name}' belongs to module rules '{provider.Owner.ModuleName}'. " +
                        $"Add PrivateDependencies.Add(\"{provider.Owner.ModuleName}\") (or PublicDependencies) to '{module.ModuleName}'.");
                if (provider.Source == source) return; // an interface referencing itself
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
}

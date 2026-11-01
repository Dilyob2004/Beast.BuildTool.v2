using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>
/// After a failed compilation: for an #include the compiler did not find, names the module that
/// provides the header and the dependency to declare (rules ported from a tool that merged every
/// module's settings fail this way until their dependencies are written down).
/// </summary>
public static partial class IncludeHints
{
    // clang/clang-cl/NDK, GCC, MSVC (the tool runs cl.exe with VSLANG=1033: English messages).
    [GeneratedRegex(@"fatal error: '([^']+)' file not found")] private static partial Regex Clang();
    [GeneratedRegex(@"fatal error: ([^\s:][^:]*?): No such file or directory")] private static partial Regex Gcc();
    [GeneratedRegex(@"error C1083: [^:]*: '([^']+)': No such file or directory")] private static partial Regex Msvc();

    public static IReadOnlyList<string> MissingIncludes(IEnumerable<string> output)
    {
        var result = new List<string>();
        foreach (string text in output)
            foreach (var regex in new[] { Clang(), Gcc(), Msvc() })
                foreach (Match match in regex.Matches(text))
                {
                    string include = match.Groups[1].Value.Trim().Replace('\\', '/');
                    if (include.Length > 0 && !result.Contains(include, StringComparer.OrdinalIgnoreCase)) result.Add(include);
                }
        return result;
    }

    /// <summary>Modules other than <paramref name="module"/> and those it already sees that provide <paramref name="include"/>.</summary>
    public static List<(ModuleRules Owner, string File)> Providers(ProjectModel model, ModuleRules module, string include)
    {
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.ModuleName };
        foreach (string name in module.PublicDependencies.Concat(module.PrivateDependencies))
            if (model.Modules.ContainsKey(name))
                foreach (var item in model.PublicClosure(model.Modules[name])) visible.Add(item.ModuleName);
        var found = new List<(ModuleRules, string)>();
        void Consider(ModuleRules? owner, string file)
        {
            if (owner == null || visible.Contains(owner.ModuleName) || found.Any(f => f.Item1 == owner)) return;
            found.Add((owner, file));
        }
        foreach (var candidate in model.Modules.Values.OrderBy(m => m.ModuleName, StringComparer.OrdinalIgnoreCase))
            foreach (string path in candidate.PublicIncludePaths.Concat(candidate.PublicSystemIncludePaths).Where(p => !p.Contains('{')))
            {
                string file = Path.GetFullPath(include, ProjectModel.ResolvePath(candidate, path));
                if (File.Exists(file)) Consider(candidate, file);
            }
        // "Core/Public/Core.h" spelled from Source/ or an include root: the module whose directory holds the file.
        var roots = model.Modules.Values.Select(m => m.ProjectDirectory.Length > 0 ? m.ProjectDirectory : model.ProjectDirectory).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => ProjectSettings.For(p).IncludeRoots.Prepend(Path.Combine(p, "Source")));
        foreach (string root in roots)
        {
            string file = Path.GetFullPath(include, root);
            if (!File.Exists(file)) continue;
            Consider(model.Modules.Values.Where(m => file.StartsWith(m.ModuleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.ModuleDirectory.Length).FirstOrDefault(), file);
        }
        return found;
    }

    /// <summary>Hint lines for a failed compile action of <paramref name="module"/>.</summary>
    public static List<string> For(ProjectModel model, ModuleRules module, IEnumerable<string> output)
    {
        var lines = new List<string>();
        foreach (string include in MissingIncludes(output))
        {
            var providers = Providers(model, module, include);
            if (providers.Count == 0) continue;
            var (owner, file) = providers[0];
            string others = providers.Count > 1 ? $" (also in {string.Join(", ", providers.Skip(1).Select(p => p.Owner.ModuleName))})" : "";
            lines.Add($"'{include}' is provided by module '{owner.ModuleName}' ({file}){others}. Add PrivateDependencies.Add(\"{owner.ModuleName}\") " +
                $"(PublicDependencies if '{module.ModuleName}' exposes it in its public headers) to {module.ModulePath}.");
        }
        return lines;
    }
}

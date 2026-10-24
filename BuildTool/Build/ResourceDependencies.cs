using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>Conservative RC include/file closure. Ambiguous macro includes disable reuse.</summary>
public static class ResourceDependencies
{
    public sealed record Result(List<string> Files, List<string> SearchDirectories, bool Complete);
    public static Result Discover(string script, IEnumerable<string> includeDirectories)
    {
        string resourceRoot = Path.GetDirectoryName(Path.GetFullPath(script))!;
        var directories = new HashSet<string>(includeDirectories.Append(resourceRoot).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool complete = true;
        string? Resolve(string name, string current)
        {
            if (Path.IsPathFullyQualified(name)) return name;
            return new[] { current, resourceRoot, GlobalEnvironment.WorkingDirectory }.Concat(directories)
                .Select(d => Path.GetFullPath(name, d)).FirstOrDefault(File.Exists);
        }
        void Visit(string path)
        {
            path = Path.GetFullPath(path);
            files.Add(path);
            if (!visited.Add(path) || !File.Exists(path)) return;
            directories.Add(Path.GetDirectoryName(path)!);
            string text = File.ReadAllText(path);
            foreach (Match include in Regex.Matches(text, @"(?m)^\s*#\s*include\s+([^\r\n]+)"))
            {
                var literal = Regex.Match(include.Groups[1].Value, "^[\"<]([^\">]+)[\">]");
                if (!literal.Success) { complete = false; continue; }
                string name = literal.Groups[1].Value;
                string? resolved = Resolve(name, Path.GetDirectoryName(path)!);
                if (resolved != null) Visit(resolved);
                else complete = false; // Includes inside inactive branches are a safe extra rebuild.
            }
            // Capture literal paths in resource declarations and string-valued #defines.
            foreach (Match literal in Regex.Matches(text, "\"([^\"\r\n]+)\""))
            {
                string name = literal.Groups[1].Value.Replace("\\\\", "\\");
                if (name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                string? resolved;
                try { resolved = Resolve(name, Path.GetDirectoryName(path)!); }
                catch (ArgumentException) { continue; }
                if (resolved != null && File.Exists(resolved)) files.Add(Path.GetFullPath(resolved));
            }
            // Nonliteral file operands may be macro expressions. Never declare those actions up to date.
            foreach (Match declaration in Regex.Matches(text, @"(?mi)^\s*\S+\s+(?:ICON|BITMAP|CURSOR|FONT|RCDATA|HTML|MESSAGETABLE|ANICURSOR|ANIICON|\d+)\s+([^\r\n]+)"))
            {
                string operand = declaration.Groups[1].Value.Trim();
                if (!operand.Contains('"') && !Regex.IsMatch(operand, @"^(BEGIN\b|\{)", RegexOptions.IgnoreCase)) complete = false;
            }
        }
        Visit(script);
        // Covers macro-selected files inside the resource directory without guessing preprocessing branches.
        if (Directory.Exists(resourceRoot))
            foreach (string file in Directory.EnumerateFiles(resourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) files.Add(file);
        return new(files.Order(StringComparer.OrdinalIgnoreCase).ToList(), directories.ToList(), complete);
    }
}

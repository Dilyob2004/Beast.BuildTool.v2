using System.Text;
using System.Text.Json;

namespace Beast.BuildTool;

public enum DependencyFormat { None, MsvcJson, Makefile }

/// <summary>Reads dependencies emitted by the compiler, including conditional and transitive includes.</summary>
public static class CompilerDependencies
{
    public static IReadOnlyList<string> Read(string path, DependencyFormat format)
    {
        if (!File.Exists(path)) return [];
        if (format == DependencyFormat.MsvcJson)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("Data").GetProperty("Includes").EnumerateArray()
                .Select(e => Path.GetFullPath(e.GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (format != DependencyFormat.Makefile) return [];
        string text = File.ReadAllText(path).Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("\r\n", "\n");
        var result = new List<string>();
        bool anyRule = false;
        // One rule per logical line. Besides "object: inputs", GCC with C++20 modules adds rules and
        // variables for its module mapper (X.c++m targets, .PHONY, CXX_IMPORTS +=, "bmi:| object");
        // only real files are inputs.
        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            int delimiter = -1;
            for (int i = 0; i < line.Length; ++i)
                if (line[i] == ':' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1]) || line[i + 1] == '|')) { delimiter = i; break; }
            if (delimiter < 0)
            {
                if (line.Contains("+=") || line.Contains('=')) continue; // make variable
                throw new InvalidDataException($"Invalid compiler dependency file: {path}");
            }
            anyRule = true;
            string targets = line[..delimiter].Trim();
            if (targets == ".PHONY" || targets.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => t.EndsWith(".c++m", StringComparison.Ordinal))) continue;
            var token = new StringBuilder();
            void Flush()
            {
                if (token.Length == 0) return;
                string value = token.ToString();
                token.Clear();
                if (!value.EndsWith(".c++m", StringComparison.Ordinal)) result.Add(Path.GetFullPath(value));
            }
            for (int i = delimiter + 1; i < line.Length; ++i)
            {
                char c = line[i];
                if (c == '|' && token.Length == 0) break; // order-only prerequisites
                if (c == '\\' && i + 1 < line.Length && (char.IsWhiteSpace(line[i + 1]) || line[i + 1] is '\\' or '#' or ':'))
                    token.Append(line[++i]);
                else if (c == '$' && i + 1 < line.Length && line[i + 1] == '$') { token.Append('$'); ++i; }
                else if (char.IsWhiteSpace(c)) Flush();
                else token.Append(c);
            }
            Flush();
        }
        if (!anyRule) throw new InvalidDataException($"Invalid compiler dependency file: {path}");
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

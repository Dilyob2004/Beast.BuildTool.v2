using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Beast.BuildTool
{
    /// <summary>
    /// Writes compile_commands.json (clangd, VS Code, CLion, clang-tidy) from the compile actions of a
    /// build: one file per target in its build directory, merged into the project root by source file,
    /// so the root database covers every target and platform built most recently.
    /// </summary>
    public static class CompileCommandsGenerator
    {
        private sealed class Entry
        {
            public string directory { get; set; } = "";
            public string file { get; set; } = "";
            public string command { get; set; } = "";
        }

        private static readonly System.Text.RegularExpressions.Regex UnityName = new(@"^Unity_\d{4}\.cpp$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static void Write(string projectPath, string buildPath, IEnumerable<CommandContext> actions)
        {
            var entries = new List<Entry>();
            // C/C++ only: editors cannot use assembler command lines.
            foreach (var action in actions.Where(a => a.Type == CommandType.Compile && a.PrerequisiteFiles.Count > 0 &&
                Path.GetExtension(a.PrerequisiteFiles[0]).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".m" or ".mm" or ".ixx" or ".cppm"))
            {
                string source = Path.GetFullPath(action.PrerequisiteFiles[0]);
                string directory = action.WorkingDirectory ?? GlobalEnvironment.WorkingDirectory;
                string command = CommandUtilities.QuoteArgument(action.CompilerPath) + " " + action.Arguments;
                entries.Add(new Entry { directory = directory, file = source, command = command });
                // A Unity unit stands in for its members: give each member the unit's command with
                // its own path, so editors analyse the file the user opens.
                if (!UnityName.IsMatch(Path.GetFileName(source)) || !command.Contains(source, StringComparison.Ordinal)) continue;
                if (!File.Exists(source)) continue;
                var members = File.ReadAllLines(source).Where(l => l.StartsWith("#include \"", StringComparison.Ordinal) && l.EndsWith('"'))
                    .Select(l => l["#include \"".Length..^1]).Where(Path.IsPathFullyQualified);
                foreach (string member in members)
                    entries.Add(new Entry { directory = directory, file = Path.GetFullPath(member), command = command.Replace(source, Path.GetFullPath(member), StringComparison.Ordinal) });
            }
            if (entries.Count == 0) return;
            // Readable command lines: no \u0022 for quotes or \u002B for '+'.
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            try
            {
                Directory.CreateDirectory(buildPath);
                WriteAtomically(Path.Combine(buildPath, "compile_commands.json"), JsonSerializer.Serialize(entries, options));
                string root = Path.Combine(projectPath, "compile_commands.json");
                var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(root))
                {
                    try
                    {
                        foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(root)) ?? [])
                            if (!string.IsNullOrWhiteSpace(entry.file) && File.Exists(entry.file)) merged[entry.file] = entry;
                    }
                    catch (JsonException) { } // A damaged database is regenerated from this build.
                }
                foreach (var entry in entries) merged[entry.file] = entry;
                WriteAtomically(root, JsonSerializer.Serialize(merged.Values.OrderBy(e => e.file, StringComparer.OrdinalIgnoreCase).ToList(), options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.TraceWarning($"compile_commands.json was not updated: {ex.Message}");
            }
        }

        private static void WriteAtomically(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>A project whose targets and modules another project uses (BuildConfig.json externalProjects).</summary>
public sealed record ExternalProject(string Name, string Directory)
{
    public string SourceDirectory => Path.Combine(Directory, "Source");
}

public static class ExternalProjects
{
    /// <summary>
    /// External projects of a project, including those of its external projects (each project's
    /// Config/BuildConfig.json lists its own), in declaration order. The same directory reached twice
    /// is loaded once; a name used for two directories, a missing Source directory or the project
    /// itself is an error.
    /// </summary>
    public static IReadOnlyList<ExternalProject> Resolve(string projectDirectory, IReadOnlyList<ExternalProjectConfig>? configured = null)
    {
        projectDirectory = Path.GetFullPath(projectDirectory);
        var result = new List<ExternalProject>();
        var visiting = new List<string> { projectDirectory };
        void Visit(string owner, IReadOnlyList<ExternalProjectConfig> entries)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || !Regex.IsMatch(entry.Name, "^[A-Za-z0-9_.-]+$"))
                    throw new InvalidOperationException($"{ConfigPath(owner)}: externalProjects needs a \"name\" of letters, digits, '_', '.' or '-' (got '{entry.Name}').");
                if (string.IsNullOrWhiteSpace(entry.Path))
                    throw new InvalidOperationException($"{ConfigPath(owner)}: external project '{entry.Name}' has no \"path\".");
                string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Expand(entry.Path), owner));
                if (!System.IO.Directory.Exists(Path.Combine(directory, "Source")))
                    throw new InvalidOperationException($"{ConfigPath(owner)}: external project '{entry.Name}' not found: {Path.Combine(directory, "Source")} does not exist.");
                if (visiting.Contains(directory, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"External project cycle: {string.Join(" -> ", visiting.Append(directory))}");
                var same = result.FirstOrDefault(p => p.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    if (!same.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"External project {directory} is named both '{same.Name}' and '{entry.Name}'.");
                    continue;
                }
                if (result.FirstOrDefault(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) is { } other)
                    throw new InvalidOperationException($"Two external projects are named '{entry.Name}': {other.Directory} and {directory}.");
                visiting.Add(directory);
                Visit(directory, ReadConfig(directory));
                visiting.RemoveAt(visiting.Count - 1);
                result.Add(new ExternalProject(entry.Name, directory));
            }
        }
        Visit(projectDirectory, configured ?? ReadConfig(projectDirectory));
        return result;
    }

    private static string ConfigPath(string project) => Path.Combine(project, "Config", "BuildConfig.json");

    /// <summary>externalProjects of a project's BuildConfig.json, without loading the rest of it as the active configuration.</summary>
    private static IReadOnlyList<ExternalProjectConfig> ReadConfig(string project)
    {
        string path = ConfigPath(project);
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("externalProjects", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            return list.EnumerateArray().Select(e => new ExternalProjectConfig
            {
                Name = e.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                Path = e.TryGetProperty("path", out var directory) ? directory.GetString() ?? "" : "",
            }).ToList();
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Cannot read {path}: {ex.Message}", ex); }
    }

    /// <summary>%VAR% (every OS) and $VAR / ${VAR} (Unix style); an unset variable is an error.</summary>
    internal static string Expand(string value)
    {
        string Lookup(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Environment variable '{name}' used in an external project path is not set.");
        value = Regex.Replace(value, @"%([A-Za-z_][A-Za-z0-9_]*)%", m => Lookup(m.Groups[1].Value));
        value = Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)", m => Lookup(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        if (value.StartsWith('~')) value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
        return value;
    }
}

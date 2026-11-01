using System.Collections.Concurrent;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>
/// Layout settings that belong to one project, read from its own Config/BuildConfig.json: also for an
/// external project, whose other build settings (compilers, Android, ...) come from the application.
/// </summary>
public sealed class ProjectSettings
{
    /// <summary>Directory of prebuilt and vendored libraries (ModulesInfo.ThirdPartyDirectory).</summary>
    public string ThirdPartyDirectory { get; }

    /// <summary>
    /// Base include directories of the project's modules besides Source/ (includeRoots): every module
    /// of the project and every module that sees one of them gets them, like Source/.
    /// </summary>
    public IReadOnlyList<string> IncludeRoots { get; }

    private ProjectSettings(string thirdParty, IReadOnlyList<string> includeRoots)
    {
        ThirdPartyDirectory = thirdParty;
        IncludeRoots = includeRoots;
    }

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ProjectSettings Settings)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings of the project rooted at <paramref name="projectDirectory"/>. Without "thirdPartyDirectory":
    /// &lt;project&gt;/ThirdParty, or &lt;project&gt;/Source/ThirdParty when only that one exists.
    /// </summary>
    public static ProjectSettings For(string projectDirectory)
    {
        projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        string config = Path.Combine(projectDirectory, "Config", "BuildConfig.json");
        DateTime stamp = File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue;
        if (Cache.TryGetValue(projectDirectory, out var cached) && cached.Stamp == stamp) return cached.Settings;
        var settings = Read(projectDirectory, config);
        Cache[projectDirectory] = (stamp, settings);
        return settings;
    }

    private static ProjectSettings Read(string project, string config)
    {
        string? thirdParty = null;
        var roots = new List<string>();
        if (File.Exists(config))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllText(config), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException ex) { throw new InvalidOperationException($"Cannot read {config}: {ex.Message}", ex); }
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("thirdPartyDirectory", out var value))
                {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                        throw new InvalidOperationException($"{config}: \"thirdPartyDirectory\" must be a non-empty path.");
                    thirdParty = Resolve(project, value.GetString()!);
                    if (!Directory.Exists(thirdParty))
                        throw new InvalidOperationException($"{config}: thirdPartyDirectory '{thirdParty}' does not exist.");
                }
                if (root.TryGetProperty("includeRoots", out var list))
                {
                    if (list.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"{config}: \"includeRoots\" must be a list of directories.");
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                            throw new InvalidOperationException($"{config}: \"includeRoots\" entries must be non-empty paths.");
                        string directory = Resolve(project, item.GetString()!);
                        // A typo would only surface as missing headers much later.
                        if (!Directory.Exists(directory))
                            throw new InvalidOperationException($"{config}: include root '{directory}' does not exist.");
                        if (!roots.Contains(directory, StringComparer.OrdinalIgnoreCase)) roots.Add(directory);
                    }
                }
            }
        }
        if (thirdParty == null)
        {
            string top = Path.Combine(project, "ThirdParty"), inSource = Path.Combine(project, "Source", "ThirdParty");
            thirdParty = !Directory.Exists(top) && Directory.Exists(inSource) ? inSource : top;
        }
        return new ProjectSettings(thirdParty, roots);
    }

    private static string Resolve(string project, string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExternalProjects.Expand(path), project));
}

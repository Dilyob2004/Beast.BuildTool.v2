using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>Opt-in Clang cache with canonical project/NDK paths and a dependency-set index.</summary>
public sealed class PortableCompileCache(AccelerationConfig config, Func<string, string> observeInput)
{
    private sealed record Manifest(List<ArtifactState> Inputs, List<ArtifactState> Outputs);
    private readonly ConcurrentDictionary<string, string> tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> inventories = new(StringComparer.OrdinalIgnoreCase);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private bool Enabled(CommandContext a) => config.CacheEnabled && config.PortableClangCache && a.Cacheable && a.Reusable &&
        a.PortableToolchainRoot != null && a.DependencyFormat == DependencyFormat.Makefile && !string.IsNullOrWhiteSpace(config.SharedCacheDirectory);
    private static IEnumerable<(string Path, string Token)> Roots(CommandContext a) => new[] {
        (Path.TrimEndingDirectorySeparator(GlobalEnvironment.WorkspacePath), "/__beast_project"),
        (Path.TrimEndingDirectorySeparator(a.PortableToolchainRoot!), "/__beast_ndk"),
        (Path.TrimEndingDirectorySeparator(GlobalEnvironment.WorkingDirectory), "/__beast_builder")
    }.OrderByDescending(r => r.Item1.Length);
    private static string Encode(string text, CommandContext a, bool depfile = false)
    {
        foreach (var (path, token) in Roots(a))
            foreach (string spelling in new[] { path.Replace('\\', '/'), path }.Distinct())
                text = text.Replace(depfile ? spelling.Replace(" ", "\\ ") : spelling, token, StringComparison.OrdinalIgnoreCase);
        return text;
    }
    private static string Decode(string text, CommandContext a, bool depfile = false)
    {
        foreach (var (path, token) in Roots(a))
            text = text.Replace(token, depfile ? path.Replace('\\', '/').Replace(" ", "\\ ") : path.Replace('\\', '/'), StringComparison.Ordinal);
        return text;
    }
    private static string EncodePath(string path, CommandContext a)
    {
        string full = Path.GetFullPath(path).Replace('\\', '/');
        foreach (var (root, token) in Roots(a))
        {
            string prefix = root.Replace('\\', '/');
            if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return token;
            if (full.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return token + full[prefix.Length..];
        }
        return full;
    }
    private string Bucket(CommandContext a)
    {
        string toolDir = Path.GetDirectoryName(a.CompilerPath)!;
        string identity = tools.GetOrAdd(toolDir, d => BuildCacheSession.HashText(string.Join('\n', Directory.GetFiles(d)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".exe" or ".dll" or ".cfg").Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetFileName(p) + ":" + BuildCacheSession.HashFile(p)))));
        var includeDirs = a.SearchDirectories.Concat(new[] { "INCLUDE", "CPATH", "CPLUS_INCLUDE_PATH", "C_INCLUDE_PATH" }
            .SelectMany(n => (Environment.GetEnvironmentVariable(n) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)));
        string search = string.Join('\n', includeDirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(d =>
            EncodePath(d, a) + ":" + inventories.GetOrAdd(d, p => BuildCacheSession.HashText(Directory.Exists(p) ? string.Join('\n', Directory.EnumerateFiles(p, "*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is not (".c" or ".cc" or ".cpp" or ".cxx" or ".cs" or ".o" or ".obj" or ".pdb" or ".d"))
                .Select(f => Path.GetRelativePath(p, f).Replace('\\', '/')).Order(StringComparer.Ordinal)) : "missing"))));
        string environment = string.Join('\n', new[] { "PATH", "INCLUDE", "LIB", "LIBPATH", "CL", "_CL_", "CPATH", "CPLUS_INCLUDE_PATH", "C_INCLUDE_PATH", "LIBRARY_PATH", "SOURCE_DATE_EPOCH" }
            .Select(n => n + "=" + Encode(Json(Environment.GetEnvironmentVariable(n)), a)));
        string declared = string.Join('\n', a.DeclaredInputs.Select(p => EncodePath(p, a) + ":" + observeInput(p)).Order(StringComparer.Ordinal));
        string key = BuildCacheSession.HashText("portable-clang-v1\n" + identity + "\n" + EncodePath(a.CompilerPath, a) + "\n" +
            Encode(a.Arguments, a) + "\n" + search + "\n" + environment + "\n" + declared);
        return Path.Combine(Path.GetFullPath(config.SharedCacheDirectory, GlobalEnvironment.WorkspacePath), "portable-clang-v1", key);
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static List<ArtifactState> CanonicalInputs(IEnumerable<ArtifactState> inputs, CommandContext a) => inputs
        .Select(f => new ArtifactState(EncodePath(f.Path, a), f.Hash)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    public bool Restore(CommandContext a)
    {
        if (!Enabled(a)) return false;
        try
        {
            string bucket = Bucket(a);
            if (!Directory.Exists(bucket)) return false;
            // One group per dependency path set, not one scan per historical header revision.
            foreach (string group in Directory.GetDirectories(bucket).Where(p => IsHash(Path.GetFileName(p))))
            {
                string[]? paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(group, "dependencies.json")));
                if (paths == null || paths.Any(string.IsNullOrWhiteSpace) || BuildCacheSession.HashText(Json(paths)) != Path.GetFileName(group)) continue;
                var current = paths.Select(p => new ArtifactState(p, observeInput(Path.GetFullPath(Decode(p, a))))).ToList();
                if (current.Any(i => i.Hash == "missing")) continue;
                string entry = Path.Combine(group, BuildCacheSession.HashText(Json(current)));
                if (!File.Exists(Path.Combine(entry, "manifest.json"))) continue;
                var state = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(entry, "manifest.json")));
                if (state?.Inputs == null || state.Outputs == null || state.Outputs.Any(f => f == null) ||
                    Json(state.Inputs) != Json(current) || state.Outputs.Count != a.ProducedFiles.Count) continue;
                if (state.Outputs.Where((f, i) => f.Path != EncodePath(a.ProducedFiles[i], a) || f.Hash != BuildCacheSession.HashFile(Path.Combine(entry, i + ".blob"))).Any()) continue;
                if (BuildCacheSession.VolatileInputs(current.Select(f => new ArtifactState(Decode(f.Path, a), f.Hash)))) continue;
                for (int i = 0; i < a.ProducedFiles.Count; ++i)
                {
                    string destination = a.ProducedFiles[i];
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (destination == a.DependencyFile) File.WriteAllText(destination, Decode(File.ReadAllText(Path.Combine(entry, i + ".blob")), a, true), new UTF8Encoding(false));
                    else File.Copy(Path.Combine(entry, i + ".blob"), destination, true);
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Log.TraceWarning($"Portable cache read skipped: {ex.Message}"); }
        return false;
    }
    public void Store(CommandContext a, ActionState original)
    {
        if (!Enabled(a) || BuildCacheSession.VolatileInputs(original.Inputs)) return;
        string? staging = null;
        try
        {
            var inputs = CanonicalInputs(original.Inputs, a);
            var paths = inputs.Select(f => f.Path).ToArray();
            string group = Path.Combine(Bucket(a), BuildCacheSession.HashText(Json(paths)));
            Directory.CreateDirectory(group);
            string dependencyIndex = Path.Combine(group, "dependencies.json");
            if (!File.Exists(dependencyIndex))
            {
                string temp = dependencyIndex + "." + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, Json(paths)); File.Move(temp, dependencyIndex, true);
            }
            string entry = Path.Combine(group, BuildCacheSession.HashText(Json(inputs)));
            if (Directory.Exists(entry)) return;
            staging = Path.Combine(group, ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var outputs = new List<ArtifactState>();
            for (int i = 0; i < a.ProducedFiles.Count; ++i)
            {
                string source = a.ProducedFiles[i], blob = Path.Combine(staging, i + ".blob");
                if (source == a.DependencyFile) File.WriteAllText(blob, Encode(File.ReadAllText(source), a, true), new UTF8Encoding(false));
                else File.Copy(source, blob);
                outputs.Add(new(EncodePath(source, a), BuildCacheSession.HashFile(blob)));
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), Json(new Manifest(inputs, outputs)));
            Directory.Move(staging, entry); staging = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Log.TraceWarning($"Portable cache write skipped: {ex.Message}"); }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}

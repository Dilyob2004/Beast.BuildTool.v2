using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beast.BuildTool;

public sealed record ArtifactState(string Path, string Hash);
public sealed record ActionState(string Command, string Search, List<ArtifactState> Inputs, List<ArtifactState> Outputs);

/// <summary>One snapshot per invocation, never a timestamp-only persistent hash cache.</summary>
public sealed class BuildCacheSession
{
    private readonly ConcurrentDictionary<string, Lazy<string>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<string>> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<string>> _contentDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<string>> _inputHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<bool>> _volatileInputs = new(StringComparer.Ordinal);
    private readonly AccelerationConfig _config;
    private readonly PortableCompileCache _portable;
    public BuildCacheSession(AccelerationConfig config) { _config = config; _portable = new(config, ObserveInput); }
    private string ObserveInput(string path) => _inputHashes.GetOrAdd(path, p => new Lazy<string>(() => HashFile(p))).Value;
    private bool MatchesInputs(IEnumerable<ArtifactState> inputs) => inputs.All(f => f.Hash != "missing" &&
        _inputHashes.GetOrAdd(f.Path, p => new Lazy<string>(() => HashFile(p))).Value == f.Hash);
    private bool HasVolatileInputs(IEnumerable<ArtifactState> inputs) => inputs.Any(f =>
        _volatileInputs.GetOrAdd(f.Path + "\n" + f.Hash, _ => new Lazy<bool>(() => VolatileInputs([f]))).Value);
    public void InvalidateOutputs(IEnumerable<string> outputs)
    {
        foreach (string output in outputs)
        {
            _inputHashes.TryRemove(output, out _);
            foreach (string directory in _directories.Keys)
                if (output.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) _directories.TryRemove(directory, out _);
            for (string? parent = Path.GetDirectoryName(Path.GetFullPath(output)); parent != null; parent = Path.GetDirectoryName(parent))
                _listings.TryRemove(parent, out _);
            foreach (string directory in _contentDirectories.Keys)
                if (output.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) _contentDirectories.TryRemove(directory, out _);
        }
    }
    public void ValidateInputSnapshot()
    {
        foreach (var input in _inputHashes)
            if (input.Value.Value != HashFile(input.Key))
                throw new IOException($"Input changed during build: {input.Key}. Run the build again.");
        foreach (var directory in _directories)
            if (directory.Value.Value != Inventory(directory.Key))
                throw new IOException($"Include search directory changed during build: {directory.Key}. Run the build again.");
        foreach (var listing in _listings)
            if (listing.Value.IsValueCreated && !SameListing(listing.Value.Value, ListDirectory(listing.Key)))
                throw new IOException($"Include search directory changed during build: {listing.Key}. Run the build again.");
    }
    private static bool SameListing(HashSet<string>? a, HashSet<string>? b) => a == null ? b == null : b != null && a.SetEquals(b);
    public string Explain(CommandContext action, string key)
    {
        if (!action.Reusable) return "volatile compiler arguments";
        var state = ReadState(action.StatePath);
        if (state == null) return "no valid previous action state";
        if (state.Command != key) return "compiler, arguments, environment or declared inputs changed";
        if (state.Search != StateSearch(action, state)) return "include search paths, a header that could shadow a used one, or library contents changed";
        if (HasVolatileInputs(state.Inputs)) return "volatile time macro in inputs";
        var input = state.Inputs.FirstOrDefault(f => HashFile(f.Path) != f.Hash);
        if (input != null) return "input content changed: " + input.Path;
        var output = state.Outputs.FirstOrDefault(f => HashFile(f.Path) != f.Hash);
        return output != null ? "output missing or changed: " + output.Path : "output set changed";
    }
    public static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string HashFile(string path)
    {
        if (!File.Exists(path)) return "missing";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();
    public static List<ArtifactState> Snapshot(IEnumerable<string> files) => files.Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(p => new ArtifactState(p, HashFile(p))).ToList();
    public static bool Matches(IEnumerable<ArtifactState> files) => files.All(f => f.Hash != "missing" && HashFile(f.Path) == f.Hash);
    public string CommandKey(CommandContext action)
    {
        string directory = Path.GetDirectoryName(action.CompilerPath)!;
        string tool = _tools.GetOrAdd(directory, p => new Lazy<string>(() => HashText(string.Join('\n',
            Snapshot(Directory.EnumerateFiles(p).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll")))))).Value;
        string environment = string.Join('\n', new[] { "PATH", "INCLUDE", "LIB", "LIBPATH", "CL", "_CL_", "LINK", "CPATH", "CPLUS_INCLUDE_PATH", "C_INCLUDE_PATH", "LIBRARY_PATH", "SOURCE_DATE_EPOCH" }
            .Select(n => n + "=" + JsonSerializer.Serialize(Environment.GetEnvironmentVariable(n))));
        if (action.Type == CommandType.Custom)
            environment = string.Join('\n', Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .OrderBy(p => p.Key.ToString(), StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value)));
        if (action.EnvironmentOverrides != null) environment += "\noverrides=" + JsonSerializer.Serialize(action.EnvironmentOverrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
        return HashText("BeastAction-v3\n" + (action.LocalOnly ? "machine=" + Environment.MachineName + "\n" : "") + Normalize(action.CompilerPath) + "\n" + tool + "\n" + action.Arguments + "\n" +
            (action.WorkingDirectory ?? GlobalEnvironment.WorkingDirectory) + "\n" + (action.ArgumentList == null ? "" : JsonSerializer.Serialize(action.ArgumentList) + "\n") +
            "VSLANG=1033\n" + environment + "\n" + string.Join('\n', action.DeclaredInputs.Select(Normalize).Order(StringComparer.Ordinal)));
    }
    // Files that are never resolved through an include search path. Adding or removing them
    // (a new .cpp, a .Build.cs, object files) must not invalidate every compile action.
    private static bool IsIncludable(string path) => Path.GetExtension(path).ToLowerInvariant() is not
        (".c" or ".cc" or ".cpp" or ".cxx" or ".cs" or ".o" or ".obj" or ".pdb" or ".d" or ".rsp" or ".response");
    // Headers and translation units: the files whose directories and spellings take part in
    // include resolution. Tool binaries, response files and PCH outputs do not.
    private static bool IsHeaderOrSource(string path) => Path.GetExtension(path).ToLowerInvariant() is
        "" or ".h" or ".hh" or ".hpp" or ".hxx" or ".h++" or ".inl" or ".ipp" or ".inc" or ".tcc" or ".tpp" or ".def" or ".ixx" or ".cppm"
        or ".c" or ".cc" or ".cpp" or ".cxx" or ".m" or ".mm";
    private static string Inventory(string directory) => Directory.Exists(directory) ? HashText(directory + "\n" + string.Join('\n',
        Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
        .Where(IsIncludable).Select(Normalize).Order(StringComparer.Ordinal))) : directory + ":missing";

    // One non-recursive listing per directory and invocation: names of files and subdirectories.
    private readonly ConcurrentDictionary<string, Lazy<HashSet<string>?>> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<bool>> _probingInputs = new(StringComparer.Ordinal);
    private HashSet<string>? Listing(string directory) => _listings.GetOrAdd(directory, d => new Lazy<HashSet<string>?>(() => ListDirectory(d))).Value;
    private static HashSet<string>? ListDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private bool Exists(string root, string relative)
    {
        string current = root;
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..") return File.Exists(Path.Combine(root, relative)); // unusual spelling: plain check
            if (Listing(current)?.Contains(segment) != true) return false;
            current = Path.Combine(current, segment);
        }
        return true;
    }

    /// <summary>
    /// Inputs inside the project that probe for files which may not exist (__has_include):
    /// the compiler does not report failed lookups, so such actions keep a full inventory.
    /// </summary>
    private bool ProbesIncludes(string path)
    {
        string hash = ObserveInput(path);
        return _probingInputs.GetOrAdd(path + "\n" + hash, _ => new Lazy<bool>(() =>
        {
            try { return File.Exists(path) && File.ReadAllText(path).Contains("__has_include", StringComparison.Ordinal); }
            catch (IOException) { return true; }
        })).Value;
    }

    private static List<string> SearchPaths(CommandContext action) => action.SearchDirectories
        .Concat(new[] { "INCLUDE", "CPATH", "CPLUS_INCLUDE_PATH", "C_INCLUDE_PATH" }
            .SelectMany(n => (Environment.GetEnvironmentVariable(n) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)))
        .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p, GlobalEnvironment.WorkingDirectory))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Include search state relevant to an action. For compiler actions with dependency files it covers
    /// only the headers they actually used: for every spelling under which a used header can be reached
    /// from a search directory, whether a file with that spelling exists in each search directory and
    /// each includer directory. A new header that would shadow a used one changes the key; unrelated
    /// new files (a new .cpp, a header nobody includes) do not. Actions without a dependency file, or
    /// whose project inputs use __has_include, keep the conservative recursive inventory.
    /// </summary>
    public string SearchKey(CommandContext action) => SearchKey(action, action.CurrentInputs());
    public string SearchKey(CommandContext action, IEnumerable<string> inputs)
    {
        var directories = SearchPaths(action);
        var allInputs = inputs.Select(p => Path.GetFullPath(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inputList = allInputs.Where(IsHeaderOrSource).ToList();
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GlobalEnvironment.WorkspacePath)) + Path.DirectorySeparatorChar;
        bool precise = action.DependencyFile != null && action.Type is CommandType.Compile or CommandType.GeneratePCH &&
            !inputList.Any(i => i.StartsWith(project, StringComparison.OrdinalIgnoreCase) && ProbesIncludes(i));
        string search;
        if (!precise)
            search = "inventory\n" + string.Join('\n', directories.Select(p => _directories.GetOrAdd(p, p => new Lazy<string>(() => Inventory(p))).Value));
        else
        {
            var spellings = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in inputList)
                foreach (string directory in directories)
                    if (input.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        spellings.Add(input[(directory.Length + 1)..].Replace('\\', '/'));
            var candidates = directories.Concat(inputList.Select(i => Path.GetDirectoryName(i)!))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var present = new System.Text.StringBuilder("precise\n");
            present.Append(string.Join('\n', directories.Select(Normalize))).Append('\n');
            foreach (string spelling in spellings)
                foreach (string candidate in candidates)
                    if (Exists(candidate, spelling)) present.Append(Normalize(candidate)).Append('|').Append(spelling.ToUpperInvariant()).Append('\n');
            search = present.ToString();
        }
        var environmentLibraries = action.Type == CommandType.Link ? new[] { "LIB", "LIBPATH", "LIBRARY_PATH" }
            .SelectMany(n => (Environment.GetEnvironmentVariable(n) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) : [];
        string libraries = string.Join('\n', action.ContentDirectories.Concat(environmentLibraries).Select(p => Path.GetFullPath(p, GlobalEnvironment.WorkingDirectory)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => _contentDirectories.GetOrAdd(p, p => new Lazy<string>(() => Directory.Exists(p) ? HashText(string.Join('\n', Snapshot(
                Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".lib" or ".a" or ".so" or ".o")))) : p + ":missing")).Value));
        return HashText(search + "\n" + libraries);
    }
    public ActionState Capture(CommandContext action, string key)
    {
        var inputs = Snapshot(action.CurrentInputs());
        return new(key, SearchKey(action, inputs.Select(i => i.Path)), inputs, Snapshot(action.ProducedFiles));
    }
    private string StateSearch(CommandContext action, ActionState state) => SearchKey(action, state.Inputs.Select(i => i.Path));
    public static bool VolatileInputs(IEnumerable<ArtifactState> inputs) => inputs.Where(f => Path.GetExtension(f.Path).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hpp" or ".hh" or ".hxx" or ".inl" or ".ipp" or ".inc" or ".i" or ".ii" or ".rsp" or ".response" or "")
        .Any(f => File.Exists(f.Path) && (File.ReadAllText(f.Path) is var text && (text.Contains("__TIME__") || text.Contains("__DATE__") || text.Contains("__TIMESTAMP__"))));
    public bool IsCurrent(CommandContext action, string key)
    {
        var state = ReadState(action.StatePath);
        return action.Reusable && state != null && state.Command == key &&
            state.Outputs.Select(f => Normalize(f.Path)).SequenceEqual(action.ProducedFiles.Select(Normalize).Order(StringComparer.Ordinal)) &&
            MatchesInputs(state.Inputs) && state.Search == StateSearch(action, state) && !HasVolatileInputs(state.Inputs) && Matches(state.Outputs);
    }
    public static ActionState? ReadState(string path)
    {
        try
        {
            var state = File.Exists(path) ? JsonSerializer.Deserialize<ActionState>(File.ReadAllText(path)) : null;
            if (state == null || state.Command == null || state.Search == null || state.Inputs == null || state.Outputs == null ||
                state.Inputs.Concat(state.Outputs).Any(f => f == null || string.IsNullOrWhiteSpace(f.Path) || !Path.IsPathFullyQualified(f.Path) ||
                    f.Hash == null || f.Hash.Length != 64 || f.Hash.Any(c => !Uri.IsHexDigit(c)))) return null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
    public static void WriteState(string path, ActionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, true);
    }
    private IEnumerable<string> CacheRoots()
    {
        if (!_config.CacheEnabled) yield break;
        yield return Path.GetFullPath(_config.CacheDirectory, GlobalEnvironment.WorkspacePath);
        if (!string.IsNullOrWhiteSpace(_config.SharedCacheDirectory)) yield return Path.GetFullPath(_config.SharedCacheDirectory, GlobalEnvironment.WorkspacePath);
    }
    private const string IndexName = "dependency-sets";
    private static string DependencyGroup(string bucket, ActionState state) => Path.Combine(bucket, ".index", IndexName,
        HashText(JsonSerializer.Serialize(state.Inputs.Select(i => i.Path).ToArray())));
    private static void PublishIndex(string bucket, ActionState state, string entry)
    {
        string group = DependencyGroup(bucket, state);
        Directory.CreateDirectory(group);
        void Publish(string path, string content)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content); File.Move(temp, path, true);
        }
        Publish(Path.Combine(group, "paths.json"), JsonSerializer.Serialize(state.Inputs.Select(i => i.Path).ToArray()));
        Publish(Path.Combine(group, HashText(JsonSerializer.Serialize(state.Inputs)) + ".entry"), Path.GetFileName(entry));
    }
    private IEnumerable<string> Candidates(string bucket)
    {
        string index = Path.Combine(bucket, ".index", IndexName);
        if (!Directory.Exists(index)) return Directory.EnumerateDirectories(bucket).Where(p => Path.GetFileName(p).Length == 64);
        var result = new List<string>();
        foreach (string group in Directory.EnumerateDirectories(index))
        {
            try
            {
                var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(group, "paths.json")));
                if (paths == null || HashText(JsonSerializer.Serialize(paths)) != Path.GetFileName(group) || paths.Any(p => !Path.IsPathFullyQualified(p))) continue;
                var inputs = paths.Select(p => new ArtifactState(p, ObserveInput(p))).ToList();
                string pointer = Path.Combine(group, HashText(JsonSerializer.Serialize(inputs)) + ".entry");
                if (!File.Exists(pointer)) continue;
                string id = File.ReadAllText(pointer);
                if (id.Length != 64 || !id.All(Uri.IsHexDigit)) continue;
                string entry = Path.Combine(bucket, id);
                if (Directory.Exists(entry)) result.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            { return Directory.EnumerateDirectories(bucket).Where(p => Path.GetFileName(p).Length == 64); }
        }
        return result;
    }
    public bool Restore(CommandContext action, string key)
    {
        if (!action.Cacheable || !action.Reusable) return false;
        foreach (string root in CacheRoots())
        {
            try
            {
                string bucket = Path.Combine(root, CacheVersion, key);
                if (!Directory.Exists(bucket)) continue;
                foreach (string entry in Candidates(bucket))
                {
                    var state = ReadState(Path.Combine(entry, "manifest.json"));
                    if (state == null || state.Command != key || !MatchesInputs(state.Inputs) || state.Search != StateSearch(action, state) || HasVolatileInputs(state.Inputs)) continue;
                    if (HashText(JsonSerializer.Serialize(state)) != Path.GetFileName(entry)) continue;
                    var outputs = action.ProducedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!state.Outputs.Select(o => Normalize(o.Path)).SequenceEqual(outputs.Select(Normalize))) continue;
                    if (state.Outputs.Where((o, i) => HashFile(Path.Combine(entry, i + ".blob")) != o.Hash).Any()) continue;
                    // Validate every artifact before writing any destination.
                    for (int i = 0; i < outputs.Length; ++i)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputs[i])!);
                        File.Copy(Path.Combine(entry, i + ".blob"), outputs[i], true);
                    }
                    if (!Matches(state.Outputs)) throw new IOException("Cache changed during restoration.");
                    WriteState(action.StatePath, state);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Log.TraceWarning($"Cache read skipped: {ex.Message}"); }
        }
        if (_portable.Restore(action))
        {
            WriteState(action.StatePath, Capture(action, key));
            return true;
        }
        return false;
    }
    // v4: include search keys cover used headers instead of whole directory trees.
    private const string CacheVersion = "v4";
    public void Store(CommandContext action, ActionState state)
    {
        if (!action.Cacheable || !action.Reusable || HasVolatileInputs(state.Inputs)) return;
        _portable.Store(action, state);
        foreach (string root in CacheRoots())
        {
            string? staging = null;
            try
            {
                string bucket = Path.Combine(root, CacheVersion, state.Command);
                string entry = Path.Combine(bucket, HashText(JsonSerializer.Serialize(state)));
                if (Directory.Exists(entry)) { PublishIndex(bucket, state, entry); continue; }
                staging = Path.Combine(bucket, ".tmp-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                for (int i = 0; i < state.Outputs.Count; ++i) File.Copy(state.Outputs[i].Path, Path.Combine(staging, i + ".blob"));
                File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(state));
                Directory.Move(staging, entry);
                staging = null;
                PublishIndex(bucket, state, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.TraceWarning($"Cache write skipped: {ex.Message}"); }
            finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }

    /// <summary>Evicts oldest local entries only. Shared-cache retention belongs to its administrator.</summary>
    public void TrimLocalCache()
    {
        if (!_config.CacheEnabled || _config.LocalCacheLimitMB == 0) return;
        if (_config.LocalCacheLimitMB < 0) throw new InvalidOperationException("LocalCacheLimitMB cannot be negative.");
        string localRoot = Path.GetFullPath(_config.CacheDirectory, GlobalEnvironment.WorkspacePath);
        if (!string.IsNullOrWhiteSpace(_config.SharedCacheDirectory) && Path.TrimEndingDirectorySeparator(localRoot).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_config.SharedCacheDirectory, GlobalEnvironment.WorkspacePath)), StringComparison.OrdinalIgnoreCase)) return;
        // Entries of the previous format (v3) are never read again; they are evicted first, being older.
        var roots = new[] { "v3", CacheVersion }.Select(v => Path.Combine(localRoot, v)).Where(Directory.Exists).ToList();
        if (roots.Count == 0) return;
        try
        {
            static bool IsHash(string name) => name.Length == 64 && name.All(Uri.IsHexDigit);
            // Never recurse through a junction/symlink or remove unrelated contents of a user-specified directory.
            var entries = new List<(string Path, FileInfo[] Files, long Size, DateTime Time)>();
            foreach (string root in roots)
            {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string bucket in Directory.EnumerateDirectories(root).Where(p => IsHash(Path.GetFileName(p))))
            {
                if ((File.GetAttributes(bucket) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string entry in Directory.EnumerateDirectories(bucket).Where(p => IsHash(Path.GetFileName(p))))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(entry).Any()) continue;
                    var files = new DirectoryInfo(entry).GetFiles();
                    if (files.Any(f => (f.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        (f.Name != "manifest.json" && !System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^\d+\.blob$")))) continue;
                    var state = ReadState(Path.Combine(entry, "manifest.json"));
                    if (state == null || state.Command != Path.GetFileName(bucket) ||
                        HashText(JsonSerializer.Serialize(state)) != Path.GetFileName(entry)) continue;
                    entries.Add((entry, files, files.Sum(f => f.Length), Directory.GetLastWriteTimeUtc(entry)));
                }
            }
            }
            long total = entries.Sum(e => e.Size), limit = (long)_config.LocalCacheLimitMB * 1024 * 1024;
            int evicted = 0;
            foreach (var entry in entries.OrderBy(e => e.Time))
            {
                if (total <= limit) break;
                // Exact validated generated entry; failed/racing removals are harmless cache misses.
                try
                {
                    // Delete only the validated artifact files, never recursively remove newly added contents.
                    foreach (var file in entry.Files) file.Delete();
                    Directory.Delete(entry.Path, false);
                    total -= entry.Size;
                    ++evicted;
                }
                catch (IOException) { }
            }
            if (evicted > 0) Log.TraceInfo($"Cache quota: removed {evicted} old local entries; {total / (1024 * 1024)} MB retained.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.TraceWarning($"Cache cleanup skipped: {ex.Message}"); }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Beast.BuildTool;

/// <summary>
/// 'watch': rebuilds a target whenever the sources of the project or of its external projects change.
/// Changes are debounced; one build runs at a time and changes made during a build start another one
/// right after it. A failed build reports its diagnostics and the watch goes on. After a successful
/// build: an optional command (--on-success) and, with --versioned, numbered copies of the target's
/// shared libraries (desktop) or a push of them to the Android device (--device), for hot reload hosts.
/// </summary>
public sealed class Watcher
{
    public sealed record Options(string Project, IReadOnlyList<Platform> Platforms, TargetConfiguration Configuration, string? Target,
        string? OnSuccess, bool Versioned, int DebounceMilliseconds, string? Device, int? MaxBuilds);

    private readonly Options _options;
    private readonly object _gate = new();
    private DateTime _lastChange = DateTime.MinValue;
    private bool _pending;
    private readonly List<string> _changed = [];
    private int _build;

    public Watcher(Options options) => _options = options;

    /// <summary>Directories watched: Source/ and Config/ of the project and of every external project.</summary>
    public static IReadOnlyList<string> WatchedDirectories(string project)
    {
        var roots = new List<string> { Path.GetFullPath(project) };
        roots.AddRange(ExternalProjects.Resolve(project, BuildConfigManager.Config.ExternalProjects).Select(e => e.Directory));
        return roots.SelectMany(r => new[] { Path.Combine(r, "Source"), Path.Combine(r, "Config") }).Where(Directory.Exists).Distinct().ToList();
    }

    /// <summary>Changes Beast itself makes, or editors' temporary files, do not start a build.</summary>
    /// <param name="path">Path relative to the watched directory (directories above it do not count).</param>
    internal static bool IsRelevant(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "Intermediate" or "Binaries" or ".git" or ".vs" or ".idea" or ".gradle" or "build" or "bin" or "obj")) return false;
        string name = Path.GetFileName(path);
        return !(name.StartsWith('.') || name.StartsWith('~') || name.EndsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("4913", StringComparison.Ordinal));
    }

    public async Task<int> RunAsync(CancellationToken token)
    {
        var directories = WatchedDirectories(_options.Project);
        var watchers = directories.Select(directory =>
        {
            var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += (_, e) => Changed(directory, e.FullPath);
            watcher.Created += (_, e) => Changed(directory, e.FullPath);
            watcher.Deleted += (_, e) => Changed(directory, e.FullPath);
            watcher.Renamed += (_, e) => { Changed(directory, e.OldFullPath); Changed(directory, e.FullPath); };
            watcher.Error += (_, e) => { Log.TraceWarning("File watcher overflow: " + e.GetException().Message + " Rebuilding."); Changed(directory, Path.Combine(directory, "overflow")); };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }).ToList();
        try
        {
            Log.TraceInfo($"Watching {string.Join(", ", directories)} (Ctrl+C stops).");
            await BuildAsync("initial build", token);
            while (!token.IsCancellationRequested && (_options.MaxBuilds == null || _build < _options.MaxBuilds))
            {
                await Task.Delay(50, token).ContinueWith(_ => { });
                string[] changed;
                lock (_gate)
                {
                    if (!_pending || (DateTime.UtcNow - _lastChange).TotalMilliseconds < _options.DebounceMilliseconds) continue;
                    _pending = false;
                    changed = _changed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    _changed.Clear();
                }
                string reason = changed.Length == 1 ? Path.GetFileName(changed[0]) : $"{changed.Length} changes";
                await BuildAsync(reason, token);
            }
            return 0;
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private void Changed(string root, string path)
    {
        if (!IsRelevant(Path.GetRelativePath(root, path))) return;
        Log.TraceVerbose($"[watch] Changed: {path}");
        lock (_gate)
        {
            _pending = true;
            _lastChange = DateTime.UtcNow;
            _changed.Add(path);
        }
    }

    private async Task<bool> BuildAsync(string reason, CancellationToken token)
    {
        int number = ++_build;
        var clock = Stopwatch.StartNew();
        Log.TraceInfo($"[watch] Build {number} ({reason})...");
        string? target = _options.Target;
        try
        {
            // Rules are evaluated again for every build: edits of *.Build.cs/*.Target.cs and BuildConfig.json apply.
            BuildConfigManager.LoadConfig(_options.Project);
            await new GenericProjectBuilder().BuildAsync(_options.Project, new DirectoryInfo(_options.Project).Name, _options.Platforms, _options.Configuration, target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.TraceError($"[watch] Build {number} failed after {clock.Elapsed.TotalSeconds:0.0}s: {ex.Message}");
            Log.TraceInfo("[watch] Waiting for changes...");
            return false;
        }
        Log.TraceInfo($"[watch] Build {number} succeeded in {clock.Elapsed.TotalSeconds:0.0}s.");
        try
        {
            var model = ProjectModel.Load(_options.Project, _options.Platforms[0], _options.Configuration);
            var built = model.SelectTarget(target);
            var libraries = _options.Versioned ? await PublishVersionedAsync(model, built, number) : [];
            if (!string.IsNullOrWhiteSpace(_options.OnSuccess)) await RunHookAsync(built, number, libraries, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.TraceError($"[watch] After build {number}: {ex.Message}");
        }
        Log.TraceInfo("[watch] Waiting for changes...");
        return true;
    }

    /// <summary>
    /// Shared libraries of the target and its dependency targets as Name.&lt;n&gt;.dll/.so/.dylib next to
    /// the originals (Binaries/&lt;platform&gt;/&lt;config&gt;/Hot), so a host that loaded the previous copy keeps
    /// it open while the linker rewrites the original. Hot/&lt;Name&gt;.latest names the newest copy. With
    /// Android and --device (or android.defaultAvd's running emulator) the copies are pushed to the
    /// application's files/hot directory with adb and run-as.
    /// </summary>
    private async Task<List<string>> PublishVersionedAsync(ProjectModel model, BuildTarget target, int number)
    {
        var platform = _options.Platforms[0];
        string binaries = Path.Combine(_options.Project, "Binaries", platform.PlatformName, _options.Configuration.ToString());
        var shared = model.TargetDependencies(target).Append(target).Where(t => t.Rules.Type == TargetType.SharedLibrary).ToList();
        if (platform is AndroidPlatform && target.Rules.Type == TargetType.Application) shared.Add(target);   // the app itself is a .so
        string hot = Path.Combine(binaries, "Hot");
        Directory.CreateDirectory(hot);
        var copies = new List<string>();
        foreach (var library in shared.Distinct())
        {
            string extension = platform.SharedLibraryFileExtension;
            string name = platform.LibraryPrefix + library.Name;
            string original = Path.Combine(binaries, name + extension);
            if (!File.Exists(original)) continue;
            string copy = Path.Combine(hot, $"{name}.{number}{extension}");
            File.Copy(original, copy, true);
            string symbols = Path.ChangeExtension(original, ".pdb");
            if (File.Exists(symbols)) File.Copy(symbols, Path.Combine(hot, $"{name}.{number}.pdb"), true);
            File.WriteAllText(Path.Combine(hot, name + ".latest"), Path.GetFileName(copy) + "\n");
            // Older copies are kept: a running host may still have one of them loaded.
            copies.Add(copy);
            Log.TraceInfo($"[watch] {Path.GetRelativePath(_options.Project, copy)}");
        }
        if (platform is AndroidPlatform && copies.Count > 0 && target.Rules.Type == TargetType.Application)
            await PushAndroidAsync(target.Name, copies);
        return copies;
    }

    private async Task PushAndroidAsync(string target, List<string> copies)
    {
        string sdk = Environment.ExpandEnvironmentVariables(BuildConfigManager.Config.Android.SdkRoot);
        if (string.IsNullOrWhiteSpace(sdk)) sdk = HostPlatform.DefaultAndroidSdk();
        string adb = Path.Combine(sdk, "platform-tools", HostPlatform.Executable("adb"));
        if (!File.Exists(adb)) { Log.TraceWarning($"[watch] adb not found ({adb}); libraries not pushed."); return; }
        string package = AndroidApplication.ApplicationId(_options.Project, target);
        var serial = _options.Device is { Length: > 0 } device ? new[] { "-s", device } : [];
        foreach (string copy in copies)
        {
            string name = Path.GetFileName(copy);
            string staging = "/data/local/tmp/beast-hot-" + name;
            foreach (var arguments in new[]
            {
                serial.Concat(new[] { "push", copy, staging }).ToArray(),
                serial.Concat(new[] { "shell", "run-as", package, "mkdir", "-p", "files/hot" }).ToArray(),
                serial.Concat(new[] { "shell", "run-as", package, "cp", staging, "files/hot/" + name }).ToArray(),
                serial.Concat(new[] { "shell", "rm", "-f", staging }).ToArray(),
            })
            {
                var (exit, output) = await IosPlatform.RunAsync(adb, arguments);
                if (exit != 0) { Log.TraceWarning($"[watch] adb {string.Join(' ', arguments)}: {output.Trim()}"); return; }
            }
            Log.TraceInfo($"[watch] Pushed {name} to {package}: files/hot/{name}");
        }
    }

    /// <summary>--on-success: a shell command with BEAST_* variables describing the build.</summary>
    private async Task RunHookAsync(BuildTarget target, int number, List<string> libraries, CancellationToken token)
    {
        var info = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", _options.OnSuccess! } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", _options.OnSuccess! } };
        info.UseShellExecute = false;
        info.WorkingDirectory = _options.Project;
        var platform = _options.Platforms[0];
        info.Environment["BEAST_BUILD"] = number.ToString();
        info.Environment["BEAST_PROJECT"] = _options.Project;
        info.Environment["BEAST_TARGET"] = target.Name;
        info.Environment["BEAST_PLATFORM"] = platform.PlatformName;
        info.Environment["BEAST_CONFIGURATION"] = _options.Configuration.ToString();
        info.Environment["BEAST_BINARIES"] = Path.Combine(_options.Project, "Binaries", platform.PlatformName, _options.Configuration.ToString());
        info.Environment["BEAST_HOT_LIBRARIES"] = string.Join(Path.PathSeparator, libraries);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the --on-success command.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) Log.TraceWarning($"[watch] --on-success exited with code {process.ExitCode}.");
    }
}

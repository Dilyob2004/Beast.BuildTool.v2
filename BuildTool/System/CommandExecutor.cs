using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Beast.BuildTool
{
    public static class CommandUtilities
    {
        public static string WriteResponseFile(string output, string arguments)
        {
            string path = Path.Combine(GlobalEnvironment.IntermediatePath, "Build", "Responses", BuildCacheSession.HashText(Path.GetFullPath(output)) + ".rsp");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != arguments) File.WriteAllText(path, arguments, new UTF8Encoding(false));
            return path;
        }
        /// <summary>
        /// Quotes one argument for the Windows command-line parser used by cl.exe and clang.exe:
        /// embedded quotes are escaped, backslashes are doubled only before a quote.
        /// </summary>
        public static string QuoteArgument(string argument)
        {
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { ++backslashes; continue; }
                if (c == '"') { result.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                result.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }
        public static string ReadProcessOutput(string filename, string ?args = null)
        {
            Process p = new Process
            {
                StartInfo =
                {
                    FileName = filename,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }
            };
            p.Start();
            return p.StandardOutput.ReadToEnd().Trim();
        }
    }
    public enum CommandType
    {
        None = 0,
        Link,
        Compile,
        GeneratePCH,
        Custom,
        Copy
    }

    public class CommandContext
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public CommandType Type { get; } = CommandType.None;
        public CommandStatus Status { get; private set; } = CommandStatus.Pending;

        public IReadOnlyList<string> Output { get; private set; } = new List<string>();
        public bool Async { get; } = true;
        public bool HasErrors => Status == CommandStatus.Failed;
        public List<string> PrerequisiteFiles { get; } = new List<string>();
        public List<string> ProducedFiles { get; } = new List<string>();
        public string Description { get; } = "";

        public string CompilerPath { get; }
        public string Arguments { get; }
        public IReadOnlyList<string>? ArgumentList { get; }
        public string? WorkingDirectory { get; }
        public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; }
        public bool WasSkipped { get; private set; }
        public TimeSpan Duration { get; private set; }
        public TimeSpan ExecutionDuration { get; private set; }
        public long PeakMemoryMB { get; private set; }
        public bool CacheHit { get; private set; }
        public bool RemoteExecuted { get; private set; }
        public string Reason { get; private set; } = "not executed";
        public IReadOnlyList<string> DeclaredInputs => _declaredInputs;
        public IReadOnlyList<string> SearchDirectories { get; }
        public IReadOnlyList<string> ContentDirectories { get; }
        public string? DependencyFile { get; }
        public DependencyFormat DependencyFormat { get; }
        public string? PortableToolchainRoot { get; }
        public bool Cacheable => _allowArtifactCache && Type == CommandType.Compile && DependencyFile != null;
        public bool LocalOnly => !_allowArtifactCache;
        public bool Reusable => !_forceExecute && !Arguments.Contains("__TIME__") && !Arguments.Contains("__DATE__") && !Arguments.Contains("__TIMESTAMP__");
        public string StatePath => _statePath ?? ProducedFiles[0] + ".state.json";
        internal BuildCacheSession? CacheSession { get; set; }
        private IReadOnlyList<string> SavedDependencies()
        {
            if (DependencyFile == null) return [];
            try { return CompilerDependencies.Read(DependencyFile, DependencyFormat); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            { return []; } // Corrupt generated metadata is repaired by restoring/recompiling the action.
        }
        internal void MarkBlocked(string failedDependency) => Reason = "not started: dependency failed (" + failedDependency + ")";
        public IEnumerable<string> CurrentInputs() => _declaredInputs.Concat(SavedDependencies());
        private readonly bool _recreateOutputs;
        private readonly IReadOnlyList<string> _declaredInputs;
        private readonly bool _forceExecute;
        private readonly bool _allowArtifactCache;
        private readonly string? _statePath;
        private readonly Func<CancellationToken, Task>? _execute;
        private string? _preparedKey;
        public bool TryReuse()
        {
            if (_preparedKey != null) return WasSkipped || CacheHit;
            var timer = Stopwatch.StartNew();
            try
            {
                if (Type is CommandType.Custom or CommandType.Copy)
                    foreach (string input in _declaredInputs)
                        if (!File.Exists(input)) throw new FileNotFoundException($"Missing input for {Description}", input);
                var cache = CacheSession ??= new BuildCacheSession(BuildConfigManager.Config.Acceleration);
                _preparedKey = cache.CommandKey(this);
                if (cache.IsCurrent(this, _preparedKey))
                { WasSkipped = true; Reason = "content and outputs unchanged"; }
                else if (cache.Restore(this, _preparedKey))
                { CacheHit = true; Reason = "restored verified compile artifacts"; Log.TraceInfo($"Cache hit: {Description}"); }
                if (WasSkipped || CacheHit) { Status = CommandStatus.Completed; return true; }
                return false;
            }
            catch (Exception ex)
            {
                _preparedKey = null;
                Status = CommandStatus.Failed;
                Reason = "Preflight failed: " + ex.Message;
                Output = [ex.Message];
                throw;
            }
            finally { Duration += timer.Elapsed; }
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {

            Status = CommandStatus.Running;
            var timer = Stopwatch.StartNew();
            var previousDuration = Duration;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cache = CacheSession ??= new BuildCacheSession(BuildConfigManager.Config.Acceleration);
                if (TryReuse()) return;
                string key = _preparedKey!;
                Reason = cache.Explain(this, key);
                Log.TraceVerbose($"Build reason: {Reason}");
                var before = BuildCacheSession.Snapshot(CurrentInputs().Where(File.Exists));
                var started = DateTime.UtcNow;

                Log.TraceInfo(Description);
                foreach (string outputPath in ProducedFiles) Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                // A failed/interrupted attempt must never leave a valid up-to-date marker.
                if (File.Exists(StatePath)) File.Delete(StatePath);

                // llvm-ar updates an existing archive, retaining removed object members otherwise.
                if (_recreateOutputs)
                    foreach (string produced in ProducedFiles) if (File.Exists(produced)) File.Delete(produced);
                var executionTimer = Stopwatch.StartNew();
                try
                {
                    if (_execute != null) await _execute(cancellationToken);
                    else if (Cacheable && !ProducedFiles.Any(p => p.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(BuildConfigManager.Config.Acceleration.WorkerQueueDirectory))
                    {
                        await SharedQueueWorker.DispatchAsync(this, cancellationToken);
                        RemoteExecuted = true;
                    }
                    else if (!await RunLocalAsync(cancellationToken)) return;
                }
                finally { ExecutionDuration = executionTimer.Elapsed; }
                foreach (string produced in ProducedFiles)
                    if (!File.Exists(produced)) throw new IOException($"Command did not produce '{produced}'.");
                if (DependencyFile != null) _ = CompilerDependencies.Read(DependencyFile, DependencyFormat);
                var state = cache.Capture(this, key);
                if (!BuildCacheSession.Matches(before) || state.Inputs.Where(i => !before.Any(b => b.Path.Equals(i.Path, StringComparison.OrdinalIgnoreCase)))
                    .Any(i => File.GetLastWriteTimeUtc(i.Path) > started))
                    throw new IOException("Inputs changed during compilation. Run the build again; result was not cached.");
                BuildCacheSession.WriteState(StatePath, state);
                cache.Store(this, state);
                Status = CommandStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                Status = CommandStatus.Canceled;
                Output = new List<string> { "Operation was canceled" };
            }
            catch (Exception ex)
            {
                Status = CommandStatus.Failed;
                Output = new List<string> { ex.Message };
                Log.TraceError(ex.Message);
            }
            finally { Duration = previousDuration + timer.Elapsed; }
        }

        private async Task<bool> RunLocalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(CompilerPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = WorkingDirectory ?? GlobalEnvironment.WorkingDirectory
            };
            if (ArgumentList != null) foreach (string argument in ArgumentList) info.ArgumentList.Add(argument);
            else info.Arguments = Arguments;
            if (EnvironmentOverrides != null)
                foreach (var pair in EnvironmentOverrides)
                    if (pair.Value == null) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
            info.Environment["VSLANG"] = "1033";
            using var process = Process.Start(info) ?? throw new IOException($"Could not start {CompilerPath}");
            var stdout = ProcessDiagnostics.ReadAsync(process.StandardOutput, ProcessDiagnostics.Emit);
            var stderr = ProcessDiagnostics.ReadAsync(process.StandardError, ProcessDiagnostics.Emit);
            try
            {
                var exit = process.WaitForExitAsync(cancellationToken);
                while (!exit.IsCompleted)
                {
                    try
                    {
                        process.Refresh();
                        PeakMemoryMB = Math.Max(PeakMemoryMB, (process.PeakWorkingSet64 + 1048575) / 1048576);
                    }
                    catch (InvalidOperationException) { } // Process can exit during sampling.
                    catch (System.ComponentModel.Win32Exception) { } // Metrics are best effort, not a build failure.
                    await Task.WhenAny(exit, Task.Delay(100, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await exit;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                throw;
            }
            Output = new[] { await stdout, await stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (process.ExitCode == 0) return true;
            Status = CommandStatus.Failed;
            Log.TraceError($"{Description}: process exited with code {process.ExitCode}.");
            return false;
        }
        public CommandContext(
            string compilerPath,
            string arguments,
            CommandType type,
            List<string> producedFiles,
            List<string> prerequisiteFiles,
            bool async,
            string Desc,
            bool recreateOutputs = false,
            string? dependencyFile = null,
            DependencyFormat dependencyFormat = DependencyFormat.None,
            IEnumerable<string>? searchDirectories = null,
            IEnumerable<string>? contentDirectories = null,
            bool forceExecute = false,
            string? portableToolchainRoot = null,
            bool allowArtifactCache = true,
            IReadOnlyList<string>? argumentList = null,
            string? workingDirectory = null,
            IReadOnlyDictionary<string, string?>? environmentOverrides = null,
            string? statePath = null,
            Func<CancellationToken, Task>? execute = null)
        {
            ProducedFiles = producedFiles ?? new List<string>();
            PrerequisiteFiles = prerequisiteFiles?.ToList() ?? new List<string>();
            _declaredInputs = PrerequisiteFiles.ToList();
            DependencyFile = dependencyFile;
            DependencyFormat = dependencyFormat;
            SearchDirectories = (searchDirectories ?? []).ToList();
            ContentDirectories = (contentDirectories ?? []).ToList();
            PrerequisiteFiles.AddRange(SavedDependencies());

            CompilerPath = compilerPath;
            Arguments = arguments;
            Description = Desc;
            Async = async;
            Type = type;
            _recreateOutputs = recreateOutputs;
            _forceExecute = forceExecute;
            PortableToolchainRoot = portableToolchainRoot;
            _allowArtifactCache = allowArtifactCache;
            ArgumentList = argumentList?.ToArray();
            WorkingDirectory = workingDirectory;
            EnvironmentOverrides = environmentOverrides == null ? null : new Dictionary<string, string?>(environmentOverrides, StringComparer.OrdinalIgnoreCase);
            _statePath = statePath;
            _execute = execute;
        }


    }

    public enum CommandStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Canceled
    }

    public class CommandExecutor
    {
        private readonly ConcurrentQueue<CommandContext> _commands = new ConcurrentQueue<CommandContext>();
        private readonly IBuildExecutor _executor;
        private readonly string? _tracePath;
        private CancellationTokenSource? _cts;

        public CommandExecutor(int maxDegreeOfParallelism = 0, string? tracePath = null)
        {
            _executor = new LocalBuildExecutor(maxDegreeOfParallelism, stopOnFirstError: BuildConfigManager.Config.StopOnFirstError);
            _tracePath = tracePath;
        }

        public void AddCommand(CommandContext command) => _commands.Enqueue(command);

        public async Task ExecuteAsync(CancellationToken externalToken = default)
        {
            var graph = new BuildActionGraph(_commands.ToList());
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _cts = cancellation;
            var timer = Stopwatch.StartNew();
            Log.TraceInfo($"Checking {graph.Actions.Count} actions in the build graph...");
            try
            {
                await _executor.ExecuteAsync(graph, cancellation.Token);
                Log.TraceInfo($"Done: {graph.Actions.Count(a => !a.WasSkipped && !a.CacheHit)} executed ({graph.Actions.Count(a => a.RemoteExecuted)} remote), {graph.Actions.Count(a => a.CacheHit)} cache hits, {graph.Actions.Count(a => a.WasSkipped)} up-to-date, {timer.Elapsed.TotalSeconds:F2}s.");
            }
            finally
            {
                _cts = null;
                if (_tracePath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_tracePath)!);
                    File.WriteAllText(_tracePath, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        executor = "Local", elapsedMs = timer.Elapsed.TotalMilliseconds,
                        actions = graph.Actions.Select(a => new
                        {
                            id = a.Id, description = a.Description, status = a.Status.ToString(), skipped = a.WasSkipped,
                            cacheHit = a.CacheHit, remote = a.RemoteExecuted, reason = a.Reason,
                            durationMs = a.Duration.TotalMilliseconds, executionMs = a.ExecutionDuration.TotalMilliseconds,
                            peakMemoryMB = a.PeakMemoryMB,
                            bookkeepingMs = (a.Duration - a.ExecutionDuration).TotalMilliseconds,
                            inputs = a.PrerequisiteFiles, outputs = a.ProducedFiles,
                            dependencies = graph.Dependencies[a].Select(d => d.Id)
                        })
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }

        public void Cancel() => _cts?.Cancel();

        public IReadOnlyList<CommandContext> GetResults() => _commands.ToList();
    }

}

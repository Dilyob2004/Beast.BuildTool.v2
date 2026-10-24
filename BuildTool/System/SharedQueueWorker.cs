using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>Authenticated shared-filesystem compilation. No network listener or shell execution.</summary>
public static class SharedQueueWorker
{
    private sealed record Envelope(string Payload, string Mac);
    private sealed record Job(string Id, DateTime Expires, string Compiler, string Arguments, string WorkingDirectory,
        string Project, List<string> Outputs, List<ArtifactState> Inputs, List<ArtifactState> ToolFiles,
        string DependencyFile, DependencyFormat Format, Dictionary<string, string?> Environment);
    private sealed record Result(string Id, int ExitCode, string Log, List<ArtifactState> Inputs, List<ArtifactState> Outputs);
    private static readonly string[] EnvironmentNames = ["PATH", "INCLUDE", "LIB", "LIBPATH", "CL", "_CL_", "LINK", "CPATH", "CPLUS_INCLUDE_PATH", "C_INCLUDE_PATH", "LIBRARY_PATH", "SOURCE_DATE_EPOCH"];
    private static byte[] Key(string name)
    {
        string key = Environment.GetEnvironmentVariable(name) ?? "";
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException($"Set {name} to a random secret of at least 32 bytes on both coordinator and worker.");
        return Encoding.UTF8.GetBytes(key);
    }
    private static void Write<T>(string path, T value, byte[] key)
    {
        string payload = JsonSerializer.Serialize(value);
        var envelope = new Envelope(payload, Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload))));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(envelope));
        File.Move(temp, path, true);
    }
    private static T Read<T>(string path, byte[] key)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Worker message exceeds 64 MB.");
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty worker message.");
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(envelope.Payload));
        if (!CryptographicOperations.FixedTimeEquals(mac, Convert.FromHexString(envelope.Mac))) throw new InvalidDataException("Worker message authentication failed.");
        return JsonSerializer.Deserialize<T>(envelope.Payload) ?? throw new InvalidDataException("Empty worker payload.");
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static async Task DispatchAsync(CommandContext action, CancellationToken cancellationToken)
    {
        int retries = BuildConfigManager.Config.Acceleration.WorkerRetryCount;
        if (retries < 0 || retries > 10) throw new InvalidOperationException("WorkerRetryCount must be between 0 and 10.");
        for (int attempt = 0; ; ++attempt)
        {
            try { await DispatchAttemptAsync(action, cancellationToken); return; }
            catch (TimeoutException) when (attempt < retries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.TraceWarning($"Worker attempt timed out; requeueing {action.Description} with isolated outputs (retry {attempt + 1}/{retries}).");
            }
        }
    }

    private static async Task DispatchAttemptAsync(CommandContext action, CancellationToken cancellationToken)
    {
        var config = BuildConfigManager.Config.Acceleration;
        if (config.WorkerTimeoutSeconds < 1) throw new InvalidOperationException("WorkerTimeoutSeconds must be positive.");
        byte[] key = Key(config.WorkerKeyEnvironment);
        string queue = Path.GetFullPath(config.WorkerQueueDirectory, GlobalEnvironment.WorkspacePath);
        Directory.CreateDirectory(queue);
        string id = Guid.NewGuid().ToString("N");
        string resultPath = Path.Combine(queue, id + ".result");
        var environment = EnvironmentNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var job = new Job(id, DateTime.UtcNow.AddSeconds(config.WorkerTimeoutSeconds), action.CompilerPath, action.Arguments,
            GlobalEnvironment.WorkingDirectory, GlobalEnvironment.WorkspacePath, action.ProducedFiles.ToList(),
            BuildCacheSession.Snapshot(action.CurrentInputs().Where(File.Exists)),
            BuildCacheSession.Snapshot(Directory.EnumerateFiles(Path.GetDirectoryName(action.CompilerPath)!).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe")),
            action.DependencyFile!, action.DependencyFormat, environment);
        Write(Path.Combine(queue, id + ".request"), job, key);
        try
        {
            while (!File.Exists(resultPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > job.Expires) throw new TimeoutException("Remote compile timed out; no unsafe local retry was started.");
                await Task.Delay(100, cancellationToken);
            }
            var result = Read<Result>(resultPath, key);
            if (result.Id != id) throw new InvalidDataException("Worker result identity mismatch.");
            if (result.ExitCode != 0) throw new InvalidOperationException($"Worker compilation failed ({result.ExitCode}): {result.Log}");
            foreach (string line in result.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries)) ProcessDiagnostics.Emit(line.TrimEnd('\r'));
            if (!BuildCacheSession.Matches(result.Inputs) || !BuildCacheSession.Matches(result.Outputs)) throw new IOException("Worker inputs or outputs differ from coordinator files.");
            string staging = Path.Combine(job.Project, "Intermediate", "Worker", id);
            if (result.Outputs.Count != job.Outputs.Count || result.Outputs.Any(o => !Within(o.Path, staging))) throw new InvalidDataException("Worker returned unexpected output paths.");
            for (int i = 0; i < job.Outputs.Count; ++i)
            {
                string expected = Path.Combine(staging, i + Path.GetExtension(job.Outputs[i]));
                if (!result.Outputs[i].Path.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Worker output ordering mismatch.");
                Directory.CreateDirectory(Path.GetDirectoryName(job.Outputs[i])!);
                File.Copy(expected, job.Outputs[i], true);
            }
        }
        finally { File.WriteAllText(Path.Combine(queue, id + ".cancel"), "coordinator finished"); }
    }

    public static async Task<int> RunAsync(CommandLine args)
    {
        foreach (string required in new[] { "queue", "project-root", "compiler-root" })
            if (!args.HasArgument(required)) throw new ArgumentException($"worker requires --{required}.");
        string queue = Path.GetFullPath(args.GetArgument("queue"));
        string projectRoot = Path.GetFullPath(args.GetArgument("project-root"));
        string compilerRoot = Path.GetFullPath(args.GetArgument("compiler-root"));
        byte[] key = Key(args.HasArgument("key-env") ? args.GetArgument("key-env") : "BEAST_WORKER_KEY");
        int idleSeconds = args.HasArgument("idle-timeout") ? int.Parse(args.GetArgument("idle-timeout")) : 0;
        int reserve = args.HasArgument("reserve-mb") ? int.Parse(args.GetArgument("reserve-mb")) : 1024;
        if (idleSeconds < 0 || reserve < 0) throw new ArgumentException("Worker limits cannot be negative.");
        Directory.CreateDirectory(queue);
        Log.TraceInfo($"Worker ready. Queue: {queue}; project root: {projectRoot}. Only trusted signed jobs are accepted.");
        var idle = Stopwatch.StartNew();
        while (idleSeconds == 0 || idle.Elapsed.TotalSeconds < idleSeconds)
        {
            bool handled = false;
            foreach (string request in Directory.EnumerateFiles(queue, "*.request"))
            {
                string id = Path.GetFileNameWithoutExtension(request);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                // Inspect authenticated routing data BEFORE claiming: heterogeneous workers can share a queue.
                try
                {
                    var candidate = Read<Job>(request, key);
                    if (!Within(candidate.Compiler, compilerRoot)) continue;
                    if (!(candidate.Project.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) || Within(candidate.Project, projectRoot))) continue;
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException)
                { /* Claim and return an authenticated rejection below. Never execute it. */ }
                catch (IOException) { continue; } // Another worker may already have claimed it.
                if (MemoryBudget.AvailableMB() - reserve < 512) break;
                string claimed = request + ".running";
                try { File.Move(request, claimed); } catch (IOException) { continue; }
                try
                {
                    var job = Read<Job>(claimed, key);
                    if (job.Id != id || job.Expires <= DateTime.UtcNow) throw new InvalidDataException("Expired or mismatched job.");
                    if (!Within(job.Compiler, compilerRoot) || Path.GetFileName(job.Compiler).ToLowerInvariant() is not ("cl.exe" or "clang-cl.exe" or "clang++.exe" or "clang++"))
                        throw new InvalidDataException("Compiler is not permitted by this worker.");
                    if (!(job.Project.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) || Within(job.Project, projectRoot)) ||
                        job.Outputs.Any(p => !Within(p, Path.Combine(job.Project, "Intermediate", "Build"))))
                        throw new InvalidDataException("Job outputs are outside the allowed project build directory.");
                    if (!BuildCacheSession.Matches(job.Inputs) || !BuildCacheSession.Matches(job.ToolFiles))
                        throw new IOException("Worker toolchain/inputs do not match coordinator. Use identical tools and shared paths.");
                    var result = await ExecuteJob(job, queue);
                    Write(Path.Combine(queue, id + ".result"), result, key);
                }
                catch (Exception ex) { Write(Path.Combine(queue, id + ".result"), new Result(id, -1, ex.Message, [], []), key); }
                handled = true;
                idle.Restart();
                if (args.HasFlag("once")) return 0;
            }
            if (!handled) await Task.Delay(100);
        }
        return 0;
    }

    private static async Task<Result> ExecuteJob(Job job, string queue)
    {
        string staging = Path.Combine(job.Project, "Intermediate", "Worker", job.Id);
        Directory.CreateDirectory(staging);
        var outputs = job.Outputs.Select((p, i) => Path.Combine(staging, i + Path.GetExtension(p))).ToList();
        string Rewrite(string text)
        {
            foreach (var pair in job.Outputs.Zip(outputs).OrderByDescending(p => p.First.Length)) text = text.Replace(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase);
            return text;
        }
        string arguments = Regex.Replace(job.Arguments, "@\"([^\"]+)\"", match =>
        {
            string response = match.Groups[1].Value;
            if (!job.Inputs.Any(i => i.Path.Equals(response, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Undeclared response file.");
            string destination = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".rsp");
            File.WriteAllText(destination, Rewrite(File.ReadAllText(response)));
            return "@\"" + destination + "\"";
        });
        arguments = Rewrite(arguments);
        var info = new ProcessStartInfo(job.Compiler, arguments) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = job.WorkingDirectory };
        foreach (var pair in job.Environment)
            if (pair.Value == null) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
        info.Environment["VSLANG"] = "1033";
        using var process = Process.Start(info) ?? throw new IOException("Could not launch compiler.");
        using var lifetime = ChildProcessJob.Attach(process);
        var stdout = ProcessDiagnostics.ReadAsync(process.StandardOutput, ProcessDiagnostics.Emit);
        var stderr = ProcessDiagnostics.ReadAsync(process.StandardError, ProcessDiagnostics.Emit);
        while (!process.HasExited)
        {
            if (DateTime.UtcNow >= job.Expires || File.Exists(Path.Combine(queue, job.Id + ".cancel")))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                throw new OperationCanceledException("Remote job canceled or expired.");
            }
            await Task.Delay(50);
        }
        await process.WaitForExitAsync();
        string log = (await stdout) + (await stderr);
        string depfile = outputs[job.Outputs.FindIndex(p => p == job.DependencyFile)];
        var inputs = process.ExitCode == 0 ? BuildCacheSession.Snapshot(job.Inputs.Select(i => i.Path).Concat(CompilerDependencies.Read(depfile, job.Format))) : [];
        if (process.ExitCode == 0 && !BuildCacheSession.Matches(job.Inputs)) throw new IOException("Inputs changed during remote compilation.");
        return new(job.Id, process.ExitCode, log, inputs, outputs.Select(p => new ArtifactState(p, BuildCacheSession.HashFile(p))).ToList());
    }
}

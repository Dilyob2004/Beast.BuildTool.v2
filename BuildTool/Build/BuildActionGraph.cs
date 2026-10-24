namespace Beast.BuildTool;

/// <summary>File-based action dependencies, independent of a local or accelerated executor.</summary>
public sealed class BuildActionGraph
{
    public IReadOnlyList<CommandContext> Actions { get; }
    public IReadOnlyDictionary<CommandContext, HashSet<CommandContext>> Dependencies { get; }

    public BuildActionGraph(IReadOnlyList<CommandContext> actions)
    {
        Actions = actions;
        var producers = new Dictionary<string, CommandContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        foreach (string output in action.ProducedFiles)
        {
            string key = Path.GetFullPath(output);
            if (!producers.TryAdd(key, action))
                throw new InvalidOperationException($"Multiple actions produce '{key}': '{producers[key].Description}' and '{action.Description}'.");
        }
        var dependencies = actions.ToDictionary(a => a, _ => new HashSet<CommandContext>());
        var statePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            string state = Path.GetFullPath(action.StatePath);
            if (producers.ContainsKey(state) || !statePaths.Add(state))
                throw new InvalidOperationException($"Build state path conflicts with another action: {state}");
        }
        foreach (string output in producers.Keys.Concat(statePaths))
            for (string? parent = Path.GetDirectoryName(output); parent != null; parent = Path.GetDirectoryName(parent))
                if (producers.ContainsKey(parent) || statePaths.Contains(parent))
                    throw new InvalidOperationException($"Build output file/directory conflict: {output} and {parent}");
        foreach (var action in actions)
        foreach (string input in action.PrerequisiteFiles)
        {
            if (producers.TryGetValue(Path.GetFullPath(input), out var producer))
                dependencies[action].Add(producer);
            else if (action.Type is CommandType.Custom or CommandType.Copy && !File.Exists(input))
                throw new FileNotFoundException($"Missing input for {action.Description}", input);
            // A removed header from the previous dependency file must trigger the compiler,
            // not fail here: its include may have been removed from the current source too.
        }
        // A generator can temporarily remove/recreate files during execution. Wait for
        // all producers in searched directories before capturing their inventory, even
        // for assets not declared as compilation inputs. This is an ordering edge only:
        // changing an unused asset's contents does not invalidate the compiler action.
        foreach (var action in actions.Where(a => a.Type is CommandType.Compile or CommandType.GeneratePCH or CommandType.Link))
        foreach (string directory in action.SearchDirectories.Concat(action.ContentDirectories))
        {
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            foreach (var producer in producers.Where(p => p.Value.Type == CommandType.Custom && p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                dependencies[action].Add(producer.Value);
        }
        var visited = new HashSet<CommandContext>();
        var path = new List<CommandContext>();
        void Visit(CommandContext action)
        {
            if (path.Contains(action)) throw new InvalidOperationException($"Build action cycle: {string.Join(" -> ", path.Append(action).Select(a => a.Description))}");
            if (!visited.Add(action)) return;
            path.Add(action);
            foreach (var dependency in dependencies[action]) Visit(dependency);
            path.RemoveAt(path.Count - 1);
        }
        foreach (var action in actions) Visit(action);
        Dependencies = dependencies;
    }
}

public interface IBuildExecutor
{
    Task ExecuteAsync(BuildActionGraph graph, CancellationToken cancellationToken);
}

/// <summary>
/// Starts ready actions without compile/link phase barriers. By default a failed action only
/// blocks its own dependents: independent actions still run, and every failure is reported at
/// the end. <paramref name="stopOnFirstError"/> restores the old fail-fast behaviour.
/// </summary>
public sealed class LocalBuildExecutor(int maxParallelJobs, Func<long>? availableMemoryMB = null, bool stopOnFirstError = false) : IBuildExecutor
{
    public async Task ExecuteAsync(BuildActionGraph graph, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int limit = maxParallelJobs > 0 ? maxParallelJobs : Environment.ProcessorCount;
        var settings = BuildConfigManager.Config.Acceleration;
        if (settings.CompileMemoryMB <= 0 || settings.LinkMemoryMB <= 0 || settings.MemoryReserveMB < 0 || settings.MaxMemoryMB < 0)
            throw new InvalidOperationException("Acceleration memory settings must be positive (reserve/maximum may be zero).");
        if (settings.LocalCacheLimitMB < 0) throw new InvalidOperationException("LocalCacheLimitMB cannot be negative.");
        var memory = availableMemoryMB ?? MemoryBudget.AvailableMB;
        long budget = settings.MaxMemoryMB > 0 ? settings.MaxMemoryMB : Math.Max(0, memory() - settings.MemoryReserveMB);
        var cacheSession = new BuildCacheSession(settings);
        foreach (var action in graph.Actions) action.CacheSession = cacheSession;
        var counts = graph.Actions.ToDictionary(a => a, a => graph.Dependencies[a].Count);
        var dependents = graph.Actions.ToDictionary(a => a, _ => new List<CommandContext>());
        foreach (var action in graph.Actions)
            foreach (var dependency in graph.Dependencies[action]) dependents[dependency].Add(action);
        var ready = new Queue<CommandContext>(graph.Actions.Where(a => counts[a] == 0));
        int completed = 0; // actions that reached a final state: succeeded, failed or blocked
        var failed = new List<CommandContext>();
        var blocked = new HashSet<CommandContext>();
        void Block(CommandContext failedAction)
        {
            var pending = new Stack<(CommandContext Action, string Cause)>();
            pending.Push((failedAction, failedAction.Description));
            while (pending.Count > 0)
            {
                var (action, cause) = pending.Pop();
                foreach (CommandContext dependent in dependents![action])
                    if (blocked!.Add(dependent)) { dependent.MarkBlocked(cause); ++completed; pending.Push((dependent, cause)); }
            }
        }
        void Fail(CommandContext item, Exception? error = null)
        {
            if (stopOnFirstError)
            {
                if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                throw new InvalidOperationException($"Build failed: {item.Description}. Dependent actions were not started.");
            }
            failed.Add(item);
            ++completed;
            Block(item);
        }
        var running = new Dictionary<Task, CommandContext>();
        var learnedMemory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string MemoryKey(CommandContext action) => action.CompilerPath + "|" + action.Type;
        long Cost(CommandContext action) => Math.Max(MemoryBudget.Cost(action, settings), learnedMemory.GetValueOrDefault(MemoryKey(action)));
        void Complete(CommandContext item)
        {
            if (!item.WasSkipped) cacheSession.InvalidateOutputs(item.ProducedFiles);
            if (item.PeakMemoryMB > 0) learnedMemory[MemoryKey(item)] = Math.Max(learnedMemory.GetValueOrDefault(MemoryKey(item)), (long)Math.Ceiling(item.PeakMemoryMB * 1.25));
            ++completed;
            foreach (var dependent in dependents[item]) if (--counts[dependent] == 0) ready.Enqueue(dependent);
        }
        try
        {
            while (completed < graph.Actions.Count)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int completedBefore = completed;
                long reserved = running.Values.Sum(Cost);
                int candidates = ready.Count;
                while (candidates-- > 0 && running.Count < limit)
                {
                    var action = ready.Dequeue();
                    bool reused;
                    try { reused = action.TryReuse(); }
                    catch (Exception ex) when (ex is not OperationCanceledException && !stopOnFirstError)
                    { Log.TraceError($"{action.Description}: {ex.Message}"); Fail(action, ex); continue; }
                    if (reused) { Complete(action); continue; }
                    long required = Cost(action);
                    if (required > budget - reserved || required > memory() - settings.MemoryReserveMB) { ready.Enqueue(action); continue; }
                    reserved += required;
                    // Bound filesystem preflight; compiler processes still execute concurrently.
                    // Parallel preflight increased disk contention in the large-include benchmark.
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    async Task Run()
                    {
                        try { await action.ExecuteAsync(cancellation.Token); completion.SetResult(); }
                        catch (Exception ex) { completion.SetException(ex); }
                    }
                    running.Add(completion.Task, action);
                    _ = Run();
                }
                if (running.Count == 0)
                {
                    if (completed == graph.Actions.Count) break;
                    if (completed > completedBefore) continue; // Reused producers unlocked new consumers.
                    throw new InvalidOperationException("Not enough memory for a ready build action. Adjust acceleration memory budget/reserve or close other applications.");
                }
                Task finished = await Task.WhenAny(running.Keys);
                var item = running[finished];
                running.Remove(finished);
                await finished;
                if (item.Status == CommandStatus.Canceled) throw new OperationCanceledException(cancellation.Token);
                if (item.Status != CommandStatus.Completed) { Fail(item); continue; }
                Complete(item);
            }
            if (failed.Count > 0)
            {
                var message = new System.Text.StringBuilder($"Build failed: {failed.Count} action(s) failed");
                if (blocked.Count > 0) message.Append($", {blocked.Count} dependent action(s) were not started");
                message.Append(':');
                foreach (var item in failed) message.Append(Environment.NewLine).Append("  ").Append(item.Description);
                throw new InvalidOperationException(message.ToString());
            }
            cacheSession.ValidateInputSnapshot();
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(running.Keys);
            cacheSession.TrimLocalCache();
        }
    }
}

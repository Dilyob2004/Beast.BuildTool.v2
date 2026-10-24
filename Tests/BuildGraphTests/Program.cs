using Beast.BuildTool;
using System.Diagnostics;
using System.Reflection;

if (args.FirstOrDefault() == "echo")
{
    File.WriteAllText(args[1], args[2]);
    Console.Out.Write(new string('o', 128 * 1024));
    Console.Error.Write(new string('e', 128 * 1024));
    return 0;
}

if (args.FirstOrDefault() == "cc")
{
    // cc <obj> <dep> <source> <include dirs...>: resolves #include "x" like a compiler and writes a Makefile depfile.
    string obj = args[1], dep = args[2], ccSource = args[3];
    var ccDirs = args.Skip(4).ToList();
    var ccDeps = new List<string>();
    void Walk(string file)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "#include \"([^\"]+)\""))
        {
            string? found = new[] { Path.GetDirectoryName(file)! }.Concat(ccDirs).Select(d => Path.Combine(d, m.Groups[1].Value)).FirstOrDefault(File.Exists);
            if (found == null) return;
            if (!ccDeps.Contains(found)) { ccDeps.Add(found); Walk(found); }
        }
    }
    Walk(ccSource);
    File.WriteAllText(obj, string.Join("|", ccDeps.Select(File.ReadAllText)));
    File.WriteAllText(dep, obj.Replace(" ", "\\ ") + ": " + string.Join(" \\\n ", new[] { ccSource }.Concat(ccDeps).Select(p => p.Replace(" ", "\\ "))) + "\n");
    return 0;
}

if (args.FirstOrDefault() == "worker")
{
    await Task.Delay(int.Parse(args[2]));
    if (args[3] == "fail") return 7;
    string output = args[1];
    foreach (string input in args.Skip(4))
        if (!File.Exists(input)) return 8;
    File.WriteAllText(output, string.Join("|", args.Skip(4).Select(File.ReadAllText)) + "done");
    return 0;
}

Log.Init();
try
{
    string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Generated", "Graph " + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(root);
    BuildConfigManager.Config.Acceleration.CacheDirectory = Path.Combine(root, "ExecutionCache");
    string process = Environment.ProcessPath!;
    string prefix = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? "\"" + Assembly.GetExecutingAssembly().Location + "\" " : "";
    CommandContext Action(string name, int delay = 0, bool fail = false, params string[] inputs) => new(
        process, prefix + $"worker \"{Path.Combine(root, name)}\" {delay} {(fail ? "fail" : "ok")} " + string.Join(' ', inputs.Select(p => "\"" + Path.Combine(root, p) + "\"")),
        CommandType.Compile, [Path.Combine(root, name)], inputs.Select(p => Path.Combine(root, p)).ToList(), true, name);
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    void Reject(Action test, string expected)
    {
        try { test(); throw new Exception("Expected graph validation failure"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains(expected), ex.Message); }
    }
    string moduleRoot = Path.Combine(root, "UnityModule"), generatedRoot = Path.Combine(root, "UnityGenerated");
    Directory.CreateDirectory(moduleRoot);
    var moduleSources = new[] { "A.cpp", "B.cpp", "C.cpp", "D.cpp", "Native.c", "Separate.cpp" }
        .Select(p => Path.Combine(moduleRoot, p)).ToList();
    foreach (string file in moduleSources) File.WriteAllText(file, "// fixture");
    File.WriteAllText(Path.Combine(moduleRoot, "Pch.h"), "#pragma once");
    var moduleRules = new ModuleRules { ModuleName = "UnityModule", ModulePath = Path.Combine(moduleRoot, "UnityModule.Build.cs"),
        UseUnityBuild = true, UnityBatchSize = 2, PrecompiledHeaderSource = "Pch.h" };
    moduleRules.UnityExcludedFiles.Add("Separate.cpp");
    var moduleEnvironment = new CPPEnvironment();
    var transformed = ModuleCompilation.Prepare(moduleRules, moduleEnvironment, moduleSources, generatedRoot);
    Check(transformed.Count == 4 && moduleEnvironment.SourcePrerequisites.Count == 2, "Unity batch/exclusion count");
    Check(transformed.Contains(moduleSources[4]) && transformed.Contains(moduleSources[5]), "Unity consumed C/excluded source");
    string unityFile = moduleEnvironment.SourcePrerequisites.Keys.First();
    File.SetLastWriteTimeUtc(unityFile, DateTime.UtcNow.AddHours(-1));
    DateTime unityStamp = File.GetLastWriteTimeUtc(unityFile);
    _ = ModuleCompilation.Prepare(moduleRules, new CPPEnvironment(), moduleSources, generatedRoot);
    Check(File.GetLastWriteTimeUtc(unityFile) == unityStamp, "Unchanged Unity source rewritten");
    var reducedEnvironment = new CPPEnvironment();
    var reduced = ModuleCompilation.Prepare(moduleRules, reducedEnvironment, moduleSources.Where(p => !p.EndsWith("B.cpp")).ToList(), generatedRoot);
    Check(reducedEnvironment.SourcePrerequisites.Values.SelectMany(p => p).All(p => !p.EndsWith("B.cpp")), "Removed source retained in Unity membership");
    Check(!reduced.Contains(Path.Combine(generatedRoot, "Unity_0001.cpp")), "Obsolete Unity unit retained in graph");
    moduleRules.UnityBatchSize = 1;
    Reject(() => ModuleCompilation.Prepare(moduleRules, new CPPEnvironment(), moduleSources, generatedRoot), "UnityBatchSize");
    moduleRules.UnityBatchSize = 2;
    moduleRules.UnityExcludedFiles.Add("typo.cpp");
    Reject(() => ModuleCompilation.Prepare(moduleRules, new CPPEnvironment(), moduleSources, generatedRoot), "not owned");
    moduleRules.UnityExcludedFiles.Remove("typo.cpp");
    moduleRules.PrecompiledHeaderSource = "A.cpp";
    Reject(() => ModuleCompilation.Prepare(moduleRules, new CPPEnvironment(), moduleSources, generatedRoot), "must be a header");
    Reject(() => _ = new BuildActionGraph([Action("same"), Action("same")]), "Multiple actions");
    Reject(() => _ = new BuildActionGraph([Action("a", inputs: ["b"]), Action("b", inputs: ["a"])]), "cycle");
    string customDir = Path.Combine(root, "custom");
    var custom = new CommandContext(process, "", CommandType.Custom, [Path.Combine(customDir, "asset.json")], [], true, "custom",
        statePath: Path.Combine(root, "custom-state.json"));
    var customConsumer = new CommandContext(process, "", CommandType.Compile, [Path.Combine(root, "custom.obj")], [], true, "custom consumer", searchDirectories: [customDir]);
    Check(new BuildActionGraph([customConsumer, custom]).Dependencies[customConsumer].Contains(custom), "Generator inventory race: search directory producer not ordered");
    Reject(() => _ = new BuildActionGraph([Action("state-conflict"), Action("state-conflict.state.json")]), "state path conflicts");
    Reject(() => _ = new BuildActionGraph([Action("file"), Action("file/child")]), "file/directory conflict");
    var nestedState = new CommandContext(process, "", CommandType.Custom, [Path.Combine(root, "valid")], [], true, "state child", statePath: Path.Combine(root, "file", "nested.state"));
    Reject(() => _ = new BuildActionGraph([Action("file"), nestedState]), "file/directory conflict");
    string literal = "%PATH% & literal", echoFile = Path.Combine(root, "literal & output.txt");
    var echo = new CommandContext(process, prefix + $"echo \"{echoFile}\" \"{literal}\"", CommandType.Compile, [echoFile], [], true, "direct process arguments");
    // Graph/process tests must not depend on the developer machine's free RAM.
    // The dedicated memory-admission tests below supply their own small budgets.
    await new LocalBuildExecutor(1, () => 16384).ExecuteAsync(new([echo]), CancellationToken.None);
    Check(File.ReadAllText(echoFile) == literal, "Arguments were interpreted by a command shell");
    Check(echo.Output.Sum(s => s.Length) == 256 * 1024, "Output streams were not drained/retained");
    Check(echo.ExecutionDuration > TimeSpan.Zero && echo.ExecutionDuration <= echo.Duration, "Execution timing incorrect");
    if (OperatingSystem.IsWindows())
    {
        var childInfo = new ProcessStartInfo(process, prefix + $"worker \"{Path.Combine(root, "job-child")}\" 30000 ok")
            { UseShellExecute = false, CreateNoWindow = true };
        using var child = Process.Start(childInfo)!;
        var lifetime = ChildProcessJob.Attach(child)!;
        Check(!child.HasExited, "Job test child exited early");
        lifetime.Dispose();
        Check(child.WaitForExit(5000), "Closing job did not terminate child");
        Check(!File.Exists(Path.Combine(root, "job-child")), "Terminated child produced output");
    }

    // Reverse insertion order. The consumer must wait for its producer, but not for an unrelated slow action.
    var consumer = Action("consumer", inputs: ["producer"]);
    var slow = Action("slow", delay: 1500);
    var producer = Action("producer", delay: 50);
    var graph = new BuildActionGraph([consumer, slow, producer]);
    Check(graph.Dependencies[consumer].SetEquals([producer]), "Wrong graph edge");
    await new LocalBuildExecutor(2, () => 16384).ExecuteAsync(graph, CancellationToken.None);
    Check(File.ReadAllText(Path.Combine(root, "consumer")) == "donedone", "Consumer ran before its input was ready");
    Check(File.GetLastWriteTimeUtc(Path.Combine(root, "consumer")) < File.GetLastWriteTimeUtc(Path.Combine(root, "slow")), "Global phase barrier remains");

    var repeated = new BuildActionGraph([Action("consumer", inputs: ["producer"]), Action("slow", delay: 1500), Action("producer", delay: 50)]);
    await new LocalBuildExecutor(2, () => 16384).ExecuteAsync(repeated, CancellationToken.None);
    Check(repeated.Actions.All(a => a.WasSkipped), "No-op action executed");
    int reuseBudget = BuildConfigManager.Config.Acceleration.MaxMemoryMB;
    BuildConfigManager.Config.Acceleration.MaxMemoryMB = 1;
    try
    {
        var noMemory = new BuildActionGraph([Action("consumer", inputs: ["producer"]), Action("producer", delay: 50)]);
        await new LocalBuildExecutor(2, () => 0).ExecuteAsync(noMemory, CancellationToken.None);
        Check(noMemory.Actions.All(a => a.WasSkipped), "No-op required compiler memory");
    }
    finally { BuildConfigManager.Config.Acceleration.MaxMemoryMB = reuseBudget; }

    var failure = Action("failure", fail: true);
    var blocked = Action("blocked", inputs: ["failure"]);
    var blockedChain = Action("blocked-chain", inputs: ["blocked"]);
    var independent = Action("independent", delay: 300);
    var secondFailure = Action("second-failure", delay: 100, fail: true);
    try
    {
        await new LocalBuildExecutor(2, () => 16384).ExecuteAsync(new([blockedChain, blocked, failure, independent, secondFailure]), CancellationToken.None);
        throw new Exception("Failure was not propagated");
    }
    catch (InvalidOperationException ex)
    {
        Check(blocked.Status == CommandStatus.Pending && blockedChain.Status == CommandStatus.Pending, "Dependent ran after failure");
        Check(independent.Status == CommandStatus.Completed, "Independent action did not run after an unrelated failure");
        Check(ex.Message.Contains("2 action(s) failed") && ex.Message.Contains("2 dependent action(s)"), "Not all failures were reported: " + ex.Message);
        Check(blocked.Reason.Contains("dependency failed"), "Blocked action has no reason");
    }
    var fastFailure = Action("fast-failure", fail: true);
    var fastIndependent = Action("fast-independent", delay: 3000);
    try
    {
        await new LocalBuildExecutor(2, () => 16384, stopOnFirstError: true).ExecuteAsync(new([fastFailure, fastIndependent]), CancellationToken.None);
        throw new Exception("Fail-fast failure was not propagated");
    }
    catch (InvalidOperationException) { Check(fastIndependent.Status != CommandStatus.Completed, "stopOnFirstError did not stop the build"); }

    var missing = Action("missing-consumer", inputs: ["nonexistent"]);
    try { await new LocalBuildExecutor(1, () => 16384).ExecuteAsync(new([missing]), CancellationToken.None); throw new Exception("Missing input accepted"); }
    catch (InvalidOperationException) { Check(missing.Status == CommandStatus.Failed, "Missing input was not reported"); }

    var longRunning = Action("canceled", delay: 5000);
    using (var cancellation = new CancellationTokenSource(200))
    {
        try { await new LocalBuildExecutor(1, () => 16384).ExecuteAsync(new([longRunning]), cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(root, "canceled")), "Canceled worker still produced output"); }
    }
    // Wrong ABI/type is rejected even if the file has the ELF magic and .so suffix.
    string wrongAbi = Path.Combine(root, "libWrong.so");
    byte[] elfHeader = new byte[64];
    elfHeader[0] = 0x7f; elfHeader[1] = (byte)'E'; elfHeader[2] = (byte)'L'; elfHeader[3] = (byte)'F';
    elfHeader[4] = 2; elfHeader[5] = 1; elfHeader[16] = 3; elfHeader[18] = 62;
    File.WriteAllBytes(wrongAbi, elfHeader);
    Reject(() => AndroidPackagePlan.ValidateArm64Library(wrongAbi), "ARM64");
    elfHeader[18] = 183; elfHeader[16] = 1;
    File.WriteAllBytes(wrongAbi, elfHeader);
    Reject(() => AndroidPackagePlan.ValidateArm64Library(wrongAbi), "shared object");
    string depfile = Path.Combine(root, "deps.d");
    string header = Path.Combine(root, "with space.h");
    File.WriteAllText(depfile, "target.o: " + header.Replace('\\', '/').Replace(" ", "\\ ") + " \\\n " + header.Replace('\\', '/').Replace(" ", "\\ "));
    Check(CompilerDependencies.Read(depfile, DependencyFormat.Makefile).SequenceEqual([header]), "Makefile dependency escaping failed");
    var settings = BuildConfigManager.Config.Acceleration;
    int savedMaximum = settings.MaxMemoryMB, savedReserve = settings.MemoryReserveMB;
    try
    {
        settings.MaxMemoryMB = settings.CompileMemoryMB;
        settings.MemoryReserveMB = 0;
        var memoryGraph = new BuildActionGraph([Action("memory-a", delay: 250), Action("memory-b", delay: 250)]);
        var timer = Stopwatch.StartNew();
        await new LocalBuildExecutor(8, () => settings.CompileMemoryMB).ExecuteAsync(memoryGraph, CancellationToken.None);
        Check(timer.ElapsedMilliseconds >= 500, "Memory admission allowed both jobs concurrently");
        var tooLarge = new CommandContext(process, "", CommandType.Link, [Path.Combine(root, "too-large")], [], true, "oversized link");
        var small = Action("fits-budget");
        try
        {
            await new LocalBuildExecutor(1, () => settings.CompileMemoryMB).ExecuteAsync(new([tooLarge, small]), CancellationToken.None);
            throw new Exception("Oversized action accepted");
        }
        catch (InvalidOperationException) { Check(File.Exists(Path.Combine(root, "fits-budget")), "Oversized ready action blocked a smaller one"); }
        settings.MaxMemoryMB = 1;
        try { await new LocalBuildExecutor(8, () => 1).ExecuteAsync(new([Action("memory-denied")]), CancellationToken.None); throw new Exception("Memory limit ignored"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("Not enough memory"), ex.Message); }
    }
    finally { settings.MaxMemoryMB = savedMaximum; settings.MemoryReserveMB = savedReserve; }
    // Quota must evict the oldest complete local entry, never shared or unrelated files.
    var cacheSettings = new AccelerationConfig {
        CacheDirectory = Path.Combine(root, "Cache"), SharedCacheDirectory = Path.Combine(root, "Shared"), LocalCacheLimitMB = 1
    };
    var cache = new BuildCacheSession(cacheSettings);
    string artifact = Path.Combine(root, "artifact.obj");
    File.WriteAllBytes(artifact, new byte[700 * 1024]);
    var cachedAction = new CommandContext(process, "", CommandType.Compile, [artifact], [], true, "cache quota",
        dependencyFile: depfile, dependencyFormat: DependencyFormat.Makefile);
    string firstKey = BuildCacheSession.HashText("first"), secondKey = BuildCacheSession.HashText("second");
    ActionState State(string key) => new(key, cache.SearchKey(cachedAction, []), [], BuildCacheSession.Snapshot([artifact]));
    cache.Store(cachedAction, State(firstKey));
    string firstEntry = Directory.GetDirectories(Path.Combine(cacheSettings.CacheDirectory, "v4", firstKey))
        .Single(p => File.Exists(Path.Combine(p, "manifest.json")));
    Directory.SetLastWriteTimeUtc(firstEntry, DateTime.UtcNow.AddHours(-1));
    cache.Store(cachedAction, State(secondKey));
    string marker = Path.Combine(cacheSettings.CacheDirectory, "keep.txt");
    File.WriteAllText(marker, "not a Beast artifact");
    cache.TrimLocalCache();
    Check(!Directory.Exists(firstEntry), "Quota did not evict oldest entry");
    Check(Directory.GetFiles(cacheSettings.CacheDirectory, "*.blob", SearchOption.AllDirectories).Length == 1, "Quota evicted wrong number of entries");
    Check(Directory.GetFiles(cacheSettings.SharedCacheDirectory, "*.blob", SearchOption.AllDirectories).Length == 2, "Quota modified shared cache");
    Check(File.ReadAllText(marker) == "not a Beast artifact", "Quota removed unrelated data");
    cacheSettings.LocalCacheLimitMB = 0;
    cache.Store(cachedAction, State(firstKey));
    cache.TrimLocalCache();
    Check(Directory.GetFiles(cacheSettings.CacheDirectory, "*.blob", SearchOption.AllDirectories).Length == 2, "Unlimited quota evicted entries");
    Check(cache.Explain(cachedAction, firstKey) == "no valid previous action state", "Missing-state reason incorrect");
    BuildCacheSession.WriteState(cachedAction.StatePath, State(firstKey));
    Check(cache.IsCurrent(cachedAction, firstKey), "Test artifact not current");
    string stableInput = Path.Combine(root, "snapshot.h");
    File.WriteAllText(stableInput, "old");
    var snapshotState = new ActionState(firstKey, cache.SearchKey(cachedAction, [stableInput]), BuildCacheSession.Snapshot([stableInput]), BuildCacheSession.Snapshot([artifact]));
    BuildCacheSession.WriteState(cachedAction.StatePath, snapshotState);
    Check(cache.IsCurrent(cachedAction, firstKey), "Snapshot test setup failed");
    DateTime inputStamp = File.GetLastWriteTimeUtc(stableInput);
    File.WriteAllText(stableInput, "new");
    File.SetLastWriteTimeUtc(stableInput, inputStamp);
    try { cache.ValidateInputSnapshot(); throw new Exception("Mid-build same-timestamp edit was ignored"); }
    catch (IOException) { }
    cache.InvalidateOutputs([stableInput]);
    Check(!cache.IsCurrent(cachedAction, firstKey), "Generated input invalidation failed");
    string includes = Path.Combine(root, "inventory");
    Directory.CreateDirectory(includes);
    var inventoryAction = new CommandContext(process, "", CommandType.Compile, [artifact], [], true, "inventory", searchDirectories: [includes]);
    var inventoryCache = new BuildCacheSession(cacheSettings);
    _ = inventoryCache.SearchKey(inventoryAction);
    string generatedHeader = Path.Combine(includes, "generated.h");
    File.WriteAllText(generatedHeader, "// new include");
    try { inventoryCache.ValidateInputSnapshot(); throw new Exception("Mid-build include addition was ignored"); }
    catch (IOException) { }
    inventoryCache.InvalidateOutputs([generatedHeader]);
    _ = inventoryCache.SearchKey(inventoryAction);
    inventoryCache.ValidateInputSnapshot();
    BuildCacheSession.WriteState(cachedAction.StatePath, State(firstKey));
    File.WriteAllText(artifact, "changed");
    Check(cache.Explain(cachedAction, firstKey).StartsWith("output missing or changed:"), "Corrupt-output reason incorrect");
    string resourceDir = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceDir);
    string resourceHeader = Path.Combine(root, "resource-version.h"), resourceData = Path.Combine(root, "payload.bin");
    File.WriteAllText(resourceHeader, "#define VALUE 1");
    File.WriteAllText(resourceData, "payload");
    string resourceScript = Path.Combine(resourceDir, "test.rc");
    File.WriteAllText(resourceScript, "#include \"../resource-version.h\"\n1 RCDATA \"../payload.bin\"\n");
    var resourceDependencies = ResourceDependencies.Discover(resourceScript, []);
    Check(resourceDependencies.Complete && resourceDependencies.Files.Contains(resourceHeader) && resourceDependencies.Files.Contains(resourceData), "RC dependencies outside resource directory missing");
    string nestedHeader = Path.Combine(root, "nested.h"), laterHeader = Path.Combine(root, "later.h");
    File.WriteAllText(resourceHeader, "#define HEADER_PATH \"later.h\"");
    File.WriteAllText(laterHeader, "#include \"nested.h\"");
    File.WriteAllText(nestedHeader, "#define NESTED 1");
    File.AppendAllText(resourceScript, "#include \"../later.h\"\n");
    Check(ResourceDependencies.Discover(resourceScript, []).Files.Contains(nestedHeader), "Literal path prevented recursive include scan");
    File.AppendAllText(resourceScript, "2 ICON GENERATED_PATH\n");
    Check(!ResourceDependencies.Discover(resourceScript, []).Complete, "Ambiguous RC macro marked reusable");
    cacheSettings.SharedCacheDirectory = cacheSettings.CacheDirectory;
    cacheSettings.LocalCacheLimitMB = 1;
    cache.TrimLocalCache();
    Check(Directory.GetFiles(cacheSettings.CacheDirectory, "*.blob", SearchOption.AllDirectories).Length == 2, "Quota removed entries from a shared/local combined directory");
    File.WriteAllText(depfile, "corrupt dependency metadata");
    var repairedAction = new CommandContext(process, "", CommandType.Compile, [artifact], [], true, "corrupt old depfile",
        dependencyFile: depfile, dependencyFormat: DependencyFormat.Makefile);
    Check(!repairedAction.CurrentInputs().Any(), "Corrupt old depfile was not ignored for cache recovery");
    string badDep = Path.Combine(root, "bad-deps.d");
    var invalidDependencies = new CommandContext(process, prefix + $"worker \"{badDep}\" 0 ok", CommandType.Compile,
        [badDep], [], true, "invalid fresh depfile", dependencyFile: badDep, dependencyFormat: DependencyFormat.Makefile);
    await invalidDependencies.ExecuteAsync();
    Check(invalidDependencies.Status == CommandStatus.Failed && !File.Exists(invalidDependencies.StatePath), "Invalid fresh dependency metadata was cached");
    // Include search keys cover used headers: a new .cpp or an unrelated header must not rebuild,
    // a header that shadows a used one must; __has_include users keep the full inventory.
    {
        string project = Path.Combine(root, "IncludeProject");
        string source = Path.Combine(project, "Source"), third = Path.Combine(project, "ThirdParty"), objects = Path.Combine(project, "Objects");
        Directory.CreateDirectory(Path.Combine(source, "Core")); Directory.CreateDirectory(third); Directory.CreateDirectory(objects);
        string? savedProject = GlobalEnvironment.ProjectPath;
        GlobalEnvironment.ProjectPath = project;
        File.WriteAllText(Path.Combine(third, "Lib.h"), "lib");
        File.WriteAllText(Path.Combine(source, "Core", "Core.h"), "#include \"Lib.h\"\ncore");
        File.WriteAllText(Path.Combine(source, "Core", "Core.cpp"), "#include \"Core.h\"\n");
        File.WriteAllText(Path.Combine(source, "Core", "Probe.cpp"), "#if __has_include(\"Optional.h\")\n#endif\n#include \"Core.h\"\n");
        CommandContext Compile(string name)
        {
            string unit = Path.Combine(source, "Core", name + ".cpp"), obj = Path.Combine(objects, name + ".o"), dep = obj + ".d";
            return new CommandContext(process, prefix + $"cc \"{obj}\" \"{dep}\" \"{unit}\" \"{source}\" \"{third}\"",
                CommandType.Compile, [obj, dep], [unit], true, name, dependencyFile: dep, dependencyFormat: DependencyFormat.Makefile, searchDirectories: [source, third]);
        }
        async Task<string> Run()
        {
            var actions = new[] { Compile("Core"), Compile("Probe") };
            await new LocalBuildExecutor(2, () => 16384).ExecuteAsync(new BuildActionGraph(actions), CancellationToken.None);
            return string.Join(",", actions.Select(a => a.CacheHit ? "cache" : a.WasSkipped ? "skip" : "run"));
        }
        try
        {
            Check(await Run() == "run,run", "Include test: first build");
            Check(await Run() == "skip,skip", "Include test: no-op");
            File.WriteAllText(Path.Combine(source, "Core", "New.cpp"), "int x;");
            Check(await Run() == "skip,skip", "A new .cpp in a search directory rebuilt compile actions");
            File.WriteAllText(Path.Combine(source, "Unrelated.h"), "u");
            Check(await Run() == "skip,run", "Unrelated header: precise action must skip, __has_include action must rebuild");
            File.WriteAllText(Path.Combine(source, "Lib.h"), "shadow");
            Check(await Run() == "run,run", "A header shadowing a used one did not rebuild");
            Check(File.ReadAllText(Path.Combine(objects, "Core.o")).Contains("shadow"), "Shadowing header not used");
            Check(await Run() == "skip,skip", "Include test: not stable after shadowing");
            File.Delete(Path.Combine(source, "Lib.h"));
            Check(await Run() != "skip,skip" && !File.ReadAllText(Path.Combine(objects, "Core.o")).Contains("shadow"), "Removed shadowing header still used");
            foreach (string file in Directory.GetFiles(objects)) File.Delete(file);
            Check(await Run() == "cache,cache", "Objects were not restored from the cache after clean");
        }
        finally { GlobalEnvironment.ProjectPath = savedProject; }
    }

    // Conan 2 `install --format=json`: host packages and components; build-context tools and the consumer are skipped.
    {
        string package = Path.Combine(root, "conan-package");
        string graphJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            graph = new
            {
                nodes = new Dictionary<string, object>
                {
                    ["0"] = new { @ref = "conanfile", package_folder = (string?)null, cpp_info = new { root = new { includedirs = new[] { "include" } } } },
                    ["1"] = new { @ref = "zlib/1.3", context = "host", package_folder = package, cpp_info = new Dictionary<string, object>
                    {
                        ["root"] = new { includedirs = new[] { Path.Combine(package, "include") }, libdirs = new[] { "lib" }, libs = new[] { "z" }, defines = (string[]?)null, system_libs = new[] { "m" } },
                        ["extra"] = new { includedirs = new[] { "include/extra" }, libs = new[] { "zextra" } }
                    } },
                    ["2"] = new { @ref = "cmake/3.30", context = "build", package_folder = package, cpp_info = new { root = new { libs = new[] { "tool" } } } },
                }
            }
        });
        var conan = PackageManagers.ParseConanGraph(graphJson);
        Check(conan.IncludeDirectories.SequenceEqual([Path.Combine(package, "include"), Path.Combine(package, "include", "extra")]), "Conan include directories: " + string.Join(";", conan.IncludeDirectories));
        Check(conan.LibraryDirectories.SequenceEqual([Path.Combine(package, "lib")]) && conan.Libraries.SequenceEqual(["z", "zextra"]) && conan.SystemLibraries.SequenceEqual(["m"]), "Conan libraries");
        try { PackageManagers.ParseConanGraph("{}"); throw new Exception("Conan 1 output accepted"); } catch (InvalidOperationException) { }
    }

    Console.WriteLine("PASS: graph ordering, parallel readiness, direct process arguments, stdout/stderr draining, no-op, duplicate outputs, cycle, failure (keep going and fail fast), missing input, cancellation, depfile parsing/recovery, memory admission/fairness, local cache quota/index, input/inventory validation, RC closure, child job lifetime, rebuild reasons, precise include search keys, Conan graph parsing.");
    return 0;
}
finally { Log.Cleanup(); }

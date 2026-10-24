namespace Beast.BuildTool;

/// <summary>
/// Cross-process locks of a project, held as open files (released by the OS if a process dies).
/// <list type="bullet">
/// <item>Project lock (exclusive, short): rules compilation/evaluation and IDE project generation.</item>
/// <item>Build lock: shared by every running build, exclusive for a whole-project clean.</item>
/// <item>Target locks (exclusive): one per platform/configuration/target being built or cleaned,
/// plus "AndroidPackage" for the shared Gradle project. Different targets and configurations
/// of one project therefore build in parallel.</item>
/// </list>
/// Waiting has no timeout: a long build of another target is not an error.
/// </summary>
internal static class ProjectBuildLock
{
    public static Task<FileStream> AcquireAsync(string projectDirectory) =>
        AcquireFileAsync(Path.Combine(projectDirectory, "Intermediate", ".beast-build.lock"), exclusive: true, "another build/generation of this project");

    public static Task<FileStream> AcquireBuildAsync(string projectDirectory, bool exclusive) =>
        AcquireFileAsync(Path.Combine(projectDirectory, "Intermediate", "Locks", "Build.lock"), exclusive,
            exclusive ? "running builds of this project" : "a clean of this project");

    public static Task<FileStream> AcquireTargetAsync(string projectDirectory, string name) =>
        AcquireFileAsync(Path.Combine(projectDirectory, "Intermediate", "Locks", Sanitize(name) + ".lock"), exclusive: true, "another build of " + name);

    /// <summary>Acquires several target locks in a fixed order, so that two builds cannot deadlock.</summary>
    public static async Task<IDisposable> AcquireTargetsAsync(string projectDirectory, IEnumerable<string> names)
    {
        var held = new List<FileStream>();
        try
        {
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                held.Add(await AcquireTargetAsync(projectDirectory, name));
            return new Releaser(held);
        }
        catch
        {
            foreach (var stream in held) stream.Dispose();
            throw;
        }
    }

    private sealed class Releaser(List<FileStream> streams) : IDisposable
    {
        public void Dispose() { foreach (var stream in Enumerable.Reverse(streams)) stream.Dispose(); }
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static async Task<FileStream> AcquireFileAsync(string file, bool exclusive, string waitingFor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.Zero;
        while (true)
        {
            try
            {
                return exclusive
                    ? new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    : new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            }
            // Windows: sharing/lock violation. Other hosts emulate FileShare with advisory locks.
            catch (IOException ex) when (OperatingSystem.IsWindows() ? (ex.HResult & 0xffff) is 32 or 33
                : ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (timer.Elapsed >= nextReport)
                {
                    Log.TraceInfo($"Waiting for {waitingFor} to finish...");
                    nextReport = timer.Elapsed + TimeSpan.FromSeconds(30);
                }
                await Task.Delay(200);
            }
        }
    }
}

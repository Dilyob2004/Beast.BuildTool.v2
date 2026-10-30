using System.Diagnostics;

namespace Beast.BuildTool;

/// <summary>
/// The JDK, Android SDK and NDK Beast uses, found without a new terminal after InstallAndroid: a path
/// in Config/BuildConfig.json is used when it exists; otherwise (or when it is empty) the environment
/// and the standard locations are searched, and the first existing one wins. A missing tool stops the
/// build before compiling, with what was searched and how to fix it.
/// </summary>
public static class AndroidToolchain
{
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    private static string Expand(string? value) => string.IsNullOrWhiteSpace(value) ? "" : Environment.ExpandEnvironmentVariables(value.Trim());

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Fix(string setting) =>
        $"Install it with {(HostPlatform.IsWindows ? "InstallAndroid.ps1" : "InstallAndroid.sh")} from the Beast repository, " +
        $"or set {setting} in Config/BuildConfig.json.";

    private static void WarnOnce(string message)
    {
        lock (Warned) if (!Warned.Add(message)) return;
        Log.TraceWarning(message);
    }

    // --- JDK ---------------------------------------------------------------------------------------

    /// <summary>Candidates in search order, with a description for messages.</summary>
    internal static List<(string Path, string Source)> JavaHomeCandidates()
    {
        var list = new List<(string, string)>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) list.Add((javaHome, "JAVA_HOME"));
        // Where InstallAndroid puts Temurin 17.
        if (HostPlatform.IsWindows) list.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beast", "jdk-17"), "InstallAndroid"));
        else if (HostPlatform.IsMacOS) list.Add((Path.Combine(Home, "Library", "Java", "JavaVirtualMachines", "beast-temurin-17", "Contents", "Home"), "InstallAndroid"));
        else list.Add((Path.Combine(Home, ".local", "share", "beast", "jdk-17"), "InstallAndroid"));
        // Android Studio's JetBrains runtime.
        if (HostPlatform.IsWindows)
        {
            list.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr"), "Android Studio"));
            list.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Android Studio", "jbr"), "Android Studio"));
        }
        else if (HostPlatform.IsMacOS)
        {
            list.Add(("/Applications/Android Studio.app/Contents/jbr/Contents/Home", "Android Studio"));
            list.Add((Path.Combine(Home, "Applications", "Android Studio.app", "Contents", "jbr", "Contents", "Home"), "Android Studio"));
        }
        else
        {
            list.Add(("/opt/android-studio/jbr", "Android Studio"));
            list.Add((Path.Combine(Home, "android-studio", "jbr"), "Android Studio"));
            list.Add(("/usr/local/android-studio/jbr", "Android Studio"));
        }
        return list;
    }

    /// <summary>A JDK root: bin/java exists and, when the release file says so, it is Java 17 or newer.</summary>
    internal static bool IsJdk17(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "bin", HostPlatform.Executable("java")))) return false;
        string release = Path.Combine(root, "release");
        if (!File.Exists(release)) return true;
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"(1\\.)?(\\d+)");
        return !match.Success || int.Parse(match.Groups[2].Value) >= 17;
    }

    /// <summary>macOS: /usr/libexec/java_home -v 17+ (installed JDKs, e.g. from a .pkg).</summary>
    private static string? MacJavaHome()
    {
        if (!HostPlatform.IsMacOS || !File.Exists("/usr/libexec/java_home")) return null;
        try
        {
            var info = new ProcessStartInfo("/usr/libexec/java_home") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-v");
            info.ArgumentList.Add("17+");
            using var process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>The JDK to use, or null with the places searched.</summary>
    public static string? FindJavaHome(out List<string> searched)
    {
        searched = [];
        string configured = Expand(BuildConfigManager.Config.Android.JavaHome);
        if (configured.Length > 0)
        {
            if (IsJdk17(configured)) return configured;
            searched.Add($"android.javaHome: {configured} ({(Directory.Exists(configured) ? "not a JDK 17+" : "does not exist")})");
        }
        foreach (var (path, source) in JavaHomeCandidates())
        {
            if (IsJdk17(path))
            {
                if (configured.Length > 0) WarnOnce($"android.javaHome ({configured}) is not usable; using {path} ({source}).");
                return path;
            }
            searched.Add($"{source}: {path}");
        }
        if (MacJavaHome() is { } mac && IsJdk17(mac)) return mac;
        if (HostPlatform.IsMacOS) searched.Add("/usr/libexec/java_home -v 17+");
        return null;
    }

    public static string RequireJavaHome()
    {
        return FindJavaHome(out var searched) ?? throw new InvalidOperationException(
            "Android build: no JDK 17+ found. Searched:" + string.Concat(searched.Select(s => "\n  - " + s)) + "\n" + Fix("android.javaHome"));
    }

    // --- SDK ---------------------------------------------------------------------------------------

    internal static List<(string Path, string Source)> SdkCandidates()
    {
        var list = new List<(string, string)>();
        foreach (string variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) list.Add((value, variable));
        string standard = HostPlatform.IsWindows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
            : HostPlatform.IsMacOS ? Path.Combine(Home, "Library", "Android", "sdk") : Path.Combine(Home, "Android", "Sdk");
        list.Add((standard, "Android Studio / InstallAndroid default"));
        return list;
    }

    /// <summary>The Android SDK to use (an existing directory), or null with the places searched.</summary>
    public static string? FindSdkRoot(out List<string> searched)
    {
        searched = [];
        string configured = Expand(BuildConfigManager.Config.Android.SdkRoot);
        if (configured.Length > 0)
        {
            if (Directory.Exists(configured)) return configured;
            searched.Add($"android.sdkRoot: {configured} (does not exist)");
        }
        foreach (var (path, source) in SdkCandidates())
        {
            if (Directory.Exists(path))
            {
                if (configured.Length > 0) WarnOnce($"android.sdkRoot ({configured}) does not exist; using {path} ({source}).");
                return path;
            }
            searched.Add($"{source}: {path}");
        }
        return null;
    }

    public static string RequireSdkRoot() => FindSdkRoot(out var searched) ?? throw new InvalidOperationException(
        "Android build: Android SDK not found. Searched:" + string.Concat(searched.Select(s => "\n  - " + s)) + "\n" + Fix("android.sdkRoot"));

    /// <summary>SDK root for messages and optional tools (adb): the found one, else the standard location.</summary>
    public static string SdkRootOrDefault() => FindSdkRoot(out _) ?? SdkCandidates()[^1].Path;

    // --- NDK ---------------------------------------------------------------------------------------

    /// <summary>
    /// An NDK directory (with source.properties): android.ndkRoot when it exists (an NDK or a directory
    /// of versions), else ANDROID_NDK_HOME/ANDROID_NDK_ROOT, else &lt;sdk&gt;/ndk; android.ndkVersion
    /// selects a version, otherwise the newest is used.
    /// </summary>
    public static string RequireNdk()
    {
        var android = BuildConfigManager.Config.Android;
        var searched = new List<string>();
        var candidates = new List<(string Path, string Source)>();
        string configured = Expand(android.NdkRoot);
        if (configured.Length > 0) candidates.Add((configured, "android.ndkRoot"));
        foreach (string variable in new[] { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) candidates.Add((value, variable));
        if (FindSdkRoot(out _) is { } sdk) candidates.Add((Path.Combine(sdk, "ndk"), "<sdk>/ndk"));
        else candidates.Add((Path.Combine(SdkCandidates()[^1].Path, "ndk"), "<sdk>/ndk"));
        foreach (var (path, source) in candidates)
        {
            if (Directory.Exists(path))
            {
                if (File.Exists(Path.Combine(path, "source.properties")) &&
                    (string.IsNullOrWhiteSpace(android.NdkVersion) || Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals(android.NdkVersion, StringComparison.OrdinalIgnoreCase) || source == "android.ndkRoot"))
                    return Use(path, source);
                var versions = Directory.GetDirectories(path).Where(d => File.Exists(Path.Combine(d, "source.properties")))
                    .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)).ToList();
                if (!string.IsNullOrWhiteSpace(android.NdkVersion))
                    versions = versions.Where(d => Path.GetFileName(d).Equals(android.NdkVersion, StringComparison.OrdinalIgnoreCase)).ToList();
                if (versions.Count > 0) return Use(versions[0], source);
                searched.Add($"{source}: {path} (no NDK{(string.IsNullOrWhiteSpace(android.NdkVersion) ? "" : " " + android.NdkVersion)} inside)");
            }
            else searched.Add($"{source}: {path} (does not exist)");
        }
        throw new InvalidOperationException("Android build: Android NDK not found. Searched:" + string.Concat(searched.Select(s => "\n  - " + s)) + "\n" +
            Fix("android.ndkRoot (and android.ndkVersion)"));

        string Use(string ndk, string source)
        {
            if (configured.Length > 0 && source != "android.ndkRoot") WarnOnce($"android.ndkRoot ({configured}) has no usable NDK; using {ndk} ({source}).");
            return ndk;
        }
    }

    /// <summary>Before compiling an Android application: the JDK and SDK that Gradle and apksigner need.</summary>
    public static (string JavaHome, string SdkRoot) RequireForPackaging()
    {
        var problems = new List<string>();
        string? java = null, sdk = null;
        try { java = RequireJavaHome(); } catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        try { sdk = RequireSdkRoot(); } catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        return (java!, sdk!);
    }
}

namespace Beast.BuildTool;

/// <summary>
/// Where an Android Application target lives. The target named like the project uses
/// Platforms/Android (created by "new"); any other application target gets its own Gradle
/// project, Platforms/Android-&lt;Target&gt;, created from the template on its first build.
/// </summary>
public static class AndroidApplication
{
    public static bool IsMain(string projectPath, string target) =>
        target.Equals(new DirectoryInfo(projectPath).Name, StringComparison.OrdinalIgnoreCase);

    public static string GradleProject(string projectPath, string target) =>
        Path.Combine(projectPath, "Platforms", IsMain(projectPath, target) ? "Android" : "Android-" + target);

    /// <summary>Java package of the generated activity (com.beast.&lt;target&gt;).</summary>
    public static string Namespace(string target) => "com.beast." + target.ToLowerInvariant();

    /// <summary>android.applicationId applies to the main application; other targets keep their namespace.</summary>
    public static string ApplicationId(string projectPath, string target)
    {
        string configured = BuildConfigManager.Config.Android.ApplicationId;
        return IsMain(projectPath, target) && !string.IsNullOrWhiteSpace(configured) ? configured : Namespace(target);
    }

    public static string Apk(string projectPath, string target, TargetConfiguration configuration) =>
        Path.Combine(projectPath, "Binaries", "Android", configuration.ToString(), target + ".apk");

    /// <summary>Packaging state (init script, Gradle fingerprint, wrap.sh) of one application.</summary>
    public static string StateDirectory(string projectPath, string target, TargetConfiguration configuration) =>
        Path.Combine(projectPath, "Intermediate", "Build", "Android", configuration.ToString(), target, "Package");

    public static void Ensure(string projectPath, string target)
    {
        string gradle = GradleProject(projectPath, target);
        if (Directory.Exists(gradle)) return;
        Log.TraceInfo($"Creating Android Gradle project for {target}: {gradle}");
        AndroidTemplateGenerator.Create(projectPath, target, gradle);
    }
}

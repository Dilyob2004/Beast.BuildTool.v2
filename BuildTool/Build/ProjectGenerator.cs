using System.Text;

namespace Beast.BuildTool;

public static class ProjectGenerator
{
    public static bool CreateProject(string projectName, string projectLocation)
    {
        if (!IsValidName(projectName))
        {
            Log.TraceError("Project name must start with a letter or underscore and contain only letters, digits, or underscores.");
            return false;
        }

        string projectDir = Path.GetFullPath(Path.Combine(projectLocation, projectName));
        if (Directory.Exists(projectDir))
        {
            Log.TraceError($"Project directory already exists: {projectDir}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(projectDir, "Source", "Launch"));
            Directory.CreateDirectory(Path.Combine(projectDir, "Intermediate", "Build"));
            Directory.CreateDirectory(Path.Combine(projectDir, "Binaries"));
            Directory.CreateDirectory(Path.Combine(projectDir, "Config"));

            File.WriteAllText(Path.Combine(projectDir, "Source", "Launch", "Launch.Build.cs"), HeaderGenerator.GenerateLaunchBuildCs(projectName), Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectDir, "Source", "Launch", "Launch.cpp"), HeaderGenerator.GenerateLaunchSource(projectName), Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectDir, "Source", projectName + ".Target.cs"), HeaderGenerator.GenerateTargetRules(projectName), Encoding.UTF8);
            BuildConfigManager.CreateDefault(projectDir);
            AndroidTemplateGenerator.Create(projectDir, projectName);
            WindowsTemplateGenerator.Ensure(projectDir, projectName);
            SolutionGenerator.TryGenerate(projectDir, projectName);

            Log.TraceInfo($"Created project '{projectName}' at '{projectDir}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.TraceError($"Failed to create project: {ex.Message}");
            return false;
        }
    }

    public static bool CreateModule(string projectPath, string moduleName)
    {
        if (!IsValidName(moduleName))
        {
            Log.TraceError("Module name must start with a letter or underscore and contain only letters, digits, or underscores.");
            return false;
        }

        string moduleDir = Path.Combine(projectPath, "Source", moduleName);
        if (Directory.Exists(moduleDir))
        {
            Log.TraceError($"Module directory already exists: {moduleDir}");
            return false;
        }

        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, $"{moduleName}.Build.cs"), HeaderGenerator.GenerateModuleBuildCs(moduleName), Encoding.UTF8);
        File.WriteAllText(Path.Combine(moduleDir, $"{moduleName}.cpp"), HeaderGenerator.GenerateModuleSource(moduleName), Encoding.UTF8);
        Log.TraceInfo($"Created module '{moduleName}'.");
        Log.TraceInfo("For explicit targets, add the module to TargetRules.Modules or to a module's PublicDependencies/PrivateDependencies.");
        return true;
    }

    private static bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

internal static class WindowsTemplateGenerator
{
    public static void Ensure(string projectDir, string projectName)
    {
        string template = Path.Combine(GlobalEnvironment.TemplatesPath, "Windows", "Resources");
        string destination = Path.Combine(projectDir, "Platforms", "Windows", "Resources");
        if (!Directory.Exists(template))
            throw new DirectoryNotFoundException($"Windows resource template directory was not found: {template}");

        Directory.CreateDirectory(destination);
        CopyIfMissing(Path.Combine(template, "Default.ico"), Path.Combine(destination, "Default.ico"));
        CopyIfMissing(Path.Combine(template, "Default-Win64.manifest"), Path.Combine(destination, "Default-Win64.manifest"));
        CopyIfMissing(Path.Combine(template, "Default-Win64-dpiaware.manifest"), Path.Combine(destination, "Default-Win64-dpiaware.manifest"));

        string script = Path.Combine(destination, projectName + ".rc");
        if (!File.Exists(script))
        {
            string text = File.ReadAllText(Path.Combine(template, "Default.rc2"))
                .Replace("{{PROJECT_NAME}}", projectName)
                .Replace("{{COMPANY_NAME}}", projectName);
            File.WriteAllText(script, text, new UTF8Encoding(false));
        }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Windows resource template was not found.", source);
        if (!File.Exists(destination))
            File.Copy(source, destination);
    }
}

internal static class AndroidTemplateGenerator
{
    /// <param name="projectName">Application name: native library, activity package and label.</param>
    /// <param name="destination">Gradle project directory; default Platforms/Android.</param>
    public static void Create(string projectDir, string projectName, string? destination = null)
    {
        string template = Path.Combine(GlobalEnvironment.TemplatesPath, "Android", "Project");
        destination ??= Path.Combine(projectDir, "Platforms", "Android");
        if (!Directory.Exists(template))
            throw new DirectoryNotFoundException($"Android template directory was not found: {template}");

        foreach (string source in Directory.GetFiles(template, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(template, source);
            if (relative.StartsWith(".gradle" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.Split(Path.DirectorySeparatorChar).Contains("jniLibs") ||
                Path.GetFileName(relative).Equals("local.properties", StringComparison.OrdinalIgnoreCase))
                continue;

            string target = Path.Combine(destination, relative)
                .Replace("com" + Path.DirectorySeparatorChar + "example" + Path.DirectorySeparatorChar + "gameactivity", "com" + Path.DirectorySeparatorChar + "beast" + Path.DirectorySeparatorChar + projectName.ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (IsText(source))
                // Gradle's Groovy parser rejects a UTF-8 BOM in settings.gradle.
                File.WriteAllText(target, ReplaceTokens(File.ReadAllText(source), projectName), new UTF8Encoding(false));
            else
                File.Copy(source, target, true);
        }
    }

    // Replace original tokens once; a name such as BeastDemo must not be
    // processed again and become BeastDemoDemo in android.app.lib_name.
    private static string ReplaceTokens(string text, string projectName) =>
        System.Text.RegularExpressions.Regex.Replace(text,
            @"com\.example\.gameactivity|com\.example\.GameActivity|My Application|Beast",
            match => match.Value switch
            {
                "com.example.gameactivity" => $"com.beast.{projectName.ToLowerInvariant()}",
                "com.example.GameActivity" => $"com.beast.{projectName.ToLowerInvariant()}.GameActivity",
                _ => projectName
            });

    private static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".gradle" or ".properties" or ".xml" or ".java" or "";
}

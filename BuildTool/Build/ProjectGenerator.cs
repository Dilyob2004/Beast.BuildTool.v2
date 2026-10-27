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
            Directory.CreateDirectory(Path.Combine(projectDir, "Source", "Launch", "Mac"));
            Directory.CreateDirectory(Path.Combine(projectDir, "Source", "Launch", "IOS"));
            File.WriteAllText(Path.Combine(projectDir, "Source", "Launch", "Mac", "LaunchMac.mm"), HeaderGenerator.GenerateLaunchSource(projectName, "LaunchMac.mm"), Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectDir, "Source", "Launch", "IOS", "LaunchIOS.mm"), HeaderGenerator.GenerateLaunchSource(projectName, "LaunchIOS.mm"), Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectDir, "Source", projectName + ".Target.cs"), HeaderGenerator.GenerateTargetRules(projectName), Encoding.UTF8);
            BuildConfigManager.CreateDefault(projectDir);
            AndroidTemplateGenerator.Create(projectDir, projectName);
            WindowsTemplateGenerator.Ensure(projectDir, projectName);
            IdeGenerator.TryGenerate(projectDir);

            Log.TraceInfo($"Created project '{projectName}' at '{projectDir}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.TraceError($"Failed to create project: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 'new --template &lt;directory&gt;': copies the template directory into the new project, replacing
    /// placeholders in file and directory names and in text files: {{ProjectName}}, {{ProjectIdentifier}}
    /// (a C# / C++ identifier), {{PROJECT_NAME}} (the same, as in the built-in templates), {{TemplateDir}}
    /// (absolute) and {{TemplateDirRelative}} (from the new project, '/' separators, e.g. for
    /// externalProjects paths). Intermediate, Binaries, .git, .vs, .gradle and .idea are not copied.
    /// What the template does not provide is completed like a built-in project: Config/BuildConfig.json,
    /// Windows resources and the Android Gradle project (template files win).
    /// </summary>
    public static bool CreateFromTemplate(string projectName, string projectLocation, string templateDirectory)
    {
        if (!IsValidName(projectName))
        {
            Log.TraceError("Project name must start with a letter or underscore and contain only letters, digits, or underscores.");
            return false;
        }
        string template = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExternalProjects.Expand(templateDirectory)));
        if (!Directory.Exists(template))
        {
            Log.TraceError($"Template directory not found: {template}");
            return false;
        }
        string projectDir = Path.GetFullPath(Path.Combine(projectLocation, projectName));
        if (Directory.Exists(projectDir))
        {
            Log.TraceError($"Project directory already exists: {projectDir}");
            return false;
        }
        if (projectDir.StartsWith(template + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Log.TraceError("The new project cannot be created inside its template directory.");
            return false;
        }
        try
        {
            var tokens = new Dictionary<string, string>
            {
                ["{{ProjectName}}"] = projectName,
                ["{{ProjectIdentifier}}"] = HeaderGenerator.SanitizeIdentifier(projectName),
                ["{{PROJECT_NAME}}"] = HeaderGenerator.SanitizeIdentifier(projectName),
                ["{{TemplateDir}}"] = template,
                ["{{TemplateDirRelative}}"] = Path.GetRelativePath(projectDir, template).Replace('\\', '/'),
            };
            string Replace(string text) => tokens.Aggregate(text, (current, token) => current.Replace(token.Key, token.Value));
            var skipped = new[] { "Intermediate", "Binaries", ".git", ".vs", ".gradle", ".idea" };
            int copied = 0;
            foreach (string source in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(template, source);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => skipped.Contains(part, StringComparer.OrdinalIgnoreCase))) continue;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Template contains a symlink/junction: {source}");
                string target = Path.Combine(projectDir, Replace(relative));
                BuildStepPlan.ValidateOutput(projectDir, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] bytes = File.ReadAllBytes(source);
                // Text with placeholders is rewritten; everything else (images, UTF-16 .rc, binaries) is copied as is.
                bool text = Array.IndexOf(bytes, (byte)0) < 0 && Encoding.UTF8.GetString(bytes).Contains("{{");
                if (text) File.WriteAllText(target, Replace(File.ReadAllText(source)), new UTF8Encoding(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
                else File.WriteAllBytes(target, bytes);
                copied++;
            }
            if (!Directory.Exists(Path.Combine(projectDir, "Source")))
                throw new InvalidOperationException($"Template {template} has no Source directory.");
            Directory.CreateDirectory(Path.Combine(projectDir, "Intermediate", "Build"));
            Directory.CreateDirectory(Path.Combine(projectDir, "Binaries"));
            if (!File.Exists(Path.Combine(projectDir, "Config", "BuildConfig.json"))) BuildConfigManager.CreateDefault(projectDir);
            WindowsTemplateGenerator.Ensure(projectDir, projectName);
            AndroidTemplateGenerator.Create(projectDir, projectName, overwrite: false);
            BuildConfigManager.LoadConfig(projectDir);
            IdeGenerator.TryGenerate(projectDir);
            Log.TraceInfo($"Created project '{projectName}' at '{projectDir}' from template {template} ({copied} files).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.TraceError($"Failed to create project from template: {ex.Message}");
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
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
    /// <param name="overwrite">false: keep files that already exist (a custom template's Android files).</param>
    public static void Create(string projectDir, string projectName, string? destination = null, bool overwrite = true)
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
            if (!overwrite && File.Exists(target)) continue;

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

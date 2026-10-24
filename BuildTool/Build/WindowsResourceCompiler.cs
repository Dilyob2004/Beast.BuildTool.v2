namespace Beast.BuildTool;

/// <summary>Compiles a project's Windows icon, version data, and application manifest into a .res file.</summary>
internal static class WindowsResourceCompiler
{
    public static void AddCompileCommand(
        CommandExecutor executor,
        WindowsPlatform platform,
        string projectPath,
        string projectName,
        TargetConfiguration configuration,
        LinkEnvironment linkEnvironment,
        string? outputDirectory = null)
    {
        WindowsTemplateGenerator.Ensure(projectPath, projectName);

        string resourceDirectory = Path.Combine(projectPath, "Platforms", "Windows", "Resources");
        string resourceScript = Path.Combine(resourceDirectory, projectName + ".rc");
        outputDirectory ??= Path.Combine(projectPath, "Intermediate", "Build", platform.PlatformName, configuration.ToString(), "Resources");
        string resourceFile = Path.Combine(outputDirectory, projectName + ".res");
        Directory.CreateDirectory(outputDirectory);

        if (string.IsNullOrWhiteSpace(platform.ResourceCompilerPath) || !File.Exists(platform.ResourceCompilerPath))
            throw new FileNotFoundException("Windows SDK resource compiler rc.exe was not found.", platform.ResourceCompilerPath);

        var dependencies = ResourceDependencies.Discover(resourceScript, platform.SystemIncludePaths.Concat(
            (Environment.GetEnvironmentVariable("INCLUDE") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)));
        if (!dependencies.Complete) Log.TraceVerbose("RC dependency scan is conservative: this resource action will not be reused.");
        string dpiAware = BuildConfigManager.Config.Windows.DpiAware ? "1" : "0";
        string debug = configuration == TargetConfiguration.Debug ? " /d_DEBUG=1" : string.Empty;
        string arguments = $"/nologo /dBEAST_DPI_AWARE={dpiAware}{debug} /i\"{resourceDirectory}\" " +
            string.Join(" ", platform.SystemIncludePaths.Select(p => $"/i\"{p}\"")) + $" /fo\"{resourceFile}\" \"{resourceScript}\"";
        executor.AddCommand(new CommandContext(
            platform.ResourceCompilerPath,
            arguments,
            CommandType.Compile,
            [resourceFile],
            dependencies.Files,
            true,
            $"Compiling Windows resources for {projectName}...",
            searchDirectories: dependencies.SearchDirectories, forceExecute: !dependencies.Complete));

        linkEnvironment.InputFiles.Add(resourceFile);
    }
}

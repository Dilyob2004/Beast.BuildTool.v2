using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beast.BuildTool;

/// <summary>
/// 'generate' and project/module creation: Visual Studio (.sln) on Windows, Xcode (.xcodeproj) on
/// macOS, and VS Code settings (.vscode) on every host. Every IDE delegates builds to this tool.
/// </summary>
public static class IdeGenerator
{
    /// <summary>Generates the host's IDE files; returns the paths written.</summary>
    public static List<string> Generate(string projectDir)
    {
        projectDir = Path.GetFullPath(projectDir);
        string name = new DirectoryInfo(projectDir).Name;
        var written = new List<string>();
        if (HostPlatform.IsWindows)
        {
            SolutionGenerator.Generate(projectDir, name);
            written.Add(Path.Combine(projectDir, name + ".sln"));
        }
        BuildConfigManager.LoadConfig(projectDir);
        Platform? host = null;
        try { host = PlatformFactory.Create(PlatformFactory.HostDefault, null)[0]; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log.TraceInfo($"No host toolchain ({ex.Message}); IDE files list sources only.");
        }
        if (HostPlatform.IsMacOS && host != null)
        {
            IosPlatform? simulator = null;
            try { simulator = new IosPlatform(simulator: true, HostPlatform.Architecture); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                Log.TraceVerbose("iOS Simulator targets skipped: " + ex.Message);
            }
            written.Add(XcodeGenerator.Generate(projectDir, name, host, simulator != null));
        }
        written.AddRange(VsCodeGenerator.Generate(projectDir, host));
        return written;
    }

    /// <summary>After 'new'/'module': regenerates IDE files without failing the command.</summary>
    public static void TryGenerate(string projectDir)
    {
        try
        {
            foreach (string file in Generate(projectDir)) Log.TraceVerbose("Generated " + file);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.TraceWarning("IDE files were not generated: " + ex.Message + " Run 'generate' after fixing it.");
        }
    }

    /// <summary>The command that starts this tool: the apphost, or dotnet + the dll.</summary>
    internal static string[] ToolCommand()
    {
        string exe = Environment.ProcessPath ?? "Beast.BuildTool";
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [exe, typeof(IdeGenerator).Assembly.Location]
            : [exe];
    }

    /// <summary>Path of the program 'run' starts for a host-platform application target.</summary>
    internal static string ProgramPath(string binaries, Platform platform, BuildTarget target) =>
        platform is MacPlatform && !target.Rules.ConsoleApplication && !target.Rules.IsTest
            ? MacPlatform.BundleExecutable(binaries, target.Name)
            : Path.Combine(binaries, target.Name + platform.ExecutableFileExtension);

    internal static IEnumerable<string> SourceFiles(string projectDir)
    {
        string source = Path.Combine(projectDir, "Source");
        if (!Directory.Exists(source)) return [];
        return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".ixx" or ".cppm" or ".m" or ".mm"
                or ".h" or ".hh" or ".hpp" or ".hxx" or ".inl" or ".cs" or ".s" or ".asm" or ".plist" or ".json" or ".xml" or ".java" or ".kt")
            .OrderBy(f => f, StringComparer.Ordinal);
    }
}

/// <summary>
/// Xcode project (&lt;Project&gt;.xcodeproj) of external build targets: Product > Build runs
/// 'beast build' for the target and configuration, Product > Run starts the program under the
/// debugger (macOS) or launches the app in the iOS Simulator. Source files are listed for browsing
/// and editing; code completion comes from compile_commands.json (clangd) as in other editors.
/// </summary>
public static class XcodeGenerator
{
    private sealed class SchemeInfo(string name, string id)
    {
        public string Name { get; } = name;
        public string Id { get; } = id;
        public string Launch { get; set; } = "";
    }

    private static readonly TargetConfiguration[] Configurations = [TargetConfiguration.Debug, TargetConfiguration.Development, TargetConfiguration.Release];

    /// <summary>Writes the project; 'mac' supplies the target list. Returns the .xcodeproj path.</summary>
    public static string Generate(string projectDir, string projectName, Platform mac, bool iosSimulator)
    {
        projectDir = Path.GetFullPath(projectDir);
        var model = ProjectModel.Load(projectDir, mac, TargetConfiguration.Debug);
        string bundle = Path.Combine(projectDir, projectName + ".xcodeproj");
        Directory.CreateDirectory(bundle);
        string script = Path.Combine(bundle, "beast-xcode.sh");
        File.WriteAllText(script, Script(projectDir), new UTF8Encoding(false));
        HostPlatform.EnsureExecutable(script);

        var objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string Id(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectName + "/" + key)))[..24];
        string ConfigList(string owner, Func<TargetConfiguration, string> settings)
        {
            var ids = new List<string>();
            foreach (var c in Configurations)
            {
                string id = Id(owner + "/config/" + c);
                objects[id] = $"{{isa = XCBuildConfiguration; buildSettings = {{{settings(c)}}}; name = {c}; }}";
                ids.Add(id);
            }
            string list = Id(owner + "/configlist");
            objects[list] = $"{{isa = XCConfigurationList; buildConfigurations = ({string.Join(", ", ids)}, ); defaultConfigurationIsVisible = 0; defaultConfigurationName = Debug; }}";
            return list;
        }

        // File tree: groups mirror Source/ directories.
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [""] = [] };
        void EnsureGroup(string relative)
        {
            if (groups.ContainsKey(relative)) return;
            string parent = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
            EnsureGroup(parent);
            groups[relative] = [];
            groups[parent].Add(Id("group/" + relative));
        }
        foreach (string file in IdeGenerator.SourceFiles(projectDir).Append(Path.Combine(projectDir, "Config", "BuildConfig.json")).Where(File.Exists))
        {
            string relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
            string directory = Path.GetDirectoryName(relative)!.Replace('\\', '/');
            EnsureGroup(directory);
            string id = Id("file/" + relative);
            objects[id] = $"{{isa = PBXFileReference; lastKnownFileType = {FileType(relative)}; path = {Quote(Path.GetFileName(relative))}; sourceTree = \"<group>\"; }}";
            groups[directory].Add(id);
        }
        foreach (var (relative, children) in groups)
        {
            string name = relative.Length == 0 ? "" : $"path = {Quote(Path.GetFileName(relative))}; ";
            objects[relative.Length == 0 ? Id("group/") : Id("group/" + relative)] =
                $"{{isa = PBXGroup; children = ({string.Concat(children.Select(c => c + ", "))}); {name}sourceTree = \"<group>\"; }}";
        }

        // One external build target per Beast target, and an iOS Simulator target per application.
        var targetIds = new List<string>();
        var schemes = new List<SchemeInfo>();
        foreach (var target in model.UsedTargets)
        {
            AddTarget(target.Name, "build", "Mac");
            if (target.Rules.Type != TargetType.Application) continue;
            string binaries = Path.Combine(projectDir, "Binaries", mac.PlatformName, "Debug");
            schemes[^1].Launch = PathRunnable(IdeGenerator.ProgramPath(binaries, mac, target), debug: true, arguments: []);
            if (!iosSimulator || target.Rules.IsTest) continue;
            AddTarget(target.Name + " iOS Simulator", "build", "IOSSimulator", target.Name);
            schemes[^1].Launch = PathRunnable("/bin/sh", debug: false, arguments: [script, target.Name, "Debug", "IOSSimulator", "run"]);
        }

        void AddTarget(string xcodeName, string action, string platform, string? beastName = null)
        {
            string id = Id("target/" + xcodeName);
            string list = ConfigList("target/" + xcodeName, _ => $"PRODUCT_NAME = {Quote(xcodeName)}; ");
            string arguments = $"\\\"$(PROJECT_FILE_PATH)/beast-xcode.sh\\\" {beastName ?? xcodeName} $(CONFIGURATION) {platform} $(ACTION)";
            objects[id] = $"{{isa = PBXLegacyTarget; buildArgumentsString = \"{arguments}\"; buildConfigurationList = {list}; buildPhases = (); " +
                $"buildToolPath = /bin/sh; buildWorkingDirectory = {Quote(projectDir)}; dependencies = (); name = {Quote(xcodeName)}; " +
                $"passBuildSettingsInEnvironment = 0; productName = {Quote(xcodeName)}; }}";
            targetIds.Add(id);
            schemes.Add(new SchemeInfo(xcodeName, id));
        }

        string projectId = Id("project");
        string projectList = ConfigList("project", c => c == TargetConfiguration.Debug ? "ONLY_ACTIVE_ARCH = YES; " : "");
        objects[projectId] = $"{{isa = PBXProject; attributes = {{BuildIndependentTargetsInParallel = 1; LastUpgradeCheck = 1600; }}; " +
            $"buildConfigurationList = {projectList}; compatibilityVersion = \"Xcode 14.0\"; developmentRegion = en; hasScannedForEncodings = 0; " +
            $"knownRegions = (en, Base, ); mainGroup = {Id("group/")}; projectDirPath = \"\"; projectRoot = \"\"; " +
            $"targets = ({string.Concat(targetIds.Select(t => t + ", "))}); }}";

        var pbx = new StringBuilder();
        pbx.AppendLine("// !$*UTF8*$!");
        pbx.AppendLine("// Generated by Beast BuildTool ('generate'); regenerated when modules are added.");
        pbx.AppendLine("{");
        pbx.AppendLine("\tarchiveVersion = 1;\n\tclasses = {\n\t};\n\tobjectVersion = 56;\n\tobjects = {");
        foreach (var (id, value) in objects) pbx.AppendLine($"\t\t{id} = {value};");
        pbx.AppendLine("\t};");
        pbx.AppendLine($"\trootObject = {projectId};");
        pbx.AppendLine("}");
        File.WriteAllText(Path.Combine(bundle, "project.pbxproj"), pbx.ToString(), new UTF8Encoding(false));

        string schemeDir = Path.Combine(bundle, "xcshareddata", "xcschemes");
        if (Directory.Exists(schemeDir)) foreach (string old in Directory.GetFiles(schemeDir, "*.xcscheme")) File.Delete(old);
        Directory.CreateDirectory(schemeDir);
        foreach (var scheme in schemes)
            File.WriteAllText(Path.Combine(schemeDir, scheme.Name + ".xcscheme"), Scheme(projectName, scheme.Name, scheme.Id, scheme.Launch), new UTF8Encoding(false));
        return bundle;
    }

    /// <summary>Xcode passes the target, configuration, platform and $(ACTION): "build", "clean" or nothing
    /// (an empty argument is dropped, so it comes last).</summary>
    private static string Script(string projectDir)
    {
        string tool = string.Join(" ", IdeGenerator.ToolCommand().Select(ShellQuote));
        return $$"""
            #!/bin/sh
            # Generated by Beast BuildTool: Xcode targets call this script (build, clean, run).
            target="$1"; config="$2"; platform="$3"; action="${4:-build}"
            project={{ShellQuote(projectDir)}}
            case "$action" in
              clean) exec {{tool}} clean "$project" --target "$target" ;;
              run)   exec {{tool}} run "$project" --target "$target" --config "$config" --platform "$platform" --no-build ;;
              *)     exec {{tool}} build "$project" --target "$target" --config "$config" --platform "$platform" ;;
            esac

            """.Replace("\r\n", "\n");
    }

    private static string PathRunnable(string path, bool debug, IReadOnlyList<string> arguments)
    {
        string args = arguments.Count == 0 ? "" :
            "\n      <CommandLineArguments>\n" + string.Concat(arguments.Select(a => $"         <CommandLineArgument argument = \"{Xml(ShellQuote(a))}\" isEnabled = \"YES\">\n         </CommandLineArgument>\n")) + "      </CommandLineArguments>";
        string debugger = debug ? "Xcode.DebuggerFoundation.Debugger.LLDB" : "";
        string launcher = debug ? "Xcode.DebuggerFoundation.Launcher.LLDB" : "Xcode.IDEFoundation.Launcher.PosixSpawn";
        return $"""
                  selectedDebuggerIdentifier = "{debugger}"
                  selectedLauncherIdentifier = "{launcher}"
                  launchStyle = "0"
                  useCustomWorkingDirectory = "YES"
                  customWorkingDirectory = "{Xml(Path.GetDirectoryName(path)!)}"
                  ignoresPersistentStateOnLaunch = "NO"
                  debugDocumentVersioning = "YES"
                  debugServiceExtension = "internal"
                  allowLocationSimulation = "YES">
                  <PathRunnable
                     runnableDebuggingMode = "0"
                     FilePath = "{Xml(path)}">
                  </PathRunnable>{args}
            """;
    }

    private static string Scheme(string projectName, string name, string id, string launch)
    {
        string reference = $"""
                     <BuildableReference
                        BuildableIdentifier = "primary"
                        BlueprintIdentifier = "{id}"
                        BuildableName = "{Xml(name)}"
                        BlueprintName = "{Xml(name)}"
                        ReferencedContainer = "container:{Xml(projectName)}.xcodeproj">
                     </BuildableReference>
            """;
        string launchBody = launch.Length > 0 ? launch : """
                  selectedDebuggerIdentifier = ""
                  selectedLauncherIdentifier = "Xcode.IDEFoundation.Launcher.PosixSpawn"
                  launchStyle = "0"
                  useCustomWorkingDirectory = "NO"
                  ignoresPersistentStateOnLaunch = "NO"
                  debugDocumentVersioning = "YES"
                  debugServiceExtension = "internal"
                  allowLocationSimulation = "YES">
            """;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- Generated by Beast BuildTool -->
            <Scheme LastUpgradeVersion = "1600" version = "1.7">
               <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
                  <BuildActionEntries>
                     <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            {reference}
                     </BuildActionEntry>
                  </BuildActionEntries>
               </BuildAction>
               <TestAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv = "YES">
               </TestAction>
               <LaunchAction
                  buildConfiguration = "Debug"
            {launchBody}
               </LaunchAction>
               <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
               </ProfileAction>
               <AnalyzeAction buildConfiguration = "Debug">
               </AnalyzeAction>
               <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES">
               </ArchiveAction>
            </Scheme>

            """.Replace("\r\n", "\n");
    }

    private static string FileType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".c" => "sourcecode.c.c",
        ".m" => "sourcecode.c.objc",
        ".mm" => "sourcecode.cpp.objcpp",
        ".cc" or ".cpp" or ".cxx" or ".ixx" or ".cppm" => "sourcecode.cpp.cpp",
        ".h" => "sourcecode.c.h",
        ".hh" or ".hpp" or ".hxx" or ".inl" => "sourcecode.cpp.h",
        ".s" => "sourcecode.asm",
        ".json" => "text.json",
        ".plist" => "text.plist.xml",
        ".xml" => "text.xml",
        ".java" => "sourcecode.java",
        _ => "text",
    };

    /// <summary>Old-style plist string: bare when safe, otherwise quoted.</summary>
    internal static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '/' or ':' or '.' or '-')
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? value;
    private static string ShellQuote(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or ':' or '=')
        ? value : "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>
/// VS Code settings in .vscode (only files that do not exist yet, so edits are kept): build/run/test
/// tasks through this tool, debug configurations for the host's applications, and
/// compile_commands.json for IntelliSense (Microsoft C/C++ or clangd).
/// </summary>
public static class VsCodeGenerator
{
    public static List<string> Generate(string projectDir, Platform? host)
    {
        projectDir = Path.GetFullPath(projectDir);
        string directory = Path.Combine(projectDir, ".vscode");
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        void Write(string name, JsonNode content)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) { Log.TraceVerbose($"{path} exists; kept."); return; }
            File.WriteAllText(path, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            written.Add(path);
        }

        string[] tool = IdeGenerator.ToolCommand();
        string matcher = HostPlatform.IsWindows ? "$msCompile" : "$gcc";
        JsonObject MakeTask(string label, string arguments, bool defaultBuild = false)
        {
            var args = new JsonArray(tool.Skip(1).Select(a => (JsonNode)a).ToArray());
            foreach (string a in arguments.Split(' ')) args.Add(JsonValue.Create(a)); // not Add<T>: reflection serialization is off
            var task = new JsonObject
            {
                ["label"] = label, ["type"] = "process", ["command"] = tool[0], ["args"] = args,
                ["problemMatcher"] = matcher,
            };
            if (defaultBuild) task["group"] = new JsonObject { ["kind"] = "build", ["isDefault"] = true };
            return task;
        }
        Write("tasks.json", new JsonObject
        {
            ["version"] = "2.0.0",
            ["tasks"] = new JsonArray(
                MakeTask("beast: build Debug", "build ${workspaceFolder} --config Debug", defaultBuild: true),
                MakeTask("beast: build Release", "build ${workspaceFolder} --config Release"),
                MakeTask("beast: run", "run ${workspaceFolder} --config Debug"),
                MakeTask("beast: test", "test ${workspaceFolder} --config Debug"),
                MakeTask("beast: clean", "clean ${workspaceFolder}"),
                MakeTask("beast: generate", "generate ${workspaceFolder}")),
        });

        if (host != null)
        {
            var launches = new JsonArray();
            foreach (var target in ProjectModel.Load(projectDir, host, TargetConfiguration.Debug).OwnTargets.Where(t => t.Rules.Type == TargetType.Application))
            {
                string binaries = Path.Combine(projectDir, "Binaries", host.PlatformName, "Debug");
                string program = "${workspaceFolder}/" + Path.GetRelativePath(projectDir, IdeGenerator.ProgramPath(binaries, host, target)).Replace('\\', '/');
                string cwd = "${workspaceFolder}/" + Path.GetRelativePath(projectDir, binaries).Replace('\\', '/');
                // Windows: Visual Studio debugger; Linux: gdb (Microsoft C/C++); macOS: CodeLLDB.
                JsonObject launch = HostPlatform.IsWindows
                    ? new() { ["type"] = "cppvsdbg", ["console"] = "integratedTerminal" }
                    : HostPlatform.IsMacOS
                        ? new() { ["type"] = "lldb" }
                        : new() { ["type"] = "cppdbg", ["MIMode"] = "gdb", ["externalConsole"] = false };
                launch["name"] = $"{target.Name} (Debug)";
                launch["request"] = "launch";
                launch["program"] = program;
                launch["args"] = new JsonArray();
                launch["cwd"] = cwd;
                launch["preLaunchTask"] = "beast: build Debug";
                launches.Add(launch);
            }
            if (launches.Count > 0) Write("launch.json", new JsonObject { ["version"] = "0.2.0", ["configurations"] = launches });
        }
        Write("settings.json", new JsonObject
        {
            ["C_Cpp.default.compileCommands"] = "${workspaceFolder}/compile_commands.json",
            ["clangd.arguments"] = new JsonArray("--compile-commands-dir=${workspaceFolder}"),
            ["files.exclude"] = new JsonObject { ["Intermediate"] = true },
        });
        return written;
    }
}

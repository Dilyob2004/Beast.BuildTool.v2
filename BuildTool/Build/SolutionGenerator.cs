using System.Text;
using System.Xml.Linq;
using System.Security.Cryptography;

namespace Beast.BuildTool;

/// <summary>Creates a Visual Studio Makefile project that delegates builds to this tool.</summary>
public static class SolutionGenerator
{
    /// <summary>Generates Visual Studio files on Windows hosts; elsewhere explains why it skips them.</summary>
    public static bool TryGenerate(string projectDir, string projectName)
    {
        if (!HostPlatform.IsWindows)
        {
            Log.TraceInfo("Visual Studio files are generated on Windows hosts only; run 'generate' there. compile_commands.json is written by 'build'.");
            return false;
        }
        Generate(projectDir, projectName);
        return true;
    }

    /// <summary>
    /// One Visual Studio configuration. Windows uses the standard x64/ARM64 platforms; Android uses
    /// configurations named "Android Debug" etc. on x64 (VS has no Android platform for Makefile
    /// projects without the Android workload), building every ABI from android.abis.
    /// </summary>
    private sealed record VsConfiguration(string Name, string VsPlatform, TargetConfiguration Configuration, Platform Platform,
        ProjectModel Model, string PlatformArguments, string BinaryDirectory, bool Android)
    {
        public string Key => Name + "|" + VsPlatform;
        public string Condition => $"'$(Configuration)|$(Platform)'=='{Key}'";
    }

    public static void Generate(string projectDir, string projectName)
    {
        projectDir = Path.GetFullPath(projectDir);
        using var projectLock = ProjectBuildLock.AcquireAsync(projectDir).GetAwaiter().GetResult();
        BuildConfigManager.LoadConfig(projectDir);
        AndroidPlatform? android = null;
        try { android = PlatformFactory.Create("Android", null)[0] as AndroidPlatform; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Log.TraceVerbose("Android configurations skipped: " + ex.Message); }
        Generate(projectDir, projectName, new WindowsPlatform(),
            WindowsPlatform.IsInstalled(PlatformArch.ARM64) ? new WindowsPlatform(PlatformArch.ARM64) : null, android);
    }

    /// <summary>Platforms are passed in so that generation is testable without Visual Studio.</summary>
    internal static void Generate(string projectDir, string projectName, Platform platform, Platform? windowsArm64, Platform? android)
    {
        var configurations = new[] { TargetConfiguration.Debug, TargetConfiguration.Development, TargetConfiguration.Release };
        var vs = new List<VsConfiguration>();
        void Add(Platform target, string vsPlatform, string prefix, string arguments, bool android)
        {
            foreach (var configuration in configurations)
                vs.Add(new(prefix + configuration, vsPlatform, configuration, target, ProjectModel.Load(projectDir, target, configuration), arguments,
                    android ? "Android" : target.PlatformName, android));
        }
        Add(platform, "x64", "", "--platform Windows", false);
        // Optional platforms appear only when their toolchains are installed.
        if (windowsArm64 != null) Add(windowsArm64, "ARM64", "", "--platform Windows --arch arm64", false);
        if (android != null) Add(android, "x64", "Android ", "--platform Android", true);

        var targets = vs[0].Model.UsedTargets;
        if (vs.Any(c => !c.Model.UsedTargets.Select(t => t.Name).SequenceEqual(targets.Select(t => t.Name))))
            throw new InvalidOperationException("Target names must be consistent across configurations and platforms.");
        if (vs.Any(c => c.Model.UsedTargets.Any(t => t.Rules.Type != targets.Single(d => d.Name == t.Name).Rules.Type)))
            throw new InvalidOperationException("A target's output type must be consistent across Visual Studio configurations.");
        var solutionProjects = new StringBuilder();
        var solutionConfigurations = new StringBuilder();
        string exe = Environment.ProcessPath ?? "Beast.BuildTool.exe";
        bool viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        string toolCommand = viaDotnet ? $"\"{exe}\" \"{typeof(SolutionGenerator).Assembly.Location}\"" : $"\"{exe}\"";
        string escapedProject = Escape(projectDir);
        string escapedToolCommand = Escape(toolCommand);
        var items = CollectProjectItems(projectDir);
        // External projects' sources (absolute paths), under External\<name> in Solution Explorer.
        foreach (var external in vs[0].Model.Externals)
            items.AddRange(CollectProjectItems(external.Directory, useAbsolutePaths: true)
                .Select(i => i with { LogicalPath = $"External\\{external.Name}\\{i.LogicalPath}" }));
        string toolset = PlatformToolset(VisualStudioInstance.VCToolChainPath);
        foreach (var target in targets)
        {
            string vcxproj = Path.Combine(projectDir, target.Name + ".vcxproj");
            string projectGuid = GetProjectGuid(vcxproj);
            string targetArgument = " --target " + target.Name;
            string projectItems = CreateProjectItems(items, projectDir, vs, target.Name);
            var xml = new StringBuilder();
            xml.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
            xml.AppendLine("""<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">""");
            xml.Append("""  <ItemGroup Label="ProjectConfigurations">""");
            foreach (var c in vs) xml.Append($"<ProjectConfiguration Include=\"{c.Key}\"><Configuration>{c.Name}</Configuration><Platform>{c.VsPlatform}</Platform></ProjectConfiguration>");
            xml.AppendLine("</ItemGroup>");
            xml.AppendLine($"""  <PropertyGroup Label="Globals"><ProjectGuid>{projectGuid}</ProjectGuid><Keyword>MakeFileProj</Keyword></PropertyGroup>""");
            xml.AppendLine("""  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />""");
            foreach (var c in vs)
                xml.AppendLine($"""  <PropertyGroup Condition="{c.Condition}" Label="Configuration"><ConfigurationType>Makefile</ConfigurationType><PlatformToolset>{toolset}</PlatformToolset></PropertyGroup>""");
            xml.Append(CreateIntelliSenseProperties(vs));
            xml.AppendLine("""  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />""");
            foreach (var c in vs)
            {
                string binaries = $"{escapedProject}\\Binaries\\{c.BinaryDirectory}\\{c.Configuration}";
                string output = c.Android
                    ? (target.Rules.Type == TargetType.Application ? target.Name + ".apk" : "lib" + target.Name + (target.Rules.Type == TargetType.StaticLibrary ? ".a" : ".so"))
                    : target.Name + (target.Rules.Type == TargetType.StaticLibrary ? ".lib" : target.Rules.Type == TargetType.SharedLibrary ? ".dll" : ".exe");
                string build = $"{escapedToolCommand} build &quot;{escapedProject}&quot;{targetArgument} {c.PlatformArguments} --config {c.Configuration}";
                string clean = $"{escapedToolCommand} clean &quot;{escapedProject}&quot;{targetArgument}";
                // F5: the Windows EXE; for an Android application, Beast's run command (install + launch on a device/emulator).
                string debugger = "", debuggerArguments = "";
                if (target.Rules.Type == TargetType.Application && !c.Android) debugger = binaries + "\\" + output;
                else if (target.Rules.Type == TargetType.Application && c.Android)
                {
                    debugger = Escape(exe);
                    debuggerArguments = (viaDotnet ? $"&quot;{Escape(typeof(SolutionGenerator).Assembly.Location)}&quot; " : "") +
                        $"run &quot;{escapedProject}&quot; --platform Android --target {target.Name} --config {c.Configuration} --no-build --wait";
                }
                xml.AppendLine($"""
                  <PropertyGroup Condition="{c.Condition}">
                    <OutDir>{binaries}\</OutDir>
                    <IntDir>{escapedProject}\Intermediate\Build\VisualStudio\{target.Name}\$(Platform)\$(Configuration)\</IntDir>
                    <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>
                    <LocalDebuggerCommand>{debugger}</LocalDebuggerCommand>
                    <LocalDebuggerCommandArguments>{debuggerArguments}</LocalDebuggerCommandArguments>
                    <LocalDebuggerWorkingDirectory>$(ProjectDir)</LocalDebuggerWorkingDirectory>
                    <NMakeIncludeSearchPath>$(ProjectDir)Source;$(NMakeIncludeSearchPath)</NMakeIncludeSearchPath>
                    <NMakeReBuildCommandLine>{clean} &amp;&amp; {build}</NMakeReBuildCommandLine>
                    <NMakeBuildCommandLine>{build}</NMakeBuildCommandLine>
                    <NMakeCleanCommandLine>{clean}</NMakeCleanCommandLine>
                    <NMakeOutput>{binaries}\{output}</NMakeOutput>
                  </PropertyGroup>
                """);
            }
            xml.AppendLine("""
              <ItemGroup>
                <None Include="Config\BuildConfig.json" />
              </ItemGroup>
            """);
            xml.AppendLine(projectItems);
            xml.AppendLine("""  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />""");
            xml.AppendLine("</Project>");
            File.WriteAllText(vcxproj, xml.ToString(), Encoding.UTF8);
            WriteProjectFilters(vcxproj + ".filters", items);
            solutionProjects.AppendLine($"Project(\"{{BC8A1FFA-BEE3-4634-8014-F334798102B3}}\") = \"{target.Name}\", \"{target.Name}.vcxproj\", \"{projectGuid}\"");
            solutionProjects.AppendLine("EndProject");
            foreach (var c in vs)
            {
                solutionConfigurations.AppendLine($"\t\t{projectGuid}.{c.Key}.ActiveCfg = {c.Key}");
                solutionConfigurations.AppendLine($"\t\t{projectGuid}.{c.Key}.Build.0 = {c.Key}");
            }
        }
        var sln = new StringBuilder();
        sln.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sln.AppendLine("# Visual Studio Version 17");
        sln.AppendLine(solutionProjects.ToString().TrimEnd());
        sln.AppendLine("Global");
        sln.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (var c in vs) sln.AppendLine($"\t\t{c.Key} = {c.Key}");
        sln.AppendLine("\tEndGlobalSection");
        sln.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        sln.Append(solutionConfigurations);
        sln.AppendLine("\tEndGlobalSection");
        sln.AppendLine("EndGlobal");
        File.WriteAllText(Path.Combine(projectDir, projectName + ".sln"), sln.ToString(), Encoding.UTF8);
    }

    private static string CreateIntelliSenseProperties(IReadOnlyList<VsConfiguration> configurations)
    {
        // Microsoft.Cpp.props imports Microsoft.Makefile.props, which copies
        // these properties into ClCompile metadata. Define them BEFORE that
        // import; otherwise the IDE's design-time compiler sees empty options.
        // Actual compilation is still performed by Beast.
        var text = new StringBuilder();
        text.AppendLine("""
              <PropertyGroup Label="BeastIntelliSense">
                <AdditionalOptions>$(AdditionalOptions) /std:c++20 /utf-8</AdditionalOptions>
              </PropertyGroup>
            """);
        foreach (var c in configurations)
        {
            var environment = GenericProjectBuilder.CreateCppEnvironment(c.Platform, c.Configuration, []);
            text.AppendLine($"""
                  <PropertyGroup Condition="{c.Condition}">
                    <NMakePreprocessorDefinitions>$(NMakePreprocessorDefinitions);{string.Join(';', environment.PreprocessorDefinitions)}</NMakePreprocessorDefinitions>
                  </PropertyGroup>
                """);
        }
        return text.ToString();
    }

    /// <summary>MSBuild toolset of the MSVC used by the build: 14.2x -> v142, 14.3x -> v143, 14.5x -> v145.</summary>
    private static string PlatformToolset(string? vcToolPath) =>
        Version.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(vcToolPath ?? "")), out var version) && version.Major == 14
            ? $"v14{version.Minor / 10}" : "v143";

    private static string GetProjectGuid(string projectFile)
    {
        // Preserve VS startup-project and debugger selections when regenerating an existing project.
        if (File.Exists(projectFile))
        {
            try
            {
                XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
                string? existing = XDocument.Load(projectFile).Descendants(ns + "ProjectGuid").FirstOrDefault()?.Value;
                if (Guid.TryParse(existing, out Guid guid))
                    return guid.ToString("B").ToUpperInvariant();
            }
            catch (System.Xml.XmlException)
            {
                // A malformed generated project can still be regenerated with a stable identity.
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectFile).ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
    }

    private sealed record ProjectItem(string ItemType, string IncludePath, string LogicalPath);

    private static List<ProjectItem> CollectProjectItems(string projectDir, bool useAbsolutePaths = false)
    {
        string sourceDirectory = Path.Combine(projectDir, "Source");
        if (!Directory.Exists(sourceDirectory))
            return [];

        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Extension = Path.GetExtension(path).ToLowerInvariant() })
            .Where(file => file.Extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".ixx" or ".cppm" or ".m" or ".mm" or ".h" or ".hh" or ".hpp" or ".hxx" or ".cs" or ".hlsl" or ".hlsli" or ".asm" or ".s")
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                // .m/.mm (macOS) are listed for browsing; ExcludedFromBuild keeps them out of Windows builds.
                string itemType = file.Extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".ixx" or ".cppm" or ".m" or ".mm"
                    ? "ClCompile"
                    : file.Extension is ".cs" or ".hlsl" or ".hlsli" or ".asm" or ".s" ? "None" : "ClInclude";
                string relativePath = Path.GetRelativePath(projectDir, file.Path).Replace('/', '\\');
                return new ProjectItem(itemType, useAbsolutePaths ? Path.GetFullPath(file.Path) : relativePath, relativePath);
            })
            .ToList();
    }

    private static string CreateProjectItems(List<ProjectItem> items, string projectDir, IReadOnlyList<VsConfiguration> configurations, string targetName)
    {
        // This fragment inherits the enclosing Project namespace. MSBuild rejects
        // a redundant xmlns attribute on ItemGroup even though it is valid XML.
        XNamespace ns = "";
        var group = new XElement(ns + "ItemGroup");
        var sources = new Dictionary<(VsConfiguration, string), HashSet<string>>();
        HashSet<string> SourcesOf(VsConfiguration c, ModuleRules module) => sources.TryGetValue((c, module.ModuleName), out var known) ? known
            : sources[(c, module.ModuleName)] = c.Model.Sources(module).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var element = new XElement(ns + item.ItemType, new XAttribute("Include", item.IncludePath));
            if (item.ItemType == "ClCompile")
            {
                string file = Path.GetFullPath(Path.Combine(projectDir, item.IncludePath.Replace('\\', Path.DirectorySeparatorChar)));
                // IntelliSense: module interface units and internal partitions are parsed as modules.
                ModuleUnitInfo unit;
                try { unit = CppModules.Scan(file); } catch (InvalidOperationException) { unit = ModuleUnitInfo.None; }
                if (unit.ProducesInterface)
                    element.Add(new XElement(ns + "CompileAs", unit.Exported ? "CompileAsCppModule" : "CompileAsCppModuleInternalPartition"));
                foreach (var c in configurations)
                {
                    var target = c.Model.SelectTarget(targetName);
                    var owner = c.Model.ResolveModules(target).Where(m => file.StartsWith(m.ModuleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.ModuleDirectory.Length).FirstOrDefault();
                    if (owner == null) continue;
                    // A module compiled by a dependency target belongs to that target's project (with its
                    // own export macros); listing it here as buildable would show wrong IntelliSense.
                    if (c.Model.Provider(target, owner) != null)
                    {
                        element.Add(new XElement(ns + "ExcludedFromBuild", new XAttribute("Condition", c.Condition), "true"));
                        continue;
                    }
                    // A file of another platform (Private/Android/...) is not part of this configuration's build.
                    if (owner.Type == ModuleType.Source && !SourcesOf(c, owner).Contains(file))
                    {
                        element.Add(new XElement(ns + "ExcludedFromBuild", new XAttribute("Condition", c.Condition), "true"));
                        continue;
                    }
                    var environment = c.Model.CompileEnvironment(target, owner, c.Platform, c.Configuration);
                    // Android IntelliSense also needs the NDK sysroot and libc++ headers.
                    var includes = environment.IncludePaths.Concat(environment.ExternalIncludePaths);
                    if (c.Platform is AndroidPlatform androidPlatform && androidPlatform is var android)
                        includes = includes.Concat(new[] { Path.Combine(android.ToolchainRoot, "sysroot", "usr", "include", "c++", "v1") })
                            .Concat(android.SystemIncludePaths).Append(Path.Combine(android.ToolchainRoot, "sysroot", "usr", "include", android.Triple));
                    element.Add(new XElement(ns + "AdditionalIncludeDirectories", new XAttribute("Condition", c.Condition),
                        string.Join(';', includes) + ";%(AdditionalIncludeDirectories)"));
                    element.Add(new XElement(ns + "PreprocessorDefinitions", new XAttribute("Condition", c.Condition),
                        string.Join(';', environment.PreprocessorDefinitions) + ";%(PreprocessorDefinitions)"));
                    string standard = environment.CppVersion switch { CppVersion.Cpp14 => "stdcpp14", CppVersion.Cpp17 => "stdcpp17", CppVersion.Latest => "stdcpplatest", _ => "stdcpp20" };
                    element.Add(new XElement(ns + "LanguageStandard", new XAttribute("Condition", c.Condition), standard));
                    string option = environment.CppVersion switch { CppVersion.Cpp14 => "c++14", CppVersion.Cpp17 => "c++17", CppVersion.Latest => "c++latest", _ => "c++20" };
                    element.Add(new XElement(ns + "AdditionalOptions", new XAttribute("Condition", c.Condition), $"%(AdditionalOptions) /std:{option} /utf-8"));
                }
            }
            group.Add(element);
        }
        return group.ToString();
    }

    private static void WriteProjectFilters(string path, List<ProjectItem> items)
    {
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string folder = item.LogicalPath;
            while (folder.LastIndexOf('\\') is int separator && separator >= 0)
            {
                folder = folder[..separator];
                folders.Add(folder);
            }
        }

        var root = new XElement(ns + "Project", new XAttribute("ToolsVersion", "4.0"));
        if (folders.Count > 0)
            root.Add(new XElement(ns + "ItemGroup", folders.Select(folder =>
                new XElement(ns + "Filter", new XAttribute("Include", folder)))));

        if (items.Count > 0)
            root.Add(new XElement(ns + "ItemGroup", items.Select(item =>
            {
                var element = new XElement(ns + item.ItemType, new XAttribute("Include", item.IncludePath));
                int separator = item.LogicalPath.LastIndexOf('\\');
                if (separator >= 0)
                    element.Add(new XElement(ns + "Filter", item.LogicalPath[..separator]));
                return element;
            })));

        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

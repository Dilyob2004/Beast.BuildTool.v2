// Model tests: project rules, targets, platform filtering, compile options, API macros, migrate,
// Visual Studio generation and locks. No compiler is needed: Android platforms are created over an
// empty fake NDK, so this runs on Windows and Linux (CI) alike.
using Beast.BuildTool;

Log.Init();
int failures = 0;
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Generated", "Model " + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
void Expect(Action action, string fragment)
{
    try { action(); }
    catch (Exception ex) when (ex.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return; }
    catch (Exception ex) { throw new Exception($"Expected an error containing '{fragment}', got: {ex.Message}"); }
    throw new Exception($"Expected an error containing '{fragment}'");
}
void Test(string name, Action body)
{
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
        // GitHub Actions annotation: readable without the job log.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null)
            Console.WriteLine("::error title=ModelTests::" + $"{name}: {ex.Message}\n{ex.StackTrace}".Replace("%", "%25").Replace("\r", "").Replace("\n", "%0A"));
    }
}
string Module(string name, string body = "") =>
    $"using Beast.BuildTool;\npublic sealed class {name} : ModuleRules {{ public {name}(ModulesInfo info) {{ PublicIncludePaths.Add(\"Public\"); {body} }} }}\n";
string Target(string name, string body) =>
    $"using Beast.BuildTool;\npublic sealed class {name}Target : TargetRules {{ public {name}Target(ModulesInfo info) {{ {body} }} }}\n";

// Fake NDK: AndroidPlatform only checks that the layout and clang++ exist.
string ndk = Path.Combine(root, "ndk", "27.0.0");
Write(Path.Combine(ndk, "source.properties"), "Pkg.Revision = 27.0.0\n");
string bin = Path.Combine(ndk, "toolchains", "llvm", "prebuilt", HostPlatform.NdkHostTag, "bin");
Write(Path.Combine(bin, HostPlatform.Executable("clang++")), "");
Directory.CreateDirectory(Path.Combine(ndk, "toolchains", "llvm", "prebuilt", HostPlatform.NdkHostTag, "sysroot", "usr", "include"));
BuildConfigManager.Config.Android.NdkRoot = Path.GetDirectoryName(ndk)!;
var arm64 = new AndroidPlatform("arm64-v8a");
var x64 = new AndroidPlatform("x86_64");

// Project: Engine (SharedLibrary: Core, Render), Tools (StaticLibrary: Util), Game (Application: Launch).
string project = Path.Combine(root, "Demo");
string source = Path.Combine(project, "Source");
Write(Path.Combine(project, "Config", "BuildConfig.json"), "{}");
Write(Path.Combine(source, "Core", "Core.Build.cs"), Module("Core",
    "PublicDefinitions.Add(\"CORE_PUBLIC=1\"); Definitions.Add(\"CORE_PRIVATE=1\"); ExcludedSources.Add(\"Experimental/**\"); ExcludedSources.Add(\"Skip*.cpp\"); CompileOptions.Favor = FavorSizeOrSpeed.SmallCode; CompileOptions.Warnings = WarningLevel.High;"));
foreach (string file in new[] { "Core.cpp", "Windows/CoreWindows.cpp", "Android/CoreAndroid.cpp", "Linux/CoreLinux.cpp",
             "Experimental/Deep/New.cpp", "SkipMe.cpp", "Private/Helper.c", "Public/Core.h" })
    Write(Path.Combine(source, "Core", file), "// fixture\n");
Write(Path.Combine(source, "Render", "Render.Build.cs"), Module("Render", "PrivateDependencies.Add(\"Core\"); ApiMacro = \"RENDER_EXPORT\"; UsePlatformDirectories = false;"));
Write(Path.Combine(source, "Render", "Render.cpp"), "");
Write(Path.Combine(source, "Render", "Windows", "RenderWindows.cpp"), "");
Write(Path.Combine(source, "Util", "Util.Build.cs"), Module("Util", "ApiMacro = \"\";"));
Write(Path.Combine(source, "Util", "Util.cpp"), "");
Write(Path.Combine(source, "Launch", "Launch.Build.cs"), Module("Launch", "PrivateDependencies.Add(\"Render\"); PrivateDependencies.Add(\"Util\");"));
Write(Path.Combine(source, "Launch", "Main.cpp"), "");
Write(Path.Combine(source, "Engine.Target.cs"), Target("Engine",
    "Type = TargetType.SharedLibrary; Modules.Add(\"Render\"); CompileOptions.Favor = FavorSizeOrSpeed.FastCode; CompileOptions.Exceptions = false;"));
Write(Path.Combine(source, "Tools.Target.cs"), Target("Tools", "Type = TargetType.StaticLibrary; Modules.Add(\"Util\");"));
Write(Path.Combine(source, "Game.Target.cs"), Target("Game",
    "Modules.Add(\"Launch\"); TargetDependencies.Add(\"Engine\"); TargetDependencies.Add(\"Tools\"); Definitions.Add(\"GAME=1\"); LinkTimeOptimization = true;"));
Write(Path.Combine(source, "GameTests.Target.cs"), Target("GameTests",
    "Modules.Add(\"Launch\"); IsTest = true; TestArguments.Add(\"--fast\"); Sanitizers = Sanitizer.Address | Sanitizer.Undefined;"));

ProjectModel model = ProjectModel.Load(project, arm64, TargetConfiguration.Release);
BuildTarget T(string name) => model.Targets.Single(t => t.Name == name);
ModuleRules M(string name) => model.Modules[name];
List<string> Names(IEnumerable<string> files) => files.Select(f => Path.GetRelativePath(source, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

Test("platform directories and ExcludedSources", () =>
{
    var core = Names(model.Sources(M("Core")));
    Check(core.Contains("Core/Core.cpp") && core.Contains("Core/Android/CoreAndroid.cpp") && core.Contains("Core/Private/Helper.c"), "Missing sources: " + string.Join(", ", core));
    Check(!core.Any(f => f.Contains("Windows/") || f.Contains("Linux/")), "Other-platform sources compiled: " + string.Join(", ", core));
    Check(!core.Any(f => f.Contains("Experimental") || f.Contains("SkipMe")), "Excluded sources compiled: " + string.Join(", ", core));
    Check(Names(model.Sources(M("Render"))).Contains("Render/Windows/RenderWindows.cpp"), "UsePlatformDirectories=false ignored");
});

Test("target dependencies", () =>
{
    var game = T("Game");
    Check(model.TargetDependencies(game).Select(t => t.Name).Order().SequenceEqual(new[] { "Engine", "Tools" }), "Dependency targets");
    Check(model.OwnModules(game).Select(m => m.ModuleName).SequenceEqual(new[] { "Launch" }), "Game compiles modules of its dependencies");
    Check(model.Provider(game, M("Core"))?.Name == "Engine" && model.Provider(game, M("Util"))?.Name == "Tools", "Wrong provider");
    Check(model.LinkedTargets(game).Count == 2, "Linked targets");
    // A test target without TargetDependencies compiles everything itself.
    Check(model.OwnModules(T("GameTests")).Count == 4, "Test target modules");
});

Test("API macros", () =>
{
    Check(ProjectModel.ApiMacroName(M("Core")) == "CORE_API" && ProjectModel.ApiMacroName(M("Render")) == "RENDER_EXPORT" && ProjectModel.ApiMacroName(M("Util")) == null, "Macro names");
    Check(model.ApiMacroValue(T("Engine"), M("Core"), PlatformType.Windows) == "__declspec(dllexport)", "Engine exports");
    Check(model.ApiMacroValue(T("Game"), M("Core"), PlatformType.Windows) == "__declspec(dllimport)", "Game imports");
    Check(model.ApiMacroValue(T("Game"), M("Core"), PlatformType.Android).Contains("visibility(\"default\")"), "Android visibility");
    Check(model.ApiMacroValue(T("Game"), M("Launch"), PlatformType.Windows) == "", "Application module exported");
    Check(model.ApiMacroValue(T("GameTests"), M("Core"), PlatformType.Windows) == "", "Static build exported");
    var env = model.CompileEnvironment(T("Game"), M("Launch"), arm64, TargetConfiguration.Release);
    Check(env.PreprocessorDefinitions.Contains("RENDER_EXPORT=__attribute__((visibility(\"default\")))"), "RENDER_EXPORT missing in consumer");
    Check(!env.PreprocessorDefinitions.Any(d => d.StartsWith("CORE_")), "Private dependency of Render leaked into Launch");
    Check(!env.PreprocessorDefinitions.Any(d => d.StartsWith("UTIL_API")), "Empty ApiMacro defined");
    Check(env.PreprocessorDefinitions.Contains("GAME=1"), "Target definition missing");
});

Test("compile options, LTO and sanitizers", () =>
{
    var core = model.CompileEnvironment(T("Engine"), M("Core"), arm64, TargetConfiguration.Release);
    Check(core.FavorSizeOrSpeed == FavorSizeOrSpeed.SmallCode, "Module Favor does not override target");
    Check(core.Warnings == WarningLevel.High && !core.EnableExceptions, "Module/target options not merged");
    Check(core.PreprocessorDefinitions.Contains("CORE_PRIVATE=1") && core.PreprocessorDefinitions.Contains("CORE_PUBLIC=1"), "Core definitions");
    var render = model.CompileEnvironment(T("Engine"), M("Render"), arm64, TargetConfiguration.Release);
    Check(render.FavorSizeOrSpeed == FavorSizeOrSpeed.FastCode, "Target Favor not inherited");
    Check(render.PreprocessorDefinitions.Contains("CORE_PUBLIC=1") && !render.PreprocessorDefinitions.Contains("CORE_PRIVATE=1"), "Definition visibility");
    Check(model.CompileEnvironment(T("Game"), M("Launch"), arm64, TargetConfiguration.Release).WholeProgramOptimization, "LTO off in Release");
    var debugModel = ProjectModel.Load(project, arm64, TargetConfiguration.Debug);
    var debugGame = debugModel.Targets.Single(t => t.Name == "Game");
    Check(!debugModel.CompileEnvironment(debugGame, debugModel.Modules["Launch"], arm64, TargetConfiguration.Debug).WholeProgramOptimization, "LTO on in Debug");
    Check(debugModel.CompileEnvironment(debugGame, debugModel.Modules["Launch"], arm64, TargetConfiguration.Debug).DebugInformation, "No debug info in Debug");
    Check(model.CompileEnvironment(T("Game"), M("Launch"), arm64, TargetConfiguration.Release).DebugInformation, "No debug info in Release");
    var tests = model.CompileEnvironment(T("GameTests"), M("Launch"), x64, TargetConfiguration.Debug);
    Check(tests.Sanitizers == (Sanitizer.Address | Sanitizer.Undefined) && !tests.RuntimeChecks, "Sanitizers");
    Expect(() => SanitizerSupport.Validate(PlatformType.Windows, Sanitizer.Undefined, "X"), "not supported");
    Check(T("GameTests").Rules.IsTest && T("GameTests").Rules.TestArguments.SequenceEqual(new[] { "--fast" }), "Test target rules");
});

Test("invalid target dependencies", () =>
{
    string bad = Path.Combine(root, "Bad");
    Write(Path.Combine(bad, "Config", "BuildConfig.json"), "{}");
    Write(Path.Combine(bad, "Source", "A", "A.Build.cs"), Module("A"));
    Write(Path.Combine(bad, "Source", "A", "A.cpp"), "");
    void Targets(string one, string two)
    {
        Write(Path.Combine(bad, "Source", "One.Target.cs"), Target("One", one));
        Write(Path.Combine(bad, "Source", "Two.Target.cs"), Target("Two", two));
    }
    Targets("Type = TargetType.StaticLibrary; Modules.Add(\"A\"); TargetDependencies.Add(\"Two\");", "Type = TargetType.StaticLibrary; Modules.Add(\"A\"); TargetDependencies.Add(\"One\");");
    Expect(() => ProjectModel.Load(bad, arm64, TargetConfiguration.Debug), "cycle");
    Targets("Modules.Add(\"A\"); TargetDependencies.Add(\"Missing\");", "Type = TargetType.StaticLibrary; Modules.Add(\"A\");");
    Expect(() => ProjectModel.Load(bad, arm64, TargetConfiguration.Debug), "unknown dependency target");
    Targets("Modules.Add(\"A\"); TargetDependencies.Add(\"Two\");", "Modules.Add(\"A\");");
    Expect(() => ProjectModel.Load(bad, arm64, TargetConfiguration.Debug), "is an Application");
    Targets("Modules.Add(\"A\"); TargetDependencies.Add(\"Two\");", "Type = TargetType.StaticLibrary; Modules.Add(\"A\");");
    Expect(() => ProjectModel.Load(bad, arm64, TargetConfiguration.Debug), "already built by dependency target");
});

Test("migrate", () =>
{
    string old = Path.Combine(root, "Old");
    Write(Path.Combine(old, "Config", "BuildConfig.json"), "{}");
    Write(Path.Combine(old, "Source", "Launch", "Launch.Build.cs"), Module("Launch"));
    Write(Path.Combine(old, "Source", "Core", "Core.Build.cs"), Module("Core"));
    Check(ProjectModel.Load(old, arm64, TargetConfiguration.Debug).Targets.Single().Legacy, "Not legacy");
    string file = ProjectModel.Migrate(old);
    Check(Path.GetFileName(file) == "Old.Target.cs", "Target file name");
    var migrated = ProjectModel.Load(old, arm64, TargetConfiguration.Debug).Targets.Single();
    Check(!migrated.Legacy && migrated.Name == "Old" && migrated.Rules.Modules.SetEquals(new[] { "Core", "Launch" }), "Migrated target");
    Expect(() => ProjectModel.Migrate(old), "already has");
});

Test("Visual Studio generation", () =>
{
    // Android platforms stand in for Windows x64/ARM64: generation only plans, it does not compile.
    SolutionGenerator.Generate(project, "Demo", arm64, x64, arm64);
    string sln = File.ReadAllText(Path.Combine(project, "Demo.sln"));
    foreach (string name in new[] { "Game", "Engine", "Tools", "GameTests" })
        Check(sln.Contains(name + ".vcxproj"), name + " missing from solution");
    foreach (string configuration in new[] { "Debug|x64", "Release|ARM64", "Android Debug|x64", "Android Release|x64" })
        Check(sln.Contains(configuration), configuration + " missing from solution");
    string game = File.ReadAllText(Path.Combine(project, "Game.vcxproj"));
    Check(game.Contains("--target Game") && game.Contains("--arch arm64") && game.Contains("--platform Android"), "Build commands");
    Check(game.Contains(@"Source\Launch\Main.cpp"), "Game sources");
    var render = System.Xml.Linq.XDocument.Parse(game).Descendants().Single(e => e.Name.LocalName == "ClCompile" && (string?)e.Attribute("Include") == @"Source\Render\Render.cpp");
    Check(render.Elements().All(e => e.Name.LocalName == "ExcludedFromBuild"), "Game compiles sources of its dependency target");
    Check(game.Contains(Path.Combine(source, "Render", "Public")), "Include path of a dependency target missing");
    string engine = File.ReadAllText(Path.Combine(project, "Engine.vcxproj"));
    Check(engine.Contains(@"Source\Core\Android\CoreAndroid.cpp") && engine.Contains(@"Source\Render\Render.cpp"), "Engine sources");
});

Test("external projects", () =>
{
    // SDK: static library target SDK (Widgets -> Core); App uses it through externalProjects.
    string sdk = Path.Combine(root, "Ext", "SDK"), app = Path.Combine(root, "Ext", "App");
    Write(Path.Combine(sdk, "Source", "Core", "Core.Build.cs"), Module("Core",
        "PublicDefinitions.Add(\"SDK_NAME_\" + info.ExternalProjectName); PrivateIncludePaths.Add(info.ProjectSourceDirectory!);"));
    Write(Path.Combine(sdk, "Source", "Core", "Core.cpp"), "");
    Write(Path.Combine(sdk, "Source", "Widgets", "Widgets.Build.cs"), Module("Widgets", "PublicDependencies.Add(\"Core\"); Resources.Add(\"{ProjectDir}/Data\");"));
    Write(Path.Combine(sdk, "Source", "Widgets", "Widgets.cpp"), "");
    Write(Path.Combine(sdk, "Source", "SDK.Target.cs"), Target("SDK", "Type = TargetType.StaticLibrary; Modules.Add(\"Widgets\");"));
    Write(Path.Combine(app, "Config", "BuildConfig.json"), "{}");
    Write(Path.Combine(app, "Source", "Main", "Main.Build.cs"), Module("Main", "PrivateDependencies.Add(\"Widgets\");"));
    Write(Path.Combine(app, "Source", "Main", "Main.cpp"), "");
    Write(Path.Combine(app, "Source", "App.Target.cs"), Target("App", "Modules.Add(\"Main\"); TargetDependencies.Add(\"SDK\");"));
    var externals = new List<ExternalProjectConfig> { new() { Name = "SDK", Path = "../SDK" } };
    var model = ProjectModel.Load(app, arm64, TargetConfiguration.Debug, externals);
    Check(model.Externals.Single().Directory == sdk, "external directory");
    var sdkTarget = model.Targets.Single(t => t.Name == "SDK");
    Check(sdkTarget.ExternalProject == "SDK" && model.SelectTarget(null).Name == "App" && model.OwnTargets.Count == 1, "targets");
    Check(model.BuildDirectory(arm64.PlatformName, TargetConfiguration.Debug, "SDK") ==
        Path.Combine(app, "Intermediate", "Build", arm64.PlatformName, "Debug", "External", "SDK", "SDK"), "external build directory");
    var core = model.Modules["Core"];
    Check(core.ProjectDirectory == sdk && core.ExternalProject == "SDK" && core.PublicDefinitions.Contains("SDK_NAME_SDK"), "ModulesInfo inside external rules");
    Check(core.PrivateIncludePaths.Contains(Path.Combine(sdk, "Source")), "info.ProjectSourceDirectory of the external project");
    Check(model.OwnModules(model.SelectTarget(null)).Select(m => m.ModuleName).SequenceEqual(new[] { "Main" }), "SDK modules are compiled by the SDK target");
    var main = model.CompileEnvironment(model.SelectTarget(null), model.Modules["Main"], arm64, TargetConfiguration.Debug);
    Check(main.IncludePaths.Contains(Path.Combine(app, "Source")) && main.IncludePaths.Contains(Path.Combine(sdk, "Source")), "consumer sees the external Source/");
    Check(main.PreprocessorDefinitions.Contains("SDK_NAME_SDK"), "public definitions of external modules");
    var widgets = model.CompileEnvironment(sdkTarget, model.Modules["Widgets"], arm64, TargetConfiguration.Debug);
    Check(widgets.IncludePaths.Contains(Path.Combine(sdk, "Source")) && !widgets.IncludePaths.Contains(Path.Combine(app, "Source")), "external modules do not see the application's Source/");
    var plan = new BuildStepPlan(app, model.BuildDirectory(arm64.PlatformName, TargetConfiguration.Debug, "SDK"), Path.Combine(app, "Binaries"), "P", "Debug", "SDK", []);
    Check(plan.Expand(model.Modules["Widgets"], "{ProjectDir}") == sdk, "{ProjectDir} of an external module");

    // Name conflicts, missing projects and recursion through the external project's own config.
    Write(Path.Combine(app, "Source", "Core", "Core.Build.cs"), Module("Core"));
    Expect(() => ProjectModel.Load(app, arm64, TargetConfiguration.Debug, externals), "external project 'SDK'");
    File.Delete(Path.Combine(app, "Source", "Core", "Core.Build.cs"));
    Expect(() => ProjectModel.Load(app, arm64, TargetConfiguration.Debug, [new() { Name = "SDK", Path = "../Missing" }]), "not found");
    string shared = Path.Combine(root, "Ext", "Shared");
    Write(Path.Combine(shared, "Source", "Util", "Util.Build.cs"), Module("Util"));
    Write(Path.Combine(shared, "Source", "Util", "Util.cpp"), "");
    Write(Path.Combine(sdk, "Config", "BuildConfig.json"), "{ \"externalProjects\": [ { \"name\": \"Shared\", \"path\": \"../Shared\" } ] }");
    var nested = ProjectModel.Load(app, arm64, TargetConfiguration.Debug, externals);
    Check(nested.Externals.Select(e => e.Name).SequenceEqual(new[] { "Shared", "SDK" }) && nested.Modules["Util"].ExternalProject == "Shared", "external projects of external projects");
    Environment.SetEnvironmentVariable("BEAST_TEST_SDK", sdk);
    Check(ProjectModel.Load(app, arm64, TargetConfiguration.Debug, [new() { Name = "SDK", Path = "%BEAST_TEST_SDK%" }]).Externals.Any(e => e.Directory == sdk), "%VAR% in the path");

    // Visual Studio: the SDK target gets a project; its sources appear under External\SDK.
    BuildConfigManager.Config.ExternalProjects = externals;
    try { SolutionGenerator.Generate(app, "App", arm64, x64, arm64); }
    finally { BuildConfigManager.Config.ExternalProjects = []; }
    Check(File.Exists(Path.Combine(app, "SDK.vcxproj")) && File.ReadAllText(Path.Combine(app, "App.sln")).Contains("SDK.vcxproj"), "SDK.vcxproj");
    Check(File.ReadAllText(Path.Combine(app, "App.vcxproj.filters")).Contains(@"External\SDK\Source\Widgets"), "external sources in filters");
});

Test("Android Java source destinations", () =>
{
    string java = Path.Combine(root, "Java");
    Write(Path.Combine(java, "A.java"), "// header\npackage com.beast.sdk.text;\npublic class A {}\n");
    Write(Path.Combine(java, "B.java"), "public class B {}\n");
    Write(Path.Combine(java, "C.kt"), "package com.beast.k\nclass C\n");
    Check(AndroidPackagePlan.JavaPath(Path.Combine(java, "A.java")) == "com/beast/sdk/text/A.java", "package path");
    Check(AndroidPackagePlan.JavaPath(Path.Combine(java, "B.java")) == "B.java", "default package");
    Check(AndroidPackagePlan.JavaPath(Path.Combine(java, "C.kt")) == "com/beast/k/C.kt", "Kotlin package without ';'");
});

Test("toolchain paths in ModulesInfo, AdditionalSources", () =>
{
    string project = Path.Combine(root, "Toolchain");
    Write(Path.Combine(ndk, "sources", "glue.c"), "");
    Write(Path.Combine(project, "Source", "Glue", "Glue.Build.cs"), Module("Glue",
        "Definitions.Add(\"ABI=\" + info.AndroidAbi); Definitions.Add(\"API=\" + info.AndroidApiLevel); " +
        "if (info.AndroidNdkRoot != null) AdditionalSources.Add(System.IO.Path.Combine(info.AndroidNdkRoot, \"sources\", \"glue.c\"));"));
    Write(Path.Combine(project, "Source", "Glue.Target.cs"), Target("Glue", "Type = TargetType.StaticLibrary; Modules.Add(\"Glue\");"));
    var model = ProjectModel.Load(project, x64, TargetConfiguration.Debug, []);
    var glue = model.Modules["Glue"];
    Check(glue.Definitions.Contains("ABI=x86_64") && glue.Definitions.Contains("API=" + BuildConfigManager.Config.Android.MinSdk), "Android ABI and API level");
    var plan = new BuildStepPlan(project, Path.Combine(project, "Intermediate"), Path.Combine(project, "Binaries"), "P", "Debug", "Glue", []);
    Check(plan.AdditionalSources(glue).SequenceEqual(new[] { Path.Combine(ndk, "sources", "glue.c") }), "AdditionalSources from the NDK");
    glue.AdditionalSources.Add("Missing.c");
    Expect(() => plan.AdditionalSources(glue), "not found");
});

Test("watch filters, build tool version", () =>
{
    Check(Watcher.IsRelevant("/p/Source/Core/Core.cpp") && Watcher.IsRelevant(@"C:\p\Source\Core\Core.h"), "sources are relevant");
    foreach (string ignored in new[] { "/p/Source/Core/Intermediate/x.o", "/p/Source/.git/HEAD", "/p/Source/Core/.Core.cpp.swp", "/p/Source/Core/Core.cpp~", "/p/Source/x.tmp", "/p/Source/Core/4913" })
        Check(!Watcher.IsRelevant(ignored), "ignored: " + ignored);
    Check(BuildToolVersion.Parse("2.1.0-dev+abc1234") == new Version(2, 1, 0) && BuildToolVersion.Parse("v3") == new Version(3, 0, 0), "version parsing");
    BuildToolVersion.Require("2.1.0", "BuildConfig.json", "2.1.0-dev+abc");
    BuildToolVersion.Require("", "BuildConfig.json", "1.0.0");
    Expect(() => BuildToolVersion.Require("2.2", "BuildConfig.json", "2.1.9"), "needs Beast BuildTool 2.2 or newer");
    Expect(() => BuildToolVersion.Require("latest", "BuildConfig.json", "2.1.0"), "is not a version");
});

Test("Xcode and VS Code generation", () =>
{
    // The Android platform stands in for macOS: generation only lists targets and paths.
    string xcode = XcodeGenerator.Generate(project, "Demo", arm64, iosSimulator: true);
    string pbx = File.ReadAllText(Path.Combine(xcode, "project.pbxproj"));
    Check(pbx.StartsWith("// !$*UTF8*$!") && pbx.Count(c => c == '{') == pbx.Count(c => c == '}') && pbx.Count(c => c == '(') == pbx.Count(c => c == ')'), "pbxproj structure");
    foreach (string name in new[] { "name = Game;", "name = Engine;", "name = Tools;", "name = \"Game iOS Simulator\";", "isa = PBXLegacyTarget", "path = Main.cpp;", "path = BuildConfig.json;" })
        Check(pbx.Contains(name), name + " missing from project.pbxproj");
    Check(!pbx.Contains("GameTests iOS Simulator") && !pbx.Contains("Engine iOS Simulator"), "iOS Simulator targets are for applications only");
    Check(pbx.Contains("$(CONFIGURATION) Mac") && pbx.Contains("Game $(CONFIGURATION) IOSSimulator"), "build arguments");
    string schemes = Path.Combine(xcode, "xcshareddata", "xcschemes");
    var game = System.Xml.Linq.XDocument.Load(Path.Combine(schemes, "Game.xcscheme"));
    string runnable = (string)game.Descendants("PathRunnable").Single().Attribute("FilePath")!;
    Check(runnable == Path.Combine(project, "Binaries", arm64.PlatformName, "Debug", "Game" + arm64.ExecutableFileExtension), "Run path: " + runnable);
    Check(!System.Xml.Linq.XDocument.Load(Path.Combine(schemes, "Engine.xcscheme")).Descendants("PathRunnable").Any(), "Library scheme runs nothing");
    Check(File.ReadAllText(Path.Combine(schemes, "Game iOS Simulator.xcscheme")).Contains("IOSSimulator"), "iOS Simulator scheme");
    string script = File.ReadAllText(Path.Combine(xcode, "beast-xcode.sh"));
    Check(script.StartsWith("#!/bin/sh\n") && script.Contains(" build \"$project\"") && script.Contains(" clean \"$project\"") && !script.Contains('\r'), "build script");

    string vscode = Path.Combine(project, ".vscode");
    if (Directory.Exists(vscode)) Directory.Delete(vscode, true);
    var written = VsCodeGenerator.Generate(project, arm64);
    Check(written.Count == 3, "VS Code files: " + written.Count);
    var tasks = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(vscode, "tasks.json")))!;
    Check(tasks["tasks"]!.AsArray().Any(t => (string?)t!["label"] == "beast: build Debug" && t["group"] != null), "default build task");
    var launch = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(vscode, "launch.json")))!["configurations"]!.AsArray();
    Check(launch.Any(l => (string?)l!["name"] == "Game (Debug)" && ((string)l["program"]!).EndsWith("/Debug/Game" + arm64.ExecutableFileExtension)), "Game launch");
    Check(launch.All(l => !((string)l!["name"]!).StartsWith("Engine")), "libraries are not launched");
    File.WriteAllText(Path.Combine(vscode, "tasks.json"), "{}");
    Check(VsCodeGenerator.Generate(project, arm64).Count == 0 && File.ReadAllText(Path.Combine(vscode, "tasks.json")) == "{}", "existing VS Code files are kept");
});

Test("platform source rules (Linux, macOS)", () =>
{
    Check(SourceFilter.IsOtherPlatform("Private/Linux/X.cpp", PlatformType.Mac) && !SourceFilter.IsOtherPlatform("Private/Linux/X.cpp", PlatformType.Linux), "Linux directory");
    Check(SourceFilter.IsOtherPlatform("Mac/X.cpp", PlatformType.Linux) && !SourceFilter.IsOtherPlatform("MacOS/X.cpp", PlatformType.Mac), "Mac directory");
    Check(SourceFilter.IsOtherPlatform("IOS/X.cpp", PlatformType.Mac) && !SourceFilter.IsOtherPlatform("IOS/X.cpp", PlatformType.IOS), "IOS directory");
    Check(SourceFilter.IsCompiledFor("A.mm", PlatformType.IOS) && SourceFilter.IsCompiledFor("A.S", PlatformType.IOS), "iOS sources");
    Expect(() => SanitizerSupport.Validate(PlatformType.IOS, Sanitizer.Thread, "X"), "not supported");
    Check(SourceFilter.IsCompiledFor("A.S", PlatformType.Linux) && SourceFilter.IsCompiledFor("A.s", PlatformType.Mac) && !SourceFilter.IsCompiledFor("A.S", PlatformType.Windows), ".S/.s");
    Check(SourceFilter.IsCompiledFor("A.asm", PlatformType.Windows) && !SourceFilter.IsCompiledFor("A.asm", PlatformType.Linux), ".asm");
    Check(SourceFilter.IsCompiledFor("A.mm", PlatformType.Mac) && !SourceFilter.IsCompiledFor("A.m", PlatformType.Linux), ".m/.mm");
    Check(SanitizerSupport.Validate(PlatformType.Linux, Sanitizer.Thread, "X") == Sanitizer.Thread, "TSan on Linux");
    Expect(() => SanitizerSupport.Validate(PlatformType.Mac, Sanitizer.Address | Sanitizer.Thread, "X"), "cannot be combined");
    Expect(() => SanitizerSupport.Validate(PlatformType.Android, Sanitizer.Thread, "X"), "not supported");
    if (HostPlatform.IsLinux)
    {
        var linux = new LinuxPlatform(HostPlatform.Architecture);
        var linuxModel = ProjectModel.Load(project, linux, TargetConfiguration.Debug);
        var core = Names(linuxModel.Sources(linuxModel.Modules["Core"]));
        Check(core.Contains("Core/Linux/CoreLinux.cpp") && !core.Any(f => f.Contains("Android/") || f.Contains("Windows/")), "Linux sources: " + string.Join(", ", core));
        Check(linuxModel.ApiMacroValue(linuxModel.Targets.Single(t => t.Name == "Game"), linuxModel.Modules["Core"], PlatformType.Linux).Contains("visibility"), "Linux API macro");
        Check(linux.ExecutablePath("/b", "Game") == "/b/Game" && linux.LibraryPrefix == "lib" && linux.CanRunOnHost, "Linux file names");
    }
    if (HostPlatform.IsMacOS)
    {
        var mac = new MacPlatform(HostPlatform.Architecture);
        var macModel = ProjectModel.Load(project, mac, TargetConfiguration.Debug);
        var core = Names(macModel.Sources(macModel.Modules["Core"]));
        Check(!core.Any(f => f.Contains("Android/") || f.Contains("Windows/") || f.Contains("Linux/")), "macOS sources: " + string.Join(", ", core));
        Check(mac.SharedLibraryFileExtension == ".dylib" && mac.CanRunOnHost && Directory.Exists(mac.SdkRoot), "macOS platform");
        Check(MacPlatform.DsymPath(MacPlatform.BundleExecutable("/b", "App")) == "/b/App.app.dSYM", "bundle dSYM path");
    }
});

Test("C++20 module scanning and graph", () =>
{
    var info = CppModules.Parse("module;\n#include <x>\nexport module Math; // c\nexport import :Ops;\nimport Core.Text;\n/* import Hidden; */\nconst char* s = \"import Fake;\";\n", "Math.cppm");
    Check(info.FullName == "Math" && info.Exported && info.ProducesInterface, "interface declaration");
    Check(info.Imports.SequenceEqual(new[] { "Math:Ops", "Core.Text" }), "imports: " + string.Join(", ", info.Imports));
    var impl = CppModules.Parse("module Math;\nint f();", "Impl.cpp");
    Check(!impl.ProducesInterface && impl.Requires.SequenceEqual(new[] { "Math" }), "implementation unit");
    var partition = CppModules.Parse("module Math:Detail;", "Detail.cpp");
    Check(partition.FullName == "Math:Detail" && partition.ProducesInterface && !partition.Exported, "internal partition");
    Check(!CppModules.Parse("#include <vector>\nint important = 1; // module\n", "A.cpp").UsesModules, "false positive");
    Expect(() => CppModules.Parse("import <vector>;", "A.cpp"), "header units");
    Expect(() => CppModules.Parse("int x;", "Empty.ixx"), "must declare a module");
    Expect(() => CppModules.Parse("module Math:Detail;", "Detail.ixx"), "internal partitions and implementation units in .cpp");
    Expect(() => CppModules.Parse("module Math;", "Impl.cppm"), "is not an interface");

    string mods = Path.Combine(root, "Mods");
    Write(Path.Combine(mods, "Config", "BuildConfig.json"), "{}");
    Write(Path.Combine(mods, "Source", "Geo", "Geo.Build.cs"), Module("Geo"));
    Write(Path.Combine(mods, "Source", "Geo", "Geo.cppm"), "export module Geo;\nexport import :Shapes;\nimport :Detail;\nexport int Area();\n");
    Write(Path.Combine(mods, "Source", "Geo", "Shapes.cppm"), "export module Geo:Shapes;\n");
    Write(Path.Combine(mods, "Source", "Geo", "Detail.cpp"), "module Geo:Detail;\n");
    Write(Path.Combine(mods, "Source", "Geo", "Area.cpp"), "module Geo;\nint Area() { return 1; }\n");
    Write(Path.Combine(mods, "Source", "App", "App.Build.cs"), Module("App", "PrivateDependencies.Add(\"Geo\");"));
    Write(Path.Combine(mods, "Source", "App", "Main.cpp"), "import Geo;\nint main() { return Area(); }\n");
    Write(Path.Combine(mods, "Source", "Lib.Target.cs"), Target("Lib", "Type = TargetType.StaticLibrary; Modules.Add(\"Geo\");"));
    Write(Path.Combine(mods, "Source", "App.Target.cs"), Target("App", "Modules.Add(\"App\"); TargetDependencies.Add(\"Lib\");"));
    var modsModel = ProjectModel.Load(mods, arm64, TargetConfiguration.Debug);
    var app = modsModel.Targets.Single(t => t.Name == "App");
    var plan = CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug);
    var env = new CPPEnvironment();
    string main = Path.Combine(mods, "Source", "App", "Main.cpp");
    plan.Apply(modsModel.Modules["App"], env, [main]);
    var unit = env.ModuleUnits[main];
    Check(unit.InterfaceOutput == null && unit.References.Select(r => r.Name).Order().SequenceEqual(new[] { "Geo", "Geo:Detail", "Geo:Shapes" }), "transitive references: " + string.Join(", ", unit.References.Select(r => r.Name)));
    Check(unit.References.All(r => r.Path.Contains(Path.Combine("Lib", "Modules")) && r.Path.EndsWith(".pcm")), "BMIs come from the dependency target");
    Check(env.SourcePrerequisites[main].Count == 3, "BMIs are prerequisites");
    var libPlan = CppModulePlan.Create(modsModel, modsModel.Targets.Single(t => t.Name == "Lib"), arm64, TargetConfiguration.Debug);
    var libEnv = new CPPEnvironment();
    var geoSources = modsModel.Sources(modsModel.Modules["Geo"]);
    libPlan.Apply(modsModel.Modules["Geo"], libEnv, geoSources);
    Check(libEnv.ModuleUnits.Count == 4 && libEnv.ModuleUnits.Values.Count(u => u.InterfaceOutput != null) == 3, "Lib module units");
    Check(libEnv.ModuleUnits.Values.Single(u => u.Info.FullName == "Geo" && u.InterfaceOutput != null).References.Count == 2, "interface references its partitions");
    // Not visible without a module dependency; unknown modules and import std are reported.
    File.WriteAllText(Path.Combine(mods, "Source", "App", "App.Build.cs"), Module("App"));
    modsModel = ProjectModel.Load(mods, arm64, TargetConfiguration.Debug);
    app = modsModel.Targets.Single(t => t.Name == "App");
    Expect(() => CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug).Apply(modsModel.Modules["App"], new CPPEnvironment(), [main]), "PrivateDependencies.Add(\"Geo\")");
    File.WriteAllText(main, "import Missing;\n");
    Expect(() => CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug).Apply(modsModel.Modules["App"], new CPPEnvironment(), [main]), "was not found");
    File.WriteAllText(main, "import std;\n");
    Expect(() => CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug).Apply(modsModel.Modules["App"], new CPPEnvironment(), [main]), "import std");
    Write(Path.Combine(mods, "Source", "Geo", "Twin.cppm"), "export module Geo;\n");
    Expect(() => CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug), "declared twice");
    File.Delete(Path.Combine(mods, "Source", "Geo", "Twin.cppm"));

    // import std: the fake NDK ships share/libc++/v1/std.cppm; the deepest importing target builds it.
    string libcxx = Path.Combine(Path.GetDirectoryName(bin)!, "share", "libc++", "v1");
    Write(Path.Combine(libcxx, "std.cppm"), "export module std;\n");
    Write(Path.Combine(libcxx, "std.compat.cppm"), "export module std.compat;\nexport import std;\n");
    File.WriteAllText(Path.Combine(mods, "Source", "App", "App.Build.cs"), Module("App", "PrivateDependencies.Add(\"Geo\");"));
    File.WriteAllText(main, "import std.compat;\nimport Geo;\nint main() { return Area(); }\n");
    File.WriteAllText(Path.Combine(mods, "Source", "Geo", "Area.cpp"), "module Geo;\nimport std;\nint Area() { return 1; }\n");
    modsModel = ProjectModel.Load(mods, arm64, TargetConfiguration.Debug);
    app = modsModel.Targets.Single(t => t.Name == "App");
    var stdEnv = new CPPEnvironment();
    CppModulePlan.Create(modsModel, app, arm64, TargetConfiguration.Debug).Apply(modsModel.Modules["App"], stdEnv, [main]);
    var stdRefs = stdEnv.ModuleUnits[main].References;
    Check(stdRefs.Any(r => r.Name == "std.compat") && stdRefs.Any(r => r.Name == "std"), "std/std.compat references: " + string.Join(", ", stdRefs.Select(r => r.Name)));
    Check(stdRefs.Single(r => r.Name == "std").Path.Contains(Path.Combine("Lib", "Modules")), "std BMI comes from the deepest importing target (Lib)");
    Check(stdRefs.Single(r => r.Name == "std.compat").Path.Contains(Path.Combine("App", "Modules")), "std.compat is built by App, the only importer");
    // Visual Studio: interfaces and internal partitions are marked for IntelliSense.
    SolutionGenerator.Generate(mods, "Mods", arm64, x64, arm64);
    string libProject = File.ReadAllText(Path.Combine(mods, "Lib.vcxproj"));
    Check(libProject.Contains("<CompileAs>CompileAsCppModule</CompileAs>") && libProject.Contains("<CompileAs>CompileAsCppModuleInternalPartition</CompileAs>"), "IntelliSense CompileAs");
});

Test("CMake and pkg-config export", () =>
{
    var windows = new ExportInfo("Engine", TargetType.SharedLibrary, PlatformType.Windows, "1.2.0", "20",
        ["CORE_API=__declspec(dllimport)", "NAME=\"a;b\""], [], []);
    string config = PackageExport.CMakeConfig(windows, "Engine.dll", "Engine.lib");
    Check(config.Contains("IMPORTED_LOCATION \"${_beast_prefix}/bin/Engine.dll\"") && config.Contains("IMPORTED_IMPLIB \"${_beast_prefix}/lib/Engine.lib\""), "Windows DLL locations");
    Check(config.Contains("CORE_API=__declspec(dllimport);NAME=\\\"a\\;b\\\""), "definition escaping: " + config);
    var android = new ExportInfo("Tools", TargetType.StaticLibrary, PlatformType.Android, "", "17", [], ["log", "m"], ["Engine"]);
    string androidConfig = PackageExport.CMakeConfig(android, "libTools.a", null);
    Check(androidConfig.Contains("lib/${CMAKE_ANDROID_ARCH_ABI}/libTools.a") && androidConfig.Contains("find_dependency(Engine CONFIG)") &&
        androidConfig.Contains("INTERFACE_LINK_LIBRARIES \"Engine::Engine;log;m\"") && androidConfig.Contains("cxx_std_17"), "Android static config: " + androidConfig);
    string pc = PackageExport.PkgConfig(android, "libTools.a");
    Check(pc.Contains("abi=arm64-v8a") && pc.Contains("Requires: Engine") && pc.Contains("Libs: -L${libdir} -lTools -llog -lm"), "Android pkg-config: " + pc);
    string version = PackageExport.CMakeVersion("1.2.0");
    Check(version.Contains("set(PACKAGE_VERSION \"1.2.0\")"), "version file");
});

Test("locks", () =>
{
    using (var first = ProjectBuildLock.AcquireTargetAsync(project, "Game").GetAwaiter().GetResult())
    {
        var other = ProjectBuildLock.AcquireTargetAsync(project, "Engine");
        Check(other.Wait(TimeSpan.FromSeconds(5)), "Different targets block each other");
        other.Result.Dispose();
        var same = ProjectBuildLock.AcquireTargetAsync(project, "Game");
        Check(!same.Wait(TimeSpan.FromMilliseconds(700)), "Same target lock acquired twice");
        first.Dispose();
        Check(same.Wait(TimeSpan.FromSeconds(10)), "Released target lock not acquired");
        same.Result.Dispose();
    }
    var shared1 = ProjectBuildLock.AcquireBuildAsync(project, false).GetAwaiter().GetResult();
    var shared2 = ProjectBuildLock.AcquireBuildAsync(project, false);
    Check(shared2.Wait(TimeSpan.FromSeconds(5)), "Shared build locks exclude each other");
    var exclusive = ProjectBuildLock.AcquireBuildAsync(project, true);
    Check(!exclusive.Wait(TimeSpan.FromMilliseconds(700)), "Exclusive lock (clean) acquired during a build");
    shared1.Dispose();
    shared2.Result.Dispose();
    Check(exclusive.Wait(TimeSpan.FromSeconds(10)), "Exclusive lock not acquired after builds");
    exclusive.Result.Dispose();
});

Console.WriteLine(failures == 0 ? $"PASS: model tests ({root})" : $"{failures} model test(s) failed ({root})");
return failures == 0 ? 0 : 1;

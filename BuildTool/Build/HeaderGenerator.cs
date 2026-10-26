using System.Text;

namespace Beast.BuildTool;

public static class HeaderGenerator
{
    public static string GenerateTargetRules(string projectName) => $$"""
        using Beast.BuildTool;

        public sealed class {{SanitizeIdentifier(projectName)}}Target : TargetRules
        {
            public {{SanitizeIdentifier(projectName)}}Target(ModulesInfo info)
            {
                Type = TargetType.Application;
                Modules.Add("Launch");
                // macOS: a windowed AppKit application (an .app bundle); iOS: a UIKit app launched like
                // one (not spawned as a console program); a console program elsewhere.
                ConsoleApplication = info.Platform is not (PlatformType.Mac or PlatformType.IOS);
            }
        }
        """;

    public static string SanitizeIdentifier(string name)
    {
        var result = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrEmpty(result) || char.IsDigit(result[0]) ? "Project_" + result : result;
    }

    public static string GenerateLaunchBuildCs(string projectName) => $$"""
        using Beast.BuildTool;

        // The Launch module is the executable entry point. Add include paths, defines and libraries here.
        public sealed class Launch : ModuleRules
        {
            public Launch(ModulesInfo info)
            {
                Definitions.Add("{{SanitizeIdentifier(projectName).ToUpperInvariant()}}_BUILD");
                // Mac/LaunchMac.mm and IOS/LaunchIOS.mm are compiled only for their platform.
                if (info.Platform == PlatformType.Mac) Frameworks.Add("Cocoa");
                if (info.Platform == PlatformType.IOS) { Frameworks.Add("UIKit"); Frameworks.Add("Foundation"); }
            }
        }
        """;

    public static string GenerateLaunchSource(string projectName, string template = "Launch.cpp") =>
        File.ReadAllText(Path.Combine(GlobalEnvironment.TemplatesPath, template))
            .Replace("{{PROJECT_NAME}}", SanitizeIdentifier(projectName));

    public static string GenerateModuleBuildCs(string moduleName) => $$"""
        using System.IO;
        using Beast.BuildTool;

        public sealed class {{SanitizeIdentifier(moduleName)}} : ModuleRules
        {
            public {{SanitizeIdentifier(moduleName)}}(ModulesInfo info)
            {
                PrivateIncludePaths.Add(Path.Combine(info.ProjectSourceDirectory!, "{{moduleName}}"));
            }
        }
        """;

    public static string GenerateModuleSource(string moduleName) => $$"""
        // {{moduleName}} module implementation.
        void {{SanitizeIdentifier(moduleName)}}_Initialize()
        {
        }
        """;
}

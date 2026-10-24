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
            }
        }
        """;

    public static string GenerateLaunchSource(string projectName) =>
        File.ReadAllText(Path.Combine(GlobalEnvironment.TemplatesPath, "Launch.cpp"))
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

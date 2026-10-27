using Beast.BuildTool;

public sealed class Core : ModuleRules
{
    public Core(ModulesInfo info)
    {
        PublicIncludePaths.Add("Public");
        // Inside an external project's rules, info describes that project.
        PublicDefinitions.Add("SDK_IS_EXTERNAL=" + (info.ExternalProjectName == "SDK" ? "1" : "0"));
    }
}

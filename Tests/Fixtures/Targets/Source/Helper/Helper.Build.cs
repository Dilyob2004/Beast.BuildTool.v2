using Beast.BuildTool;
public sealed class Helper : ModuleRules
{
    public Helper(ModulesInfo info) { Type = ModuleType.HeaderOnly; PublicIncludePaths.Add("Public"); PublicDefinitions.Add("HELPER_PUBLIC=1"); }
}

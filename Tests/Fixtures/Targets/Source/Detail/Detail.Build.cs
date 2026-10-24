using Beast.BuildTool;
public sealed class Detail : ModuleRules
{
    public Detail(ModulesInfo info) { PublicIncludePaths.Add("Public"); PublicDefinitions.Add("DETAIL_PUBLIC=1"); Definitions.Add("DETAIL_PRIVATE=1"); }
}

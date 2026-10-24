using Beast.BuildTool;
public sealed class Unused : ModuleRules
{
    public Unused(ModulesInfo info) { PublicDefinitions.Add("UNUSED_DEFINE=1"); }
}

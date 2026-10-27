using Beast.BuildTool;

public sealed class MainAppB : ModuleRules
{
    public MainAppB(ModulesInfo info)
    {
        PrivateDependencies.Add("Widgets");
    }
}

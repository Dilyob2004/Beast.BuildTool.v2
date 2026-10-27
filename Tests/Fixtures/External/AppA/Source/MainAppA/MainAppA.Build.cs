using Beast.BuildTool;

public sealed class MainAppA : ModuleRules
{
    public MainAppA(ModulesInfo info)
    {
        PrivateDependencies.Add("Widgets");
    }
}

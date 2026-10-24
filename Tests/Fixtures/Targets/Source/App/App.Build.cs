using Beast.BuildTool;
public sealed class App : ModuleRules
{
    public App(ModulesInfo info) { PrivateDependencies.Add("Math"); }
}

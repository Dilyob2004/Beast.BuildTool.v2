using Beast.BuildTool;

public sealed class {{ProjectIdentifier}}App : ModuleRules
{
    public {{ProjectIdentifier}}App(ModulesInfo info)
    {
        PrivateDependencies.Add("Widgets");
        Resources.Add("Assets");
    }
}

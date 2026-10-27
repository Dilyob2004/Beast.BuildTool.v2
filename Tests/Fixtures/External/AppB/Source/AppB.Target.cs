using Beast.BuildTool;

public sealed class AppBTarget : TargetRules
{
    public AppBTarget(ModulesInfo info)
    {
        Modules.Add("MainAppB");
        TargetDependencies.Add("SDK");
    }
}

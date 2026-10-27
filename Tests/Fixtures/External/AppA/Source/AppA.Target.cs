using Beast.BuildTool;

public sealed class AppATarget : TargetRules
{
    public AppATarget(ModulesInfo info)
    {
        Modules.Add("MainAppA");
        TargetDependencies.Add("SDK");
    }
}

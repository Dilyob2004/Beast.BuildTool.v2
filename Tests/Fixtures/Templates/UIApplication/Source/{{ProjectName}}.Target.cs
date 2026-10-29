using Beast.BuildTool;

// Created from the UIApplication template (Tests/Fixtures/Templates).
public sealed class {{ProjectIdentifier}}Target : TargetRules
{
    public {{ProjectIdentifier}}Target(ModulesInfo info)
    {
        Modules.Add("{{ProjectIdentifier}}App");
        TargetDependencies.Add("SDK");
    }
}

using Beast.BuildTool;
public sealed class Math : ModuleRules
{
    public Math(ModulesInfo info)
    {
        PublicIncludePaths.Add("Public");
        PublicDefinitions.Add("MATH_PUBLIC=1");
        Definitions.Add("MATH_PRIVATE=1");
        PublicDependencies.Add("Helper");
        PrivateDependencies.Add("Detail");
    }
}

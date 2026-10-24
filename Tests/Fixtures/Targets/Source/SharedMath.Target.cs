using Beast.BuildTool;
public sealed class SharedMathTarget : TargetRules
{
    public SharedMathTarget(ModulesInfo info) { Type = TargetType.SharedLibrary; Modules.Add("Math"); }
}

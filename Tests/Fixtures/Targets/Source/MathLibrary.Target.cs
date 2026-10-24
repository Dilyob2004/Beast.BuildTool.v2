using Beast.BuildTool;
public sealed class MathLibraryTarget : TargetRules
{
    public MathLibraryTarget(ModulesInfo info) { Type = TargetType.StaticLibrary; Modules.Add("Math"); }
}

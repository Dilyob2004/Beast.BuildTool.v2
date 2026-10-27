using Beast.BuildTool;

// The library applications link: Widgets and, through it, Core.
public sealed class SDKTarget : TargetRules
{
    public SDKTarget(ModulesInfo info)
    {
        Type = TargetType.StaticLibrary;
        Modules.Add("Widgets");
        if (info.Platform == PlatformType.Android) Modules.Add("AndroidGlue");
    }
}

using System.IO;
using Beast.BuildTool;

// android_native_app_glue.c from the NDK, compiled with its own definitions: a separate module
// holds the definition that only this file needs (per-file definitions are not required).
public sealed class AndroidGlue : ModuleRules
{
    public AndroidGlue(ModulesInfo info)
    {
        if (info.AndroidNdkRoot == null) return;
        string glue = Path.Combine(info.AndroidNdkRoot, "sources", "android", "native_app_glue");
        PublicIncludePaths.Add(glue);
        AdditionalSources.Add(Path.Combine(glue, "android_native_app_glue.c"));
        Definitions.Add("ANativeActivity_onCreate=BeastNativeActivityOnCreate");
        InputLibraries.Add("android");
        InputLibraries.Add("log");
    }
}

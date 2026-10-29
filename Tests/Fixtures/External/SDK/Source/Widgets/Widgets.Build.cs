using Beast.BuildTool;

public sealed class Widgets : ModuleRules
{
    public Widgets(ModulesInfo info)
    {
        PublicIncludePaths.Add("Public");
        PublicDependencies.Add("Core");
        // {ProjectDir} is the SDK's root, not the application's.
        Resources.Add("{ProjectDir}/Data", "Assets/SDK");
        if (info.Platform == PlatformType.Android)
        {
            AndroidAssets.Add("Android/widgets.txt", "SDK/widgets.txt");
            AndroidJavaSources.Add("Android/Java");          // a source root: com/beast/sdk/...
            AndroidJavaSources.Add("Android/BeastText.java"); // package com.beast.sdk.text
        }
    }
}

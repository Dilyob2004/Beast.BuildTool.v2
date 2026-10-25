namespace Beast.BuildTool;

/// <summary>Turns --platform/--arch (or Config/BuildConfig.json defaults) into platform instances.</summary>
public static class PlatformFactory
{
    /// <summary>
    /// Windows: x64 (default) or ARM64. Android: one platform per ABI; without --arch the ABIs come
    /// from android.abis, so that every listed ABI is packaged into the APK.
    /// </summary>
    public static IReadOnlyList<Platform> Create(string? platform, string? architecture)
    {
        string value = (platform ?? DefaultPlatform()).ToLowerInvariant();
        string? arch = architecture?.ToLowerInvariant();
        switch (value)
        {
            case "windows" or "win64" or "win":
                return [new WindowsPlatform(ParseWindowsArch(arch))];
            case "windowsarm64" or "win-arm64" or "windows-arm64":
                return [new WindowsPlatform(PlatformArch.ARM64)];
            case "android":
                var abis = arch != null ? [arch] : BuildConfigManager.Config.Android.Abis;
                if (abis.Count == 0) throw new InvalidOperationException("android.abis is empty. List at least one ABI: arm64-v8a or x86_64.");
                return abis.Select(AndroidPlatform.NormalizeAbi).Distinct().Select(a => (Platform)new AndroidPlatform(a)).ToList();
            case "android-x86_64":
                return [new AndroidPlatform("x86_64")];
            case "linux":
                return [new LinuxPlatform(ParseUnixArch(arch, "Linux"))];
            case "linuxarm64" or "linux-arm64":
                return [new LinuxPlatform(PlatformArch.ARM64)];
            case "ios":
                return [new IosPlatform(simulator: false)];
            case "iossimulator" or "ios-simulator" or "simulator":
                return [new IosPlatform(simulator: true, ParseUnixArch(arch, "iOS Simulator"))];
            case "mac" or "macos" or "osx" or "darwin":
                return [new MacPlatform(ParseUnixArch(arch, "macOS"))];
            default:
                throw new ArgumentException($"Unsupported platform '{platform}'. Use Windows (--arch x64|arm64), Linux (--arch x64|arm64), Mac (--arch arm64|x64), IOS, IOSSimulator (--arch arm64|x64) or Android (--arch arm64-v8a|x86_64).");
        }
    }

    private static PlatformArch ParseUnixArch(string? arch, string platform) => arch switch
    {
        null => HostPlatform.Architecture,
        "x64" or "x86_64" or "amd64" => PlatformArch.X64,
        "arm64" or "aarch64" => PlatformArch.ARM64,
        _ => throw new ArgumentException($"Unsupported {platform} architecture '{arch}'. Use x64 or arm64.")
    };

    public static Platform CreateSingle(string? platform, string? architecture) => Create(platform, architecture)[0];

    private static PlatformArch ParseWindowsArch(string? arch) => arch switch
    {
        null or "x64" or "x86_64" or "amd64" => PlatformArch.X64,
        "arm64" or "aarch64" => PlatformArch.ARM64,
        _ => throw new ArgumentException($"Unsupported Windows architecture '{arch}'. Use x64 or arm64.")
    };

    /// <summary>The native platform of the machine running Beast: Windows, Linux or Mac.</summary>
    public static string HostDefault => HostPlatform.IsWindows ? "Windows" : HostPlatform.IsMacOS ? "Mac" : "Linux";

    /// <summary>
    /// Config default. "Host" (or empty) is the machine's own platform; on hosts without MSVC a
    /// "Windows" default falls back to the host platform.
    /// </summary>
    public static string DefaultPlatform()
    {
        string configured = BuildConfigManager.Config.DefaultPlatform;
        if (string.IsNullOrWhiteSpace(configured) || configured.Equals("host", StringComparison.OrdinalIgnoreCase)) return HostDefault;
        if (!HostPlatform.IsWindows && configured.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            Log.TraceInfo($"No MSVC on this host: building for {HostDefault}. Pass --platform to choose explicitly.");
            return HostDefault;
        }
        return configured;
    }
}

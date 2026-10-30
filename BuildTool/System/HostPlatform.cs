using System.Runtime.InteropServices;

namespace Beast.BuildTool;

/// <summary>
/// Facts about the machine running Beast. Windows targets need a Windows host (MSVC); Android
/// targets build on Windows, Linux and macOS hosts with the matching NDK/SDK installation.
/// </summary>
public static class HostPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static PlatformArch Architecture => IsArm64 ? PlatformArch.ARM64 : PlatformArch.X64;
    public static bool IsArm64 => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    /// <summary>NDK prebuilt toolchain directory name. NDK ships x86_64 (universal on macOS) host tools.</summary>
    public static string NdkHostTag => IsWindows ? "windows-x86_64" : IsMacOS ? "darwin-x86_64" : "linux-x86_64";

    /// <summary>Executable file name on this host: "clang++" becomes "clang++.exe" on Windows.</summary>
    public static string Executable(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>Shell wrapper script name: "gradlew" becomes "gradlew.bat" on Windows.</summary>
    public static string Script(string name) => IsWindows ? name + ".bat" : name;

    /// <summary>Makes a script runnable on Unix hosts (templates copied from Windows lose the mode bit).</summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | execute);
    }

    /// <summary>The Android SDK in use: the first existing of android.sdkRoot, ANDROID_HOME, ANDROID_SDK_ROOT, the standard location (see AndroidToolchain).</summary>
    public static string DefaultAndroidSdk() => AndroidToolchain.SdkRootOrDefault();

    /// <summary>The JDK in use (see AndroidToolchain), or "" when none is found.</summary>
    public static string DefaultJavaHome() => AndroidToolchain.FindJavaHome(out _) ?? "";
}

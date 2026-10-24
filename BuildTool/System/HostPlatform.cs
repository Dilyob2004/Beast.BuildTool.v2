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
        if (IsWindows || !File.Exists(path)) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | execute);
    }

    /// <summary>Default Android SDK location: ANDROID_HOME/ANDROID_SDK_ROOT, then the Android Studio default.</summary>
    public static string DefaultAndroidSdk()
    {
        foreach (string variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) return value;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsWindows) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        return IsMacOS ? Path.Combine(home, "Library", "Android", "sdk") : Path.Combine(home, "Android", "Sdk");
    }

    /// <summary>Default JDK: JAVA_HOME, then the JetBrains runtime bundled with Android Studio.</summary>
    public static string DefaultJavaHome()
    {
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } value) return value;
        if (IsWindows) return @"C:\Program Files\Android\Android Studio\jbr";
        if (IsMacOS) return "/Applications/Android Studio.app/Contents/jbr/Contents/Home";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string candidate in new[] { "/opt/android-studio/jbr", Path.Combine(home, "android-studio", "jbr"), "/usr/local/android-studio/jbr" })
            if (Directory.Exists(candidate)) return candidate;
        return "";
    }
}

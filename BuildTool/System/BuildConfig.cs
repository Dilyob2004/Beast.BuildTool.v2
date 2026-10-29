using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beast.BuildTool;

/// <summary>Per-project toolchain settings stored in Config/BuildConfig.json.</summary>
public sealed class BuildConfig
{
    [JsonPropertyName("defaultConfiguration")]
    public string DefaultConfiguration { get; set; } = "Debug";

    /// <summary>
    /// Other Beast projects whose targets and modules this project uses: their Source/*.Target.cs
    /// targets can be TargetDependencies, their modules Public/PrivateDependencies. Paths are relative
    /// to this project's root or absolute; %VAR% / $VAR are expanded.
    /// </summary>
    [JsonPropertyName("externalProjects")]
    public List<ExternalProjectConfig> ExternalProjects { get; set; } = [];

    [JsonPropertyName("defaultPlatform")]
    public string DefaultPlatform { get; set; } = "Windows";

    [JsonPropertyName("maxParallelJobs")]
    public int MaxParallelJobs { get; set; }

    /// <summary>Stop the whole build at the first failed action (default: keep building independent actions).</summary>
    [JsonPropertyName("stopOnFirstError")]
    public bool StopOnFirstError { get; set; }
    /// <summary>Write compile_commands.json for clangd/VS Code/CLion after each build's graph is planned.</summary>
    [JsonPropertyName("generateCompileCommands")]
    public bool GenerateCompileCommands { get; set; } = true;
    /// <summary>Per-test timeout of the test command.</summary>
    [JsonPropertyName("testTimeoutSeconds")]
    public int TestTimeoutSeconds { get; set; } = 600;
    [JsonPropertyName("vcpkg")]
    public VcpkgConfig Vcpkg { get; set; } = new();
    [JsonPropertyName("conan")]
    public ConanConfig Conan { get; set; } = new();
    [JsonPropertyName("acceleration")]
    public AccelerationConfig Acceleration { get; set; } = new();

    [JsonPropertyName("android")]
    public AndroidBuildConfig Android { get; set; } = new();

    [JsonPropertyName("windows")]
    public WindowsBuildConfig Windows { get; set; } = new();

    [JsonPropertyName("linux")]
    public LinuxBuildConfig Linux { get; set; } = new();

    [JsonPropertyName("mac")]
    public MacBuildConfig Mac { get; set; } = new();

    [JsonPropertyName("ios")]
    public IosBuildConfig Ios { get; set; } = new();
}

/// <summary>An external project: a name (used in messages and Intermediate/.../External/&lt;name&gt;) and its root.</summary>
public sealed class ExternalProjectConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

/// <summary>iOS devices and the iOS Simulator (Xcode).</summary>
public sealed class IosBuildConfig
{
    /// <summary>Minimum iOS version (MinimumOSVersion, --target=...-apple-ios&lt;version&gt;).</summary>
    [JsonPropertyName("deploymentTarget")] public string DeploymentTarget { get; set; } = "15.0";
    /// <summary>CFBundleIdentifier of applications; empty: com.beast.&lt;target&gt;.</summary>
    [JsonPropertyName("bundleIdentifier")] public string BundleIdentifier { get; set; } = "";
    /// <summary>Device builds: signing identity, e.g. "Apple Development: Name (TEAMID)" (security find-identity -v -p codesigning).</summary>
    [JsonPropertyName("signingIdentity")] public string SigningIdentity { get; set; } = "";
    /// <summary>Device builds: .mobileprovision file for the bundle identifier (its entitlements are used).</summary>
    [JsonPropertyName("provisioningProfile")] public string ProvisioningProfile { get; set; } = "";
    /// <summary>Simulator for run/test (name such as "iPhone 16" or a UDID); empty: a booted one or the first iPhone.</summary>
    [JsonPropertyName("simulator")] public string Simulator { get; set; } = "";
    /// <summary>Device for run (name or identifier from xcrun devicectl list devices); --device overrides.</summary>
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    /// <summary>Explicit C++ driver (another Clang, e.g. for C++20 modules); empty: Apple clang.</summary>
    [JsonPropertyName("cxx")] public string Cxx { get; set; } = "";
}

/// <summary>Native Linux targets (Clang or GCC).</summary>
public sealed class LinuxBuildConfig
{
    /// <summary>"clang", "gcc" or empty: Clang when it is on PATH, otherwise GCC.</summary>
    [JsonPropertyName("compiler")] public string Compiler { get; set; } = "";
    /// <summary>Explicit C++ driver (e.g. /usr/bin/clang++-18 or aarch64-linux-gnu-g++); overrides compiler.</summary>
    [JsonPropertyName("cxx")] public string Cxx { get; set; } = "";
    /// <summary>Explicit archiver; empty: llvm-ar (Clang) or gcc-ar (GCC), else ar.</summary>
    [JsonPropertyName("ar")] public string Ar { get; set; } = "";
    /// <summary>Linker for -fuse-ld (lld, gold, mold); empty: the compiler default, lld for Clang LTO.</summary>
    [JsonPropertyName("linker")] public string Linker { get; set; } = "";
    /// <summary>Sysroot for cross compilation (another architecture or a non-Linux host; Clang).</summary>
    [JsonPropertyName("sysroot")] public string Sysroot { get; set; } = "";
    /// <summary>"libc++" (Clang: -stdlib=libc++, needed for import std) or empty: the compiler default (libstdc++).</summary>
    [JsonPropertyName("stdlib")] public string Stdlib { get; set; } = "";
}

/// <summary>Native macOS targets (Apple clang from Xcode or the Command Line Tools).</summary>
public sealed class MacBuildConfig
{
    /// <summary>Minimum macOS version (-mmacosx-version-min).</summary>
    [JsonPropertyName("deploymentTarget")] public string DeploymentTarget { get; set; } = "11.0";
    /// <summary>Explicit SDK path; empty: xcrun --sdk macosx --show-sdk-path.</summary>
    [JsonPropertyName("sdkRoot")] public string SdkRoot { get; set; } = "";
    /// <summary>Explicit C++ driver, e.g. Homebrew LLVM's clang++ (needed for C++20 modules); empty: Apple clang.</summary>
    [JsonPropertyName("cxx")] public string Cxx { get; set; } = "";
    /// <summary>Bundle identifier of .app bundles; empty: com.beast.&lt;target&gt;.</summary>
    [JsonPropertyName("bundleIdentifier")] public string BundleIdentifier { get; set; } = "";
    /// <summary>Keychain profile for notarization (xcrun notarytool store-credentials &lt;profile&gt;).</summary>
    [JsonPropertyName("notaryProfile")] public string NotaryProfile { get; set; } = "";
    /// <summary>Code signing identity for codesign; empty: ad-hoc ("-") signature.</summary>
    [JsonPropertyName("signingIdentity")] public string SigningIdentity { get; set; } = "";
}

/// <summary>vcpkg manifest mode: used when vcpkg.json exists in the project root.</summary>
public sealed class VcpkgConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>vcpkg installation; empty uses VCPKG_ROOT, then the copy bundled with Visual Studio.</summary>
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    /// <summary>Empty: x64-windows / arm64-windows (+ "-static" with StaticRuntime), arm64-android / x64-android.</summary>
    [JsonPropertyName("triplet")] public string Triplet { get; set; } = "";
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = [];
}

/// <summary>Conan 2: used when conanfile.txt or conanfile.py exists in the project root.</summary>
public sealed class ConanConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Conan executable; empty uses "conan" from PATH.</summary>
    [JsonPropertyName("executable")] public string Executable { get; set; } = "";
    /// <summary>Host profile; empty: the default profile (Windows) or one generated from the NDK settings (Android).</summary>
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = [];
}

public sealed class AccelerationConfig
{
    public bool CacheEnabled { get; set; } = true;
    public string CacheDirectory { get; set; } = "Intermediate/Cache";
    public string SharedCacheDirectory { get; set; } = "";
    public bool PortableClangCache { get; set; }
    public int LocalCacheLimitMB { get; set; } = 4096;
    public int MemoryReserveMB { get; set; } = 1024;
    public int CompileMemoryMB { get; set; } = 512;
    public int LinkMemoryMB { get; set; } = 1024;
    public int MaxMemoryMB { get; set; } = 0;
    public string WorkerQueueDirectory { get; set; } = "";
    public string WorkerKeyEnvironment { get; set; } = "BEAST_WORKER_KEY";
    public int WorkerTimeoutSeconds { get; set; } = 120;
    public int WorkerRetryCount { get; set; } = 1;
}

public sealed class WindowsBuildConfig
{
    /// <summary>Embed the per-monitor DPI-aware Windows manifest in the executable.</summary>
    [JsonPropertyName("dpiAware")]
    public bool DpiAware { get; set; } = true;

    /// <summary>Exact Windows SDK version (e.g. "10.0.26100.0"); empty selects the newest installed one.</summary>
    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; set; } = "";

    /// <summary>"msvc" (cl.exe, default) or "clang-cl" (the LLVM bundled with Visual Studio, or llvmRoot).</summary>
    [JsonPropertyName("compiler")]
    public string Compiler { get; set; } = "msvc";

    /// <summary>LLVM installation containing bin/clang-cl.exe; empty uses Visual Studio's VC/Tools/Llvm.</summary>
    [JsonPropertyName("llvmRoot")]
    public string LlvmRoot { get; set; } = "";
}

public sealed class AndroidBuildConfig
{
    [JsonPropertyName("releaseSigning")]
    public AndroidSigningConfig ReleaseSigning { get; set; } = new();

    [JsonPropertyName("javaHome")]
    public string JavaHome { get; set; } = HostPlatform.DefaultJavaHome();

    [JsonPropertyName("sdkRoot")]
    public string SdkRoot { get; set; } = HostPlatform.DefaultAndroidSdk();

    /// <summary>Either an NDK directory or its parent directory containing versions.</summary>
    [JsonPropertyName("ndkRoot")]
    public string NdkRoot { get; set; } = Path.Combine(HostPlatform.DefaultAndroidSdk(), "ndk");
    /// <summary>ABIs built and packaged into one APK: "arm64-v8a" and/or "x86_64" (emulators, Chromebooks).</summary>
    [JsonPropertyName("abis")]
    public List<string> Abis { get; set; } = ["arm64-v8a"];
    /// <summary>Application ID (package name on the device). Empty: the generated Gradle value (com.beast.name).</summary>
    [JsonPropertyName("applicationId")]
    public string ApplicationId { get; set; } = "";
    /// <summary>versionCode; 0 keeps the Gradle value.</summary>
    [JsonPropertyName("versionCode")]
    public int VersionCode { get; set; }
    /// <summary>versionName; empty keeps the Gradle value.</summary>
    [JsonPropertyName("versionName")]
    public string VersionName { get; set; } = "";
    /// <summary>compileSdk; 0 uses targetSdk.</summary>
    [JsonPropertyName("compileSdk")]
    public int CompileSdk { get; set; }

    [JsonPropertyName("ndkVersion")]
    public string NdkVersion { get; set; } = "";

    [JsonPropertyName("minSdk")]
    public int MinSdk { get; set; } = 29;

    [JsonPropertyName("targetSdk")]
    public int TargetSdk { get; set; } = 35;

    /// <summary>AVD name used when no Android emulator is already running.</summary>
    [JsonPropertyName("defaultAvd")]
    public string DefaultAvd { get; set; } = "";

    [JsonPropertyName("emulatorBootTimeoutSeconds")]
    public int EmulatorBootTimeoutSeconds { get; set; } = 180;
}

public sealed class AndroidSigningConfig
{
    /// <summary>Empty: preserve Gradle signing, or use the local Android debug key.</summary>
    [JsonPropertyName("keyStore")]
    public string KeyStore { get; set; } = "";
    [JsonPropertyName("keyAlias")]
    public string KeyAlias { get; set; } = "";
    [JsonPropertyName("storePasswordEnv")]
    public string StorePasswordEnv { get; set; } = "";
    [JsonPropertyName("keyPasswordEnv")]
    public string KeyPasswordEnv { get; set; } = "";
}

public static class BuildConfigManager
{
    private static BuildConfig _config = new();
    public static BuildConfig Config => _config;

    public static void LoadConfig(string projectPath)
    {
        string configPath = Path.Combine(projectPath, "Config", "BuildConfig.json");
        if (!File.Exists(configPath))
        {
            _config = new BuildConfig();
            return;
        }

        try
        {
            _config = JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(configPath), JsonOptions) ?? new BuildConfig();
        }
        catch (Exception ex)
        {
            // Falling back here can silently replace a configured release
            // signing identity with the default debug key.
            throw new InvalidOperationException($"Cannot read '{configPath}'. Fix the build configuration before building.", ex);
        }
    }

    public static void CreateDefault(string projectPath)
    {
        string configPath = Path.Combine(projectPath, "Config", "BuildConfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new BuildConfig(), JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

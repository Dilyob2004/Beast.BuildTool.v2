using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Beast.BuildTool
{


    public enum ModuleType
    {
        Source,
        External,
        HeaderOnly
    }
    public class ModulesInfo
    {
        public readonly TargetConfiguration Configuration;
        public readonly PlatformType Platform;
        public readonly PlatformArch Architecture;
        public string ConfigurationName
        {
            get
            {
                switch (Configuration)
                {
                    case TargetConfiguration.Debug: return "Debug";
                    case TargetConfiguration.Development: return "Development";
                    case TargetConfiguration.Release: return "Release";
                    default: return "Development";
                }
            }
        }
        public string ThirdPartyDirectory
        {
            get
            {
                return GlobalEnvironment.ThirdPartyPath;
            }
        }
        private readonly string? _projectDirectory;
        public string SourceDirectory => _projectDirectory != null ? System.IO.Path.Combine(_projectDirectory, "Source") : GlobalEnvironment.SourcePath;
        /// <summary>Root of the project whose rules are evaluated (an external project's root inside its own rules).</summary>
        public string? ProjectDirectory => _projectDirectory ?? GlobalEnvironment.ProjectPath;
        public string? ProjectSourceDirectory => ProjectDirectory != null ? System.IO.Path.Combine(ProjectDirectory, "Source") : null;
        /// <summary>Root of the project being built (the application), also inside an external project's rules.</summary>
        public string? BuildProjectDirectory => GlobalEnvironment.ProjectPath;
        /// <summary>Name of the external project whose rules are evaluated; null for the built project itself.</summary>
        public string? ExternalProjectName { get; }
        /// <summary>Android: the NDK in use (the version directory, e.g. .../ndk/27.0.12077973); null on other platforms.</summary>
        public string? AndroidNdkRoot { get; }
        /// <summary>Android: the SDK (android.sdkRoot or ANDROID_HOME) when it exists; null on other platforms.</summary>
        public string? AndroidSdkRoot { get; }
        /// <summary>Android: API level of the compiler target (android.minSdk); null on other platforms.</summary>
        public int? AndroidApiLevel { get; }
        /// <summary>Android ABI being compiled ("arm64-v8a", "x86_64"); null on other platforms.</summary>
        public string? AndroidAbi { get; }
        public ModulesInfo(TargetConfiguration configuration, Platform platform, string? projectDirectory = null, string? externalProjectName = null)
        {
            Configuration = configuration;
            Platform = platform.Type;
            Architecture = platform.Architecture;
            _projectDirectory = projectDirectory;
            ExternalProjectName = externalProjectName;
            if (platform is AndroidPlatform android)
            {
                AndroidNdkRoot = android.NdkRoot;
                AndroidApiLevel = android.ApiLevel;
                AndroidAbi = android.Abi;
                string sdk = System.Environment.ExpandEnvironmentVariables(BuildConfigManager.Config.Android.SdkRoot ?? "");
                if (string.IsNullOrWhiteSpace(sdk)) sdk = HostPlatform.DefaultAndroidSdk();
                AndroidSdkRoot = !string.IsNullOrWhiteSpace(sdk) && System.IO.Directory.Exists(sdk) ? System.IO.Path.GetFullPath(sdk) : null;
            }
        }
    }
    public class ModuleRules
    {

        public ModuleType Type;

        public HashSet<string> PublicIncludePaths { get; } = new HashSet<string>();
        public HashSet<string> PrivateIncludePaths { get; } = new HashSet<string>();

        public HashSet<string> Definitions { get; } = new HashSet<string>();
        // Definitions are private in explicit targets; legacy projects retain their old merged environment.
        public HashSet<string> PublicDefinitions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PublicDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrivateDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ModuleDirectory => Path.GetDirectoryName(ModulePath)!;

        public HashSet<string> PublicSystemIncludePaths { get; } = new HashSet<string>();
        public HashSet<string> PrivateSystemIncludePaths { get; } = new HashSet<string>();

        public HashSet<string> InputLibraries { get; } = new HashSet<string>();
        public HashSet<string> DelayLoadLibraries { get; } = new HashSet<string>();
        /// <summary>macOS frameworks of the SDK or on the framework search path, linked as -framework Name.</summary>
        public HashSet<string> Frameworks { get; } = new(StringComparer.Ordinal);
        public RuntimeDependencyCollection RuntimeDependencies { get; } = new();
        /// <summary>
        /// Application resources (files or directories, module-relative; destination relative to the
        /// resource root): next to the program on Windows/Linux, in X.app/Contents/Resources on macOS,
        /// in X.app on iOS, in the APK's assets on Android. Read them with BeastResources.h.
        /// Modules of the application and of its dependency targets may declare them.
        /// </summary>
        public RuntimeDependencyCollection Resources { get; } = new();
        // APK-only declarations. Sources are module-relative; destinations are
        // relative to assets/ or a single filename under lib/arm64-v8a/.
        public RuntimeDependencyCollection AndroidAssets { get; } = new();
        public RuntimeDependencyCollection AndroidNativeLibraries { get; } = new();
        /// <summary>
        /// Java/Kotlin sources compiled into the APK (also from dependency and external targets):
        /// a file with its destination below the Java source root ("com/beast/sdk/TextInput.java";
        /// without one, the file's package declaration decides), or a directory whose contents are
        /// package directories (recursively). Staged outside the Gradle project and added to its
        /// main source set; files removed from the rules disappear from the next APK.
        /// </summary>
        public RuntimeDependencyCollection AndroidJavaSources { get; } = new();
        public List<BuildStep> BuildSteps { get; } = [];
        /// <summary>
        /// Sources outside the module directory compiled as part of this module (absolute, module-relative
        /// or with {ProjectDir}/{ModuleDir}... tokens), e.g. a file of the NDK:
        /// AdditionalSources.Add(Path.Combine(info.AndroidNdkRoot!, "sources/android/native_app_glue/android_native_app_glue.c")).
        /// </summary>
        public List<string> AdditionalSources { get; } = [];
        public HashSet<string> GeneratedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        [Obsolete("Not used by Beast. Use PublicDependencies/PrivateDependencies, InputLibraries or VcpkgLibraries.")]
        public HashSet<string> DirectoryDependencies { get; } = new HashSet<string>();
        [Obsolete("Not used by Beast (and misspelled). Use InputLibraries, VcpkgLibraries or a Conan/vcpkg manifest.")]
        public HashSet<string> ThirdPartyDependices { get; } = new HashSet<string>();

        /// <summary>Compiler settings for this module's sources; unset values inherit TargetRules.CompileOptions.</summary>
        public CompileOptions CompileOptions { get; } = new();
        /// <summary>Extra linker arguments contributed by this module to every target that links it.</summary>
        public List<string> LinkerArguments { get; } = [];
        /// <summary>Module-relative files, directories or glob patterns (*, ?, **) that are never compiled.</summary>
        public HashSet<string> ExcludedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// When true (default), sources inside directories named after another platform
        /// (Windows/Win64/Win32, Android, Linux, Mac, MacOS, IOS) are skipped.
        /// </summary>
        public bool UsePlatformDirectories = true;
        /// <summary>
        /// vcpkg libraries to link, by name ("fmt" finds fmt.lib/fmtd.lib, libfmt.a or libfmt.so) or file name.
        /// Requires vcpkg.json in the project root; package headers are always visible.
        /// </summary>
        public List<string> VcpkgLibraries { get; } = [];
        /// <summary>
        /// Name of the export macro defined for this module and its consumers (explicit targets only).
        /// null: MODULENAME_API; empty: no macro. Expands to __declspec(dllexport/dllimport) across a
        /// SharedLibrary target boundary on Windows, visibility("default") on Android, otherwise to nothing.
        /// </summary>
        public string? ApiMacro;

        public string PrecompiledHeaderSource = "";
        public HashSet<string> PrecompiledHeaderExcludedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool UseUnityBuild = false;
        public int UnityBatchSize = 8;
        public HashSet<string> UnityExcludedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string ModuleName = "";
        public string ModulePath = "";
        /// <summary>Root of the project that defines this module (the external project's root for its modules).</summary>
        public string ProjectDirectory = "";
        /// <summary>Name of the external project that defines this module; null for the project's own modules.</summary>
        public string? ExternalProject;

    }
}

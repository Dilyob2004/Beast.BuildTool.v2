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
        public string SourceDirectory
        {
            get
            {
                return GlobalEnvironment.SourcePath;
            }
        }
        public string? ProjectDirectory => GlobalEnvironment.ProjectPath;
        public string? ProjectSourceDirectory => GlobalEnvironment.ProjectPath != null ? System.IO.Path.Combine(GlobalEnvironment.ProjectPath, "Source") : null;
        public ModulesInfo(TargetConfiguration configuration, Platform platform)
        {
            Configuration = configuration;
            Platform = platform.Type;
            Architecture = platform.Architecture;
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
        public List<BuildStep> BuildSteps { get; } = [];
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

    }
}

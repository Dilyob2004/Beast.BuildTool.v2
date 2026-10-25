using System;
using System.Collections.Generic;

namespace Beast.BuildTool
{
    public enum PlatformArch
    {
        X64,
        ARM64
    }

    public class InvalidArchitectureException : Exception
    {
        public InvalidArchitectureException(PlatformArch architecture)
        : base(string.Format("Unknown architecture {0}.", architecture))
        {
        }
        public InvalidArchitectureException(PlatformArch architecture, string message)
        : base(string.Format("Unknown architecture {0}. " + message, architecture))
        {
        }
    }
    public enum PlatformType
    {
        Android,
        Windows,
        Linux,
        Mac,
        IOS
    }
    public abstract class Platform
    {

        public abstract PlatformArch Architecture { get; }
        public abstract PlatformType Type { get; }

        public readonly HashSet<string> SystemIncludePaths = new HashSet<string>();
        public readonly HashSet<string> SystemLibraryPaths = new HashSet<string>();

        public abstract string SharedLibraryFileExtension { get; }
        public abstract string PlatformName { get; }
        /// <summary>".exe" on Windows, empty elsewhere.</summary>
        public virtual string ExecutableFileExtension => Type == PlatformType.Windows ? ".exe" : "";
        /// <summary>File name prefix of libraries: "lib" (libCore.a, libCore.so), empty on Windows.</summary>
        public virtual string LibraryPrefix => Type == PlatformType.Windows ? "" : "lib";
        /// <summary>True when executables built for this platform run on the machine running Beast.</summary>
        public virtual bool CanRunOnHost => false;
        /// <summary>BMI file extension of C++20 modules (.ifc, .pcm, .gcm); null when the compiler is not supported.</summary>
        public virtual string? ModuleInterfaceExtension => null;
        /// <summary>Why C++20 modules are unavailable (when ModuleInterfaceExtension is null).</summary>
        public virtual string ModulesUnsupportedReason => $"the {PlatformName} toolchain does not support them";
        /// <summary>The program file of an application target in <paramref name="binaries"/>.</summary>
        public virtual string ExecutablePath(string binaries, string target) => System.IO.Path.Combine(binaries, target + ExecutableFileExtension);

        /// <inheritdoc />
        public abstract string StaticLibraryFileExtension { get; }
        public abstract bool IsValid();
        /// <summary>Set per target: archives receive LTO objects (MSVC lib.exe /LTCG).</summary>
        public bool LinkTimeOptimization { get; set; }
        public virtual void SetupCPPEnvironment(CPPEnvironment _Environment)
        {
        }
        public virtual void SetupLinkEnvironment(LinkEnvironment LinkEnv)
        {
        }
        public abstract CPPOutput CompileCppFiles(CommandExecutor Executor, CPPEnvironment Environment, List<string> sourceFiles, string outputPath);

        public abstract void LinkFiles(CommandExecutor Executor, LinkEnvironment Env, string outputFilePath);
        public abstract void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath);
    }
}

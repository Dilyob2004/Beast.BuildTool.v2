using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Beast.BuildTool
{
    public enum LinkerOutput
    {
        /// <summary>
        /// The executable file (aka .exe file).
        /// </summary>
        Executable,

        /// <summary>
        /// The shared library file (aka .dll file).
        /// </summary>
        SharedLibrary,

        /// <summary>
        /// The static library file (aka .lib file).
        /// </summary>
        StaticLibrary,

        /// <summary>
        /// The import library file (aka .lib file).
        /// </summary>
    }
    public class LinkEnvironment : ICloneable
    {
        /// <summary>
        /// The output type.
        /// </summary>
        public LinkerOutput Output;

        /// <summary>
        /// Enables the code optimization.
        /// </summary>
        public bool Optimization = false;

        /// <summary>
        /// Enables debug information generation.
        /// </summary>
        public bool DebugInformation = false;

        /// <summary>
        /// Hints to use full debug information.
        /// </summary>
        public bool UseFullDebugInformation = false;

        /// <summary>
        /// Hints to use fast PDB linking.
        /// </summary>
        public bool UseFastPDBLinking = false;

        /// <summary>
        /// Enables the link time code generation (LTCG).
        /// </summary>
        public bool LinkTimeCodeGeneration = false;

        /// <summary>
        /// Hints to use incremental linking.
        /// </summary>
        public bool UseIncrementalLinking = false;

        /// <summary>
        /// Use CONSOLE subsystem on Windows instead of the WINDOWS one.
        /// </summary>
        public bool LinkAsConsoleProgram = true;

        /// <summary>
        /// The collection of the object files to be linked.
        /// </summary>
        public HashSet<string> InputFiles = new HashSet<string>(); 

        /// <summary>
        /// The collection of dependent static or import libraries that need to be linked.
        /// </summary>
        public readonly HashSet<string> InputLibraries = new HashSet<string>();

        public readonly HashSet<string> ExcludedLibraries = new HashSet<string>();
        public readonly HashSet<string> DelayLoadLibraries = new HashSet<string>();

        /// <summary>
        /// The collection of dependent static or import libraries paths.
        /// </summary>
        public readonly HashSet<string> LibraryPaths = new HashSet<string>();


        /// <summary>
        /// The collection of custom arguments to pass to the linker.
        /// </summary>
        public readonly List<string> CustomArgs = new List<string>();
        /// <summary>Link the C++ runtime statically (Android -static-libstdc++).</summary>
        public bool StaticRuntime = false;
        /// <summary>Sanitizers the objects were compiled with (runtime libraries, link flags).</summary>
        public Sanitizer Sanitizers = Sanitizer.None;

        /// <inheritdoc />
        public object Clone()
        {
            var clone = new LinkEnvironment
            {
                Output = Output,
                Optimization = Optimization,
                DebugInformation = DebugInformation,
                UseFullDebugInformation = UseFullDebugInformation,
                UseFastPDBLinking = UseFastPDBLinking,
                LinkTimeCodeGeneration = LinkTimeCodeGeneration,
                UseIncrementalLinking = UseIncrementalLinking,
                LinkAsConsoleProgram = LinkAsConsoleProgram,
                StaticRuntime = StaticRuntime,
                Sanitizers = Sanitizers,
            };
            clone.InputFiles.AddRange(InputFiles);
            clone.InputLibraries.AddRange(InputLibraries);
            clone.LibraryPaths.AddRange(LibraryPaths);
            clone.CustomArgs.AddRange(CustomArgs);
            return clone;
        }
    }

}

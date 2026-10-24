using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Beast.BuildTool
{

    public enum CppVersion
    {
        /// <summary>
        /// C++14
        /// </summary>
        Cpp14,

        /// <summary>
        /// C++17
        /// </summary>
        Cpp17,

        /// <summary>
        /// C++20
        /// </summary>
        Cpp20,

        /// <summary>
        /// The latest version supported by the compiler.
        /// </summary>
        Latest,
    }

    public enum PrecompiledHeaderFileUsage
    {
        /// <summary>
        /// Precompiled Headers Files (PCH) feature is disabled.
        /// </summary>
        None,

        /// <summary>
        /// Enables creation and usage of the header file. The input source PCH will be precompiled and included.
        /// </summary>
        CreateManual,

        /// <summary>
        /// Enables usage of the header file. The input source PCH will be included in the build (assuming it exists).
        /// </summary>
        UseManual,
    }
    /// <summary>
    /// The compilation optimization hint.
    /// </summary>
    public enum FavorSizeOrSpeed
    {
        /// <summary>
        /// The default option.
        /// </summary>
        Neither,

        /// <summary>
        /// The fast code.
        /// </summary>
        FastCode,

        /// <summary>
        /// The small code.
        /// </summary>
        SmallCode
    }

    /// <summary>
    /// The code sanitizers for core errors detection by compiler-supported checks.
    /// </summary>
    [Flags]
    public enum Sanitizer
    {
        /// <summary>
        /// No sanitizers in use.
        /// </summary>
        None = 0,

        /// <summary>
        /// Memory errors detector,
        /// </summary>
        Address = 1,

        /// <summary>
        /// Data races and deadlocks detector.
        /// </summary>
        Thread = 2,

        /// <summary>
        /// Uninitialized memory reads detector.
        /// </summary>
        Memory = 4,

        /// <summary>
        /// Undefined behavior (UB) detector.
        /// </summary>
        Undefined = 8,
    }
    public static class HashSetExtensions
    {
        public static void AddRange<T>(this HashSet<T> hashSet, IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                hashSet.Add(item);
            }
        }
    }
    public class CPPEnvironment : ICloneable
    {
        /// <summary>
        /// C++ standard version to use for compilation.
        /// </summary>
        public CppVersion CppVersion = CppVersion.Cpp20;

        /// <summary>
        /// Selects a predefined set of options that affect the size and speed of generated code.
        /// </summary>
        public FavorSizeOrSpeed FavorSizeOrSpeed = FavorSizeOrSpeed.Neither;

        /// <summary>
        /// Selects a sanitizers to use (as flags).
        /// </summary>
        public Sanitizer Sanitizers = Sanitizer.None;

        /// <summary>
        /// Enables exceptions support.
        /// </summary>
        public bool EnableExceptions = true;

        /// <summary>
        /// Enables RTTI.
        /// </summary>
        public bool RuntimeTypeInfo = true;

        /// <summary>
        /// Enables functions inlining support.
        /// </summary>
        public bool Inlining = false;

        /// <summary>
        /// Enables code optimization.
        /// </summary>
        public bool Optimization = false;

        /// <summary>
        /// Enables the whole program optimization.
        /// </summary>
        public bool WholeProgramOptimization = false;

        /// <summary>
        /// Enables functions level linking support.
        /// </summary>
        public bool FunctionLevelLinking = false;

        /// <summary>
        /// Enables debug information generation.
        /// </summary>
        public bool DebugInformation = false;

        /// <summary>
        /// Hints to use Debug version of the standard library.
        /// </summary>
        public bool UseDebugCRT = false;

        /// <summary>
        /// Enables runtime checks.
        /// </summary>
        public bool RuntimeChecks = false;

        /// <summary>
        /// Enables string pooling.
        /// </summary>
        public bool StringPooling = false;

        /// <summary>
        /// Enables the compiler intrinsic functions.
        /// </summary>
        public bool IntrinsicFunctions = false;

        /// <summary>
        /// Enables buffer security checks.
        /// </summary>
        public bool BufferSecurityCheck = true;

        /// <summary>
        /// Hints to treat warnings as errors.
        /// </summary>
        public bool TreatWarningsAsErrors = false;

        /// <summary>Warning level (see <see cref="WarningLevel"/>).</summary>
        public WarningLevel Warnings = WarningLevel.Default;

        /// <summary>C language standard for .c files.</summary>
        public CLanguageStandard CStandard = CLanguageStandard.Default;

        /// <summary>Floating-point model.</summary>
        public FloatingPointModel FloatingPoint = FloatingPointModel.Precise;

        /// <summary>Static C/C++ runtime (MSVC /MT, Android -static-libstdc++).</summary>
        public bool StaticRuntime = false;

        /// <summary>
        /// The collection of defines with preprocessing symbol for a source files.
        /// </summary>
        public readonly HashSet<string> PreprocessorDefinitions = new HashSet<string>();

        /// <summary>
        /// The additional paths to add to the list of directories searched for include files.
        /// </summary>
        public readonly HashSet<string> IncludePaths = new HashSet<string>();

        /// <summary>
        /// Third-party include directories (module *SystemIncludePaths, vcpkg, Conan): MSVC /external:I,
        /// Clang -isystem. Their warnings are not reported.
        /// </summary>
        public readonly HashSet<string> ExternalIncludePaths = new HashSet<string>();

        /// <summary>
        /// Custom compiler arguments, in order; duplicates are kept (e.g. repeated -Xclang).
        /// </summary>
        public readonly List<string> CustomArgs = new List<string>();

        /// <summary>
        /// The Precompiled Header File (PCH) usage.
        /// </summary>
        public PrecompiledHeaderFileUsage PrecompiledHeaderUsage = PrecompiledHeaderFileUsage.None;

        /// <summary>
        /// The Precompiled Header File (PCH) binary path. Null if not created.
        /// </summary>
        public string? PrecompiledHeaderFile;

        /// <summary>
        /// The Precompiled Header File (PCH) source path. Null if not provided.
        /// </summary>
        public string PrecompiledHeaderSource = "";
        public readonly HashSet<string> PrecompiledHeaderExcludedFiles = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> SourcePrerequisites = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>C++20 module units among the sources: the BMI each writes and the BMIs it references.</summary>
        public readonly Dictionary<string, ModuleCompileUnit> ModuleUnits = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public object Clone()
        {
            var clone = new CPPEnvironment
            {
                CppVersion = CppVersion,
                FavorSizeOrSpeed = FavorSizeOrSpeed,
                Sanitizers = Sanitizers,
                EnableExceptions = EnableExceptions,
                RuntimeTypeInfo = RuntimeTypeInfo,
                Inlining = Inlining,
                Optimization = Optimization,
                WholeProgramOptimization = WholeProgramOptimization,
                FunctionLevelLinking = FunctionLevelLinking,
                DebugInformation = DebugInformation,
                UseDebugCRT = UseDebugCRT,
                RuntimeChecks = RuntimeChecks,
                StringPooling = StringPooling,
                IntrinsicFunctions = IntrinsicFunctions,
                BufferSecurityCheck = BufferSecurityCheck,
                TreatWarningsAsErrors = TreatWarningsAsErrors,
                Warnings = Warnings,
                CStandard = CStandard,
                FloatingPoint = FloatingPoint,
                StaticRuntime = StaticRuntime,
                PrecompiledHeaderUsage = PrecompiledHeaderUsage,
                PrecompiledHeaderFile = PrecompiledHeaderFile,
                PrecompiledHeaderSource = PrecompiledHeaderSource,
            };
            clone.AdditionalPrerequisites.AddRange(AdditionalPrerequisites);
            clone.PrecompiledHeaderExcludedFiles.UnionWith(PrecompiledHeaderExcludedFiles);
            foreach (var pair in SourcePrerequisites) clone.SourcePrerequisites.Add(pair.Key, pair.Value.ToList());
            foreach (var pair in ModuleUnits) clone.ModuleUnits.Add(pair.Key, pair.Value);
            clone.PreprocessorDefinitions.AddRange(PreprocessorDefinitions);
            clone.IncludePaths.AddRange(IncludePaths);
            clone.ExternalIncludePaths.AddRange(ExternalIncludePaths);
            clone.CustomArgs.AddRange(CustomArgs);
            return clone;
        }

        /// <summary>
        /// Additional prerequisite files that will trigger recompilation if modified (e.g. module's .Build.cs).
        /// </summary>
        public readonly List<string> AdditionalPrerequisites = new List<string>();
    }
    public class CPPOutput
    {
        /// <summary>
        /// The result object files.
        /// </summary>
        public readonly List<string> ObjectFiles = new List<string>();

        /// <summary>
        /// The result precompiled header file (PCH) created during compilation. Can be used in other compilations (as shared).
        /// </summary>
        public string ?PrecompiledHeaderFile;
    }
}

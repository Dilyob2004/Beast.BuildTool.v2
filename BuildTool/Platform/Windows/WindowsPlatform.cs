using Microsoft.VisualBasic;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace Beast.BuildTool
{


    public class WindowsPlatform : Platform
    {
        /// <summary>
        /// The VC tools root path.
        /// </summary>
        protected readonly string ?VCToolPath;

        /// <summary>
        /// The compiler path.
        /// </summary>
        protected readonly string ?CompilerPath;

        /// <summary>
        /// The resource compiler path.
        /// </summary>
        public readonly string? ResourceCompilerPath;

        /// <summary>
        /// The linker path.
        /// </summary>
        protected readonly string ?LinkerPath;

        /// <summary>
        /// The library tool path.
        /// </summary>
        protected readonly string ? LibToolPath;


        public override string SharedLibraryFileExtension => ".dll";

        /// <inheritdoc />
        public override string StaticLibraryFileExtension => ".lib";
        /// <summary>x64 keeps the historical "Windows" directory names; ARM64 builds use "WindowsArm64".</summary>
        public override string PlatformName => Architecture == PlatformArch.ARM64 ? "WindowsArm64" : "Windows";
        public override string? ModuleInterfaceExtension => UsesClangCl ? null : ".ifc";
        public override bool CanRunOnHost => HostPlatform.IsWindows && (Architecture != PlatformArch.ARM64 || HostPlatform.IsArm64);


        public override PlatformType Type { get; } = PlatformType.Windows;
        public override PlatformArch Architecture { get; }

        /// <summary>True when windows.compiler is "clang-cl": C/C++ sources are compiled by LLVM's clang-cl.</summary>
        public bool UsesClangCl { get; }
        private readonly string? _llvmBin;
        private readonly string _msvcBin;
        private string TargetDirectory => Architecture == PlatformArch.ARM64 ? "arm64" : "x64";
        private string ClangTriple => Architecture == PlatformArch.ARM64 ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        /// <summary>MASM for x64, armasm64 for ARM64; both ship with MSVC.</summary>
        private string AssemblerPath => Path.Combine(_msvcBin, Architecture == PlatformArch.ARM64 ? "armasm64.exe" : "ml64.exe");
        // clang-cl + LTO needs LLVM's linker and librarian (link.exe cannot read LLVM bitcode).
        private string ActiveLinker(bool lto) => UsesClangCl && lto ? Path.Combine(_llvmBin!, "lld-link.exe") : LinkerPath!;
        private string ActiveLibrarian => UsesClangCl && LinkTimeOptimization ? Path.Combine(_llvmBin!, "llvm-lib.exe") : LibToolPath!;

        /// <summary>True when the MSVC tools for <paramref name="architecture"/> are installed (used by IDE generation).</summary>
        public static bool IsInstalled(PlatformArch architecture)
        {
            try { return new WindowsPlatform(architecture) is { } platform && platform.IsValid(); }
            catch (Exception) { return false; }
        }

        private Version? MinVersion = new Version(0, 0);

        public override void SetupLinkEnvironment(LinkEnvironment LinkEnv)
        {
            base.SetupLinkEnvironment(LinkEnv);
            LinkEnv.InputLibraries.Add("dwmapi.lib");
            LinkEnv.InputLibraries.Add("kernel32.lib");
            LinkEnv.InputLibraries.Add("user32.lib");
            LinkEnv.InputLibraries.Add("comdlg32.lib");
            LinkEnv.InputLibraries.Add("advapi32.lib");
            LinkEnv.InputLibraries.Add("shell32.lib");
            LinkEnv.InputLibraries.Add("ole32.lib");
            LinkEnv.InputLibraries.Add("oleaut32.lib");
            LinkEnv.InputLibraries.Add("delayimp.lib");
            LinkEnv.InputLibraries.Add("gdi32.lib");
            LinkEnv.InputLibraries.Add("Ws2_32.lib");


            LinkEnv.ExcludedLibraries.Add("LIBC");
            LinkEnv.ExcludedLibraries.Add("LIBCP");
            LinkEnv.ExcludedLibraries.Add("LIBCD");
            LinkEnv.ExcludedLibraries.Add("LIBCPD");
        }
        public override void SetupCPPEnvironment(CPPEnvironment Environment)
        {
            base.SetupCPPEnvironment(Environment);
            Environment.PreprocessorDefinitions.Add("_CRT_SECURE_NO_WARNINGS");
            //Environment.PreprocessorDefinitions.Add("_CRT_SECURE_NO_DEPRECATE");
            //Environment.PreprocessorDefinitions.Add("_CRT_USE_BUILTIN_OFFSETOF");


            /*if (Environment.CppVersion == CppVersion.Latest)
                Environment.PreprocessorDefinitions.Add("_SILENCE_CXX23_ALIGNED_STORAGE_DEPRECATION_WARNING");
            if (Environment.CppVersion == CppVersion.Cpp20)
                Environment.PreprocessorDefinitions.Add("_SILENCE_CXX20_ALIGNED_STORAGE_DEPRECATION_WARNING");
            else if(Environment.CppVersion == CppVersion.Cpp17)
                Environment.PreprocessorDefinitions.Add("_SILENCE_CXX17_ALIGNED_STORAGE_DEPRECATION_WARNING");
            */
            Environment.PreprocessorDefinitions.Add("PLATFORM_WINDOWS");
            Environment.PreprocessorDefinitions.Add("PLATFORM_WIN32");
            Environment.PreprocessorDefinitions.Add("WIN32");
            Environment.PreprocessorDefinitions.Add("WIN64");
            Environment.PreprocessorDefinitions.Add("_WINDOWS");
        }

        /// <summary>LLVM's compiler-rt directory (lib/clang/&lt;version&gt;/lib/windows) used by clang-cl.</summary>
        private string ClangRuntimeDirectory()
        {
            string root = Path.Combine(Path.GetDirectoryName(_llvmBin!)!, "lib", "clang");
            string? found = Directory.Exists(root) ? Directory.GetDirectories(root)
                .Select(d => Path.Combine(d, "lib", "windows")).Where(Directory.Exists)
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(d))), out var v) ? v : new Version(0, 0))
                .FirstOrDefault() : null;
            return found ?? throw new DirectoryNotFoundException($"clang runtime libraries were not found under {root}.");
        }

        /// <summary>
        /// clang-cl AddressSanitizer link inputs for link.exe/lld-link: the dynamic runtime import library
        /// and, whole-archived, the runtime thunk (names differ between LLVM versions; both are probed).
        /// </summary>
        internal IEnumerable<string> ClangAddressSanitizerLinkArguments()
        {
            string directory = ClangRuntimeDirectory();
            string suffix = Architecture == PlatformArch.ARM64 ? "aarch64" : "x86_64";
            string runtime = Path.Combine(directory, $"clang_rt.asan_dynamic-{suffix}.lib");
            if (!File.Exists(runtime)) throw new FileNotFoundException("clang AddressSanitizer runtime library was not found.", runtime);
            yield return CommandUtilities.QuoteArgument(runtime);
            foreach (string thunk in new[] { "asan_dynamic_runtime_thunk", "asan_static_runtime_thunk" }.Select(n => Path.Combine(directory, $"clang_rt.{n}-{suffix}.lib")).Where(File.Exists).Take(1))
                yield return "/wholearchive:" + CommandUtilities.QuoteArgument(thunk);
        }

        /// <summary>
        /// AddressSanitizer runtime DLLs to copy beside EXE/DLL targets: MSVC's clang_rt.asan*_dynamic
        /// (one or two, by VS version), or LLVM's with clang-cl.
        /// </summary>
        public IReadOnlyList<string> AddressSanitizerRuntimes()
        {
            string directory = UsesClangCl ? ClangRuntimeDirectory() : _msvcBin;
            string suffix = Architecture == PlatformArch.ARM64 ? "aarch64" : "x86_64";
            var found = Directory.Exists(directory)
                ? Directory.GetFiles(directory, $"clang_rt.asan*dynamic-{suffix}.dll").Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
            if (found.Count == 0) throw new FileNotFoundException($"AddressSanitizer runtime (clang_rt.asan_dynamic-{suffix}.dll) was not found in {directory}. Install the C++ AddressSanitizer component of Visual Studio.");
            return found;
        }

        public override bool IsValid()
        {

            if(string.IsNullOrEmpty(VCToolPath) || !File.Exists(CompilerPath) || !File.Exists(LinkerPath) || !File.Exists(LibToolPath) || !File.Exists(ResourceCompilerPath))
            {
                return false;
            }

            return true;
        }
        public WindowsPlatform(PlatformArch architecture = PlatformArch.X64)
        {
            if (!HostPlatform.IsWindows)
                throw new PlatformNotSupportedException("Windows targets require a Windows host with Visual Studio (MSVC). On this host build --platform Android.");
            Architecture = architecture;
            if (VisualStudioInstance.VCToolChainPath == null)
            {
                VisualStudioInstance.EnsureVCToolPath();
            }

            VCToolPath = VisualStudioInstance.VCToolChainPath;

            if (VCToolPath == null)
                throw new Exception("Visual Studio VC Tool Path not found");
            // Native ARM64 host tools when present, otherwise the x64-hosted (cross) compilers.
            string host = HostPlatform.IsArm64 && Directory.Exists(Path.Combine(VCToolPath, "bin", "HostARM64")) ? "HostARM64" : "Hostx64";
            string BinaryPath = Path.Combine(VCToolPath, "bin", host, TargetDirectory);
            _msvcBin = BinaryPath;
            if (!File.Exists(Path.Combine(BinaryPath, "cl.exe")))
                throw new FileNotFoundException($"MSVC tools for {TargetDirectory} were not found. Install the \"MSVC {TargetDirectory} build tools\" component of Visual Studio.", Path.Combine(BinaryPath, "cl.exe"));

            CompilerPath = Path.Combine(BinaryPath, "cl.exe");
            LinkerPath = Path.Combine(BinaryPath, "link.exe");
            LibToolPath = Path.Combine(BinaryPath, "lib.exe");

            var windows = BuildConfigManager.Config.Windows;
            switch (windows.Compiler.Trim().ToLowerInvariant())
            {
                case "" or "msvc" or "cl": break;
                case "clang-cl" or "clang":
                    string llvm = !string.IsNullOrWhiteSpace(windows.LlvmRoot)
                        ? Environment.ExpandEnvironmentVariables(windows.LlvmRoot)
                        : Path.GetFullPath(Path.Combine(VCToolPath, "..", "..", "Llvm", HostPlatform.IsArm64 ? "ARM64" : "x64"));
                    _llvmBin = Path.Combine(llvm, "bin");
                    if (!File.Exists(Path.Combine(_llvmBin, "clang-cl.exe")))
                        throw new FileNotFoundException("clang-cl was not found. Install \"C++ Clang tools for Windows\" in Visual Studio or set windows.llvmRoot.", Path.Combine(_llvmBin, "clang-cl.exe"));
                    CompilerPath = Path.Combine(_llvmBin, "clang-cl.exe");
                    UsesClangCl = true;
                    break;
                default: throw new InvalidOperationException($"Unknown windows.compiler '{windows.Compiler}'. Use \"msvc\" or \"clang-cl\".");
            }

            SystemIncludePaths.Add(Path.Combine(VCToolPath, "include"));
            SystemLibraryPaths.Add(Path.Combine(VCToolPath, "lib", TargetDirectory));

            if (TryGetLatestWindowsSdkDir(out string? SDKDir, out List<Version>? Versions))
            {

                if (string.IsNullOrEmpty(SDKDir) || Versions == null || Versions.Count == 0)
                {
                    throw new Exception("Windows SDK not found");
                }

                // Pick the newest SDK unless Config/BuildConfig.json pins windows.sdkVersion.
                string pinned = BuildConfigManager.Config.Windows.SdkVersion;
                if (!string.IsNullOrWhiteSpace(pinned))
                {
                    if (!Version.TryParse(pinned, out var requested) || !Versions.Contains(requested))
                        throw new Exception($"Windows SDK {pinned} is not installed. Available: {string.Join(", ", Versions.Order())}");
                    MinVersion = requested;
                }
                else MinVersion = Versions.Max()!;
                string LatestSDKVersionName = MinVersion.ToString();

                string includeRootDir = Path.Combine(SDKDir, "include", LatestSDKVersionName);
                {
                    SystemIncludePaths.Add(Path.Combine(includeRootDir, "ucrt"));
                    SystemIncludePaths.Add(Path.Combine(includeRootDir, "shared"));
                    SystemIncludePaths.Add(Path.Combine(includeRootDir, "um"));
                    SystemIncludePaths.Add(Path.Combine(includeRootDir, "winrt"));
                }
                string libraryRootDir = Path.Combine(SDKDir, "lib", LatestSDKVersionName);
                {
                    SystemLibraryPaths.Add(Path.Combine(libraryRootDir, "ucrt", TargetDirectory));
                    SystemLibraryPaths.Add(Path.Combine(libraryRootDir, "um", TargetDirectory));
                }
                // rc.exe produces machine-independent .res files: use the host's build of it.
                string sdkBin = Path.Combine(SDKDir, "bin", LatestSDKVersionName);
                string rcHost = HostPlatform.IsArm64 && File.Exists(Path.Combine(sdkBin, "arm64", "rc.exe")) ? "arm64" : "x64";
                ResourceCompilerPath = Path.Combine(sdkBin, rcHost, "rc.exe");
            }
            else
            {
                throw new Exception("Windows SDK not found");
            }
        }
        private void AppendArgs_CPP(CPPEnvironment CompileEnvironment, List<string> Arguments)
        {
            Arguments.Add("/TP");
            // C++ version
            switch (CompileEnvironment.CppVersion)
            {
                case CppVersion.Cpp14:
                    Arguments.Add("/std:c++14");
                    break;
                case CppVersion.Cpp17:
                    Arguments.Add("/std:c++17");
                    break;
                case CppVersion.Cpp20:
                    Arguments.Add("/std:c++20");
                    break;
                case CppVersion.Latest:
                    Arguments.Add("/std:c++latest");
                    break;
            }
            Arguments.Add("/Zc:__cplusplus");
            AppendWarningArgs(CompileEnvironment, Arguments, false);


            // Enable Run-Time Type Information
            if (CompileEnvironment.RuntimeTypeInfo)
                Arguments.Add("/GR");
            else
                Arguments.Add("/GR-");
            // Buffer Security Check
            if (CompileEnvironment.BufferSecurityCheck)
                Arguments.Add("/GS");
            else
                Arguments.Add("/GS-");
        }
        private void AppendArgs_C(CPPEnvironment CompileEnvironment, List<string> Arguments)
        {
            // Explicitly compile the file as C.
            Arguments.Add("/TC");

            switch (CompileEnvironment.CStandard)
            {
                case CLanguageStandard.C11: Arguments.Add("/std:c11"); break;
                case CLanguageStandard.C17: Arguments.Add("/std:c17"); break;
            }
            // Default level for C is /W0: needed for external C code that is noisy at higher levels.
            AppendWarningArgs(CompileEnvironment, Arguments, true);

            Arguments.Add("/Zc:strictStrings-"); 
            Arguments.Add("/Zc:ternary-");
        }


        private static void AppendWarningArgs(CPPEnvironment environment, List<string> arguments, bool isC)
        {
            switch (environment.Warnings)
            {
                case WarningLevel.Off: arguments.Add("/W0"); break;
                case WarningLevel.High: arguments.Add("/W4"); break;
                case WarningLevel.Pedantic:
                    arguments.Add("/W4");
                    if (!isC) arguments.Add("/permissive-");
                    break;
                default: arguments.Add(isC ? "/W0" : "/W3"); break;
            }
        }

        public override CPPOutput CompileCppFiles(CommandExecutor Executor, CPPEnvironment Environment, List<string> sourceFiles, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            var Output = new CPPOutput();
            
            // Initialize dependency cache for this output path

            var CommonArgs = new List<string>();
            {
                if (UsesClangCl)
                {
                    if (Environment.Sanitizers.HasFlag(Sanitizer.Address) && Environment.StaticRuntime)
                        throw new InvalidOperationException("clang-cl AddressSanitizer needs the dynamic CRT: disable StaticRuntime or use windows.compiler \"msvc\".");
                    CommonArgs.Add("--target=" + ClangTriple);
                    // clang-cl ignores MSVC options it does not implement; keep that from failing /WX builds.
                    CommonArgs.Add("-Wno-unknown-argument");
                    CommonArgs.Add("-Wno-unused-command-line-argument");
                }
                // Suppress Startup Banner
                CommonArgs.Add("/nologo");
                // Compile project sources as UTF-8 on every platform.
                CommonArgs.Add("/utf-8");

                // Compile Without Linking
                CommonArgs.Add("/c");

                // Generate Intrinsic Functions
                if (Environment.IntrinsicFunctions)
                    CommonArgs.Add("/Oi");

                // Enable Function-Level Linking
                if (Environment.FunctionLevelLinking)
                    CommonArgs.Add("/Gy");
                else
                    CommonArgs.Add("/Gy-");


                // List Include Files
                //CommonArgs.Add("/showIncludes");

                // Code Analysis
                if (!UsesClangCl) CommonArgs.Add("/analyze-");

                // Remove unreferenced COMDAT
                CommonArgs.Add("/Zc:inline");

                // Run-Time Error Checks
                if (Environment.RuntimeChecks && !UsesClangCl)
                    CommonArgs.Add("/RTC1");

                // Inline Function Expansion
                if (Environment.Inlining)
                    CommonArgs.Add("/Ob2");

                if (Environment.DebugInformation)
                {
                    // Debug Information Format
                    //if (UseSeparatePdb)
                    // Embed CodeView in each object: relocatable cache/worker outputs without compiler-PDB locks.
                    CommonArgs.Add("/Z7");
                    //else
                    //CommonArgs.Add("/Z7");

                    // Enhance Optimized Debugging
                    if (!UsesClangCl) CommonArgs.Add("/Zo");
                }

                // Favor Small Code, Favor Fast Code
                if (Environment.FavorSizeOrSpeed == FavorSizeOrSpeed.FastCode)
                    CommonArgs.Add("/Ot");
                else if (Environment.FavorSizeOrSpeed == FavorSizeOrSpeed.SmallCode)
                    CommonArgs.Add("/Os");
                if (Environment.Optimization)
                {
                    // Generate Intrinsic Functions
                    CommonArgs.Add("/Oi");

                    // Frame-Pointer Omission
                    CommonArgs.Add("/Oy");

                    // Only use /Ox with /GL to prevent too long build times
                    if (Environment.WholeProgramOptimization)
                    {
                        // Enable Most Speed Optimizations
                        CommonArgs.Add("/Ox");

                        // Whole Program Optimization (clang-cl: ThinLTO bitcode for lld-link)
                        CommonArgs.Add(UsesClangCl ? "-flto=thin" : "/GL");
                    }
                    else
                    {
                        // Enable SEE2 and other code optimizations but without breaking incremental linking
                        //CommonArgs.Add("/Og"); // Results in D9035 warning
                        CommonArgs.Add("/O2");
                    }
                }
                else
                {
                    // Disable compiler optimizations (Debug)
                    CommonArgs.Add("/Od");

                    // Frame-Pointer Omission
                    CommonArgs.Add("/Oy-");
                }

                if (Environment.Sanitizers.HasFlag(Sanitizer.Address))
                    CommonArgs.Add(UsesClangCl ? "-fsanitize=address" : "/fsanitize=address");

                // Full Path of Source Code File in Diagnostics
                CommonArgs.Add("/FC"); 

                // Report Internal Compiler Errors
                if (!UsesClangCl) CommonArgs.Add("/errorReport:prompt");

                // Exception Handling Model
                //if (!Environment.CompileAsWinRT)
                {
                    if (Environment.EnableExceptions)
                        CommonArgs.Add("/EHsc");
                    else
                        CommonArgs.Add("/D_HAS_EXCEPTIONS=0");
                }

                // Eliminate Duplicate Strings
                if (Environment.StringPooling)
                    CommonArgs.Add("/GF");
                else
                    CommonArgs.Add("/GF-");

                // Use Run-Time Library
                if (Environment.StaticRuntime)
                    CommonArgs.Add(Environment.UseDebugCRT ? "/MTd" : "/MT");
                else
                    CommonArgs.Add(Environment.UseDebugCRT ? "/MDd" : "/MD");

                // Specify floating-point behavior
                CommonArgs.Add(Environment.FloatingPoint switch
                {
                    FloatingPointModel.Fast => "/fp:fast",
                    FloatingPointModel.Strict => "/fp:strict",
                    _ => "/fp:precise"
                });



                // Treats all compiler warnings as errors
                if (Environment.TreatWarningsAsErrors)
                    CommonArgs.Add("/WX");
                else
                    CommonArgs.Add("/WX-");

                // Warning level is added per language (AppendArgs_C/AppendArgs_CPP).

                // wchar_t is Native Type
                CommonArgs.Add("/Zc:wchar_t");

                // Common Language Runtime Compilation
                //if (Environment.CompileAsWinRT)
                //CommonArgs.Add("/clr");
                // Preprocessor conformance mode
                if (!UsesClangCl) CommonArgs.Add("/Zc:preprocessor");

            }

            foreach (var definition in Environment.PreprocessorDefinitions)
                CommonArgs.Add("/D " + CommandUtilities.QuoteArgument(definition));

            foreach (var includePath in Environment.IncludePaths)
                AddIncludePath(CommonArgs, includePath, false);

            foreach (var includePath in Environment.ExternalIncludePaths.Concat(SystemIncludePaths))
                AddIncludePath(CommonArgs, includePath, true);



            CommonArgs.AddRange(Environment.CustomArgs);
            string? pchFile = null, pchSource = null;
            var CompileArgs = new List<string>();
            if (sourceFiles.Any(s => ModuleCompilation.UsesPch(Environment, s)))
            {
                if (Environment.PrecompiledHeaderUsage != PrecompiledHeaderFileUsage.CreateManual)
                    throw new InvalidOperationException("UseManual PCH is unsupported; let the module create its PCH.");
                pchSource = Path.GetFullPath(Environment.PrecompiledHeaderSource);
                string pchSourcePath = Path.Combine(outputPath, "Generated", "BeastPch.cpp");
                ModuleCompilation.WriteIfChanged(pchSourcePath, $"#include \"{pchSource.Replace('\\', '/')}\"\n");
                pchFile = Path.Combine(outputPath, "BeastPch.pch");
                string pchObjFile = Path.Combine(outputPath, "BeastPch.obj");
                var (dependencyFile, dependencyFormat, dependencyArgument) = Dependencies(pchFile);
                Output.PrecompiledHeaderFile = pchFile;
                Output.ObjectFiles.Add(pchObjFile);
                CompileArgs.AddRange(CommonArgs);
                AppendArgs_CPP(Environment, CompileArgs);
                CompileArgs.Add($"/Yc\"{pchSource}\"");
                CompileArgs.Add($"/Fp\"{pchFile}\"");
                CompileArgs.Add($"/Fo\"{pchObjFile}\"");
                CompileArgs.AddRange(dependencyArgument);
                CompileArgs.Add($"\"{pchSourcePath}\"");
                string response = CommandUtilities.WriteResponseFile(pchFile, string.Join("\n", CompileArgs));
                Executor.AddCommand(new CommandContext(CompilerPath!, $@"@""{response}""", CommandType.GeneratePCH,
                    [pchFile, pchObjFile, dependencyFile], new[] { pchSource, pchSourcePath, response, typeof(WindowsPlatform).Assembly.Location }
                        .Concat(Environment.AdditionalPrerequisites).ToList(), true, $"Creating PCH {Path.GetFileName(pchSource)} for Windows...",
                    dependencyFile: dependencyFile, dependencyFormat: dependencyFormat,
                    searchDirectories: Environment.IncludePaths.Concat(Environment.ExternalIncludePaths).Concat(SystemIncludePaths).Append(Path.GetDirectoryName(pchSource)!), allowArtifactCache: false));
            }
            foreach (var sourceFile in sourceFiles)
            {
                List<string> PrerequisiteFiles = new List<string>();
                List<string> ProducedFiles = new List<string>();

                //var Headers = GetHeaderDependencies(sourceFile);
                var sourceId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceFile))))[..12];
                var sourceFilename = Path.GetFileNameWithoutExtension(sourceFile) + "_" + sourceId;
                var objFile = Path.Combine(outputPath, sourceFilename + ".obj");
                
                Output.ObjectFiles.Add(objFile);
                ProducedFiles.Add(objFile);
                if (Path.GetExtension(sourceFile).Equals(".asm", StringComparison.OrdinalIgnoreCase))
                {
                    AddAssemblerCommand(Executor, Environment, sourceFile, objFile);
                    continue;
                }


                CompileArgs.Clear();
                bool IsCFile = Path.GetExtension(sourceFile).Equals(".c", StringComparison.OrdinalIgnoreCase);
                if (IsCFile)
                {
                    AppendArgs_C(Environment, CompileArgs);
                }
                else
                {

                    AppendArgs_CPP(Environment, CompileArgs);

                }
                CompileArgs.AddRange(CommonArgs);

                bool usesPch = pchFile != null && ModuleCompilation.UsesPch(Environment, sourceFile);
                if (usesPch)
                {
                    CompileArgs.Add($"/FI\"{pchSource}\"");
                    CompileArgs.Add($"/Yu\"{pchSource}\"");
                    CompileArgs.Add($"/Fp\"{pchFile}\"");
                }
                // C++20 modules: interface (/interface) or internal partition units write an IFC;
                // every unit references the IFCs of what it imports, transitively.
                if (Environment.ModuleUnits.GetValueOrDefault(sourceFile) is { } moduleUnit)
                {
                    if (moduleUnit.InterfaceOutput != null)
                    {
                        CompileArgs.Add(moduleUnit.Info.Exported ? "/interface" : "/internalPartition");
                        CompileArgs.Add($"/ifcOutput \"{moduleUnit.InterfaceOutput}\"");
                        ProducedFiles.Add(moduleUnit.InterfaceOutput);
                    }
                    foreach (var (name, path) in moduleUnit.References) CompileArgs.Add($"/reference \"{name}={path}\"");
                }


                CompileArgs.Add(string.Format("/Fo\"{0}\"", objFile));
                CompileArgs.Add("\"" + sourceFile + "\"");

                PrerequisiteFiles.Add(sourceFile);
                var (dependencyFile, dependencyFormat, dependencyArgument) = Dependencies(objFile);
                CompileArgs.AddRange(dependencyArgument);
                ProducedFiles.Add(dependencyFile);

                if (usesPch)
                {
                    PrerequisiteFiles.Add(pchFile!);
                }
                PrerequisiteFiles.AddRange(Environment.SourcePrerequisites.GetValueOrDefault(sourceFile) ?? []);
                PrerequisiteFiles.Add(typeof(WindowsPlatform).Assembly.Location);
                
                PrerequisiteFiles.AddRange(Environment.AdditionalPrerequisites);
                
                // Use response file if command line is too long (Windows limit is ~32767 chars)
                string compilerArgs;
                if (string.Join(" ", CompileArgs).Length > 8000)
                {
                    var responseFile = Path.Combine(outputPath, sourceFilename + ".rsp");
                    var newContent = string.Join(System.Environment.NewLine, CompileArgs);
                    // Only rewrite if content changed to avoid unnecessary recompilation
                    if (!File.Exists(responseFile) || File.ReadAllText(responseFile) != newContent)
                        File.WriteAllText(responseFile, newContent);
                    compilerArgs = $"@\"{responseFile}\"";
                    PrerequisiteFiles.Add(responseFile);
                }
                else
                {
                    compilerArgs = string.Join(" ", CompileArgs);
                }
                
                Executor.AddCommand(new CommandContext(CompilerPath!, compilerArgs, CommandType.Compile,
                    ProducedFiles, PrerequisiteFiles, true, $"Compiling {Path.GetFileName(sourceFile)} ..",
                    dependencyFile: dependencyFile, dependencyFormat: dependencyFormat,
                    searchDirectories: Environment.IncludePaths.Concat(Environment.ExternalIncludePaths).Concat(SystemIncludePaths).Append(Path.GetDirectoryName(sourceFile)!),
                    allowArtifactCache: !usesPch));
            
            
            }
            
            
            return Output;
        }

        /// <summary>Compiler-reported includes: MSVC /sourceDependencies JSON, clang-cl Makefile depfile.</summary>
        private (string File, DependencyFormat Format, string[] Arguments) Dependencies(string output)
        {
            if (!UsesClangCl)
            {
                string json = output + ".deps.json";
                return (json, DependencyFormat.MsvcJson, [$"/sourceDependencies \"{json}\""]);
            }
            string depfile = output + ".d";
            return (depfile, DependencyFormat.Makefile, ["/clang:-MD", CommandUtilities.QuoteArgument("/clang:-MF" + depfile)]);
        }

        /// <summary>
        /// .asm sources: ml64 (x64) or armasm64 (ARM64). The assemblers report no include dependencies,
        /// so only the source itself is tracked, and results are not shared through the compile cache.
        /// </summary>
        private void AddAssemblerCommand(CommandExecutor executor, CPPEnvironment environment, string source, string objFile)
        {
            var args = new List<string>();
            if (Architecture == PlatformArch.ARM64)
            {
                args.Add("-nologo");
                if (environment.DebugInformation) args.Add("-g");
                foreach (string include in environment.IncludePaths.Concat(environment.ExternalIncludePaths).Where(Directory.Exists)) args.Add("-i " + CommandUtilities.QuoteArgument(include));
                args.Add("-o " + CommandUtilities.QuoteArgument(objFile));
                args.Add(CommandUtilities.QuoteArgument(source));
            }
            else
            {
                args.AddRange(["/nologo", "/c", environment.Warnings == WarningLevel.Off ? "/W0" : "/W3"]);
                if (environment.TreatWarningsAsErrors) args.Add("/WX");
                if (environment.DebugInformation) args.Add("/Zi");
                // Plain NAME or NAME=value definitions only: MASM text macros cannot hold C syntax.
                foreach (string define in environment.PreprocessorDefinitions.Where(d => System.Text.RegularExpressions.Regex.IsMatch(d, @"^[A-Za-z_]\w*(=[\w.]*)?$")))
                    args.Add("/D" + define);
                foreach (string include in environment.IncludePaths.Concat(environment.ExternalIncludePaths).Where(Directory.Exists)) args.Add("/I " + CommandUtilities.QuoteArgument(include));
                args.Add("/Fo" + CommandUtilities.QuoteArgument(objFile));
                args.Add("/Ta" + CommandUtilities.QuoteArgument(source));
            }
            executor.AddCommand(new CommandContext(AssemblerPath, string.Join(" ", args), CommandType.Compile, [objFile],
                [source, AssemblerPath, typeof(WindowsPlatform).Assembly.Location], true, $"Assembling {Path.GetFileName(source)} ..",
                allowArtifactCache: false));
        }

        public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string args = "/NOLOGO " + (LinkTimeOptimization && !UsesClangCl ? "/LTCG " : "") + "/OUT:\"" + outputPath + "\" " + string.Join(" ", objects.Select(x => "\"" + x + "\""));
            string response = CommandUtilities.WriteResponseFile(outputPath, args);
            executor.AddCommand(new CommandContext(ActiveLibrarian, $"@\"{response}\"", CommandType.Link,
                new List<string> { outputPath }, objects.Append(response).ToList(), false, $"Archiving {Path.GetFileName(outputPath)}...", recreateOutputs: true));
        }

        public override void LinkFiles(CommandExecutor Executor, LinkEnvironment LinkEnv, string outputFilePath)
        {

            List<string> PrerequisiteFiles = new List<string>();
            List<string> ProducedFiles = new List<string>();

            // Add all input .obj files as prerequisites so linker re-runs when any .obj is recompiled
            foreach (var inputFile in LinkEnv.InputFiles)
            {
                PrerequisiteFiles.Add(inputFile);
            }

            var args = new List<string>();

                string ExtentionFile = "";

                args.AddRange(LinkEnv.CustomArgs);
                {
                    // Suppress startup banner
                    args.Add("/NOLOGO");

                    // Report internal compiler errors
                    args.Add("/ERRORREPORT:PROMPT");

                    // Output File Name

                    if (LinkEnv.Output == LinkerOutput.Executable)
                        ExtentionFile = ".exe";
                    else if (LinkEnv.Output == LinkerOutput.SharedLibrary)
                        ExtentionFile = ".dll";
                    else if (LinkEnv.Output == LinkerOutput.StaticLibrary)
                        ExtentionFile = ".lib";


                    args.Add(string.Format("/OUT:\"{0}\"", outputFilePath + ExtentionFile));

                    // Specify target platform
                    switch (Architecture)
                    {
                        case PlatformArch.X64:
                            args.Add("/MACHINE:x64");
                            break;
                        case PlatformArch.ARM64:
                            args.Add("/MACHINE:ARM64");
                            break;
                        default: throw new InvalidArchitectureException(Architecture);
                    }

                    // Specify subsystem
                    if (LinkEnv.LinkAsConsoleProgram)
                    {
                        args.Add("/SUBSYSTEM:CONSOLE");
                    }
                    else
                    {
                        args.Add("/SUBSYSTEM:WINDOWS");
                    }


                    if (LinkEnv.LinkTimeCodeGeneration)
                    {
                        // Link-time code generation
                        args.Add("/LTCG");
                    }

                    {
                        // Don't create Side-by-Side Assembly Manifest
                        args.Add("/MANIFEST:NO");

                        // Fixed Base Address
                        args.Add("/FIXED:NO");

                        // Compatible with Data Execution Prevention
                        args.Add("/NXCOMPAT");

                        // Allow delay-loaded DLLs to be explicitly unloaded
                        args.Add("/DELAY:UNLOAD");

                        if (LinkEnv.Output == LinkerOutput.SharedLibrary)
                        {
                            // Build a DLL
                            args.Add("/DLL");
                        }

                        // Redirect imports LIB file auto-generated for EXE/DLL
                        var libFile = Path.ChangeExtension(outputFilePath, StaticLibraryFileExtension);
                        args.Add("/IMPLIB:\"" + libFile + "\"");

                        // Don't embed the full PDB path
                        args.Add("/PDBALTPATH:%_PDB%");

                        // Optimize
                        if (LinkEnv.Optimization && !LinkEnv.UseIncrementalLinking)
                        {
                            // Generate an EXE checksum
                            args.Add("/RELEASE");

                            // Eliminate unreferenced symbols
                            args.Add("/OPT:REF");

                            // Remove redundant COMDATs
                            args.Add("/OPT:ICF");
                        }
                        else
                        {
                            // Keep symbols that are unreferenced
                            args.Add("/OPT:NOREF");

                            // Disable identical COMDAT folding
                            args.Add("/OPT:NOICF");
                        }

                        // Link Incrementally
                        if (LinkEnv.UseIncrementalLinking && !LinkEnv.LinkTimeCodeGeneration)
                        {
                            args.Add("/INCREMENTAL");
                        }
                        else
                        {
                            args.Add("/INCREMENTAL:NO");
                        }

                        if (LinkEnv.DebugInformation)
                        {
                            // Generate debug information
                            if (LinkEnv.UseFastPDBLinking)
                            {
                                args.Add("/DEBUG:FASTLINK");
                            }
                            else if (LinkEnv.UseFullDebugInformation)
                            {
                                args.Add("/DEBUG:FULL");
                            }
                            else
                            {
                                args.Add("/DEBUG"); // Same as /DEBUG:FULL
                            }

                            // Use Program Database
                            var pdbFile = Path.ChangeExtension(outputFilePath, ".pdb");
                            args.Add(string.Format("/PDB:\"{0}\"", pdbFile));
                            ProducedFiles.Add(pdbFile);
                        }
                    }


                }

                if (UsesClangCl && LinkEnv.Sanitizers.HasFlag(Sanitizer.Address))
                    args.AddRange(ClangAddressSanitizerLinkArguments());

                // Delay-load DLLs
                if (LinkEnv.Output == LinkerOutput.Executable || LinkEnv.Output == LinkerOutput.SharedLibrary)
                {
                    foreach (var dll in LinkEnv.DelayLoadLibraries)
                        args.Add(string.Format("/DELAYLOAD:\"{0}\"", dll));
                }

                // Additional lib paths
                foreach (var libpath in SystemLibraryPaths)
                {
                    args.Add(string.Format("/LIBPATH:\"{0}\"", libpath));
                }
                foreach (var libpath in LinkEnv.LibraryPaths)
                {
                    args.Add(string.Format("/LIBPATH:\"{0}\"", libpath));
                }

                // Input libraries
                foreach (var library in LinkEnv.InputLibraries)
                {
                    args.Add(string.Format("\"{0}\"", library));
                    if (File.Exists(library) || Path.IsPathFullyQualified(library)) PrerequisiteFiles.Add(library);
                }

                // Input files
                foreach (var file in LinkEnv.InputFiles)
                {
                    if (file.EndsWith(".pch", StringComparison.OrdinalIgnoreCase))
                    {
                        // PCH file
                        args.Add(string.Format("/Yu:\"{0}\"", file));
                        continue;
                    }

                    args.Add(string.Format("\"{0}\"", file));
                }
                bool useResponseFile = true;
                string? responseFile = null;
                if (useResponseFile)
                {
                    string intermediateDir = (GlobalEnvironment.IsProjectBuild && !string.IsNullOrEmpty(GlobalEnvironment.ProjectPath))
                        ? Path.Combine(GlobalEnvironment.ProjectPath, "Intermediate", "Build", "Link")
                        : Path.Combine(GlobalEnvironment.IntermediatePath, "Build", "Link");
                    if (!Directory.Exists(intermediateDir))
                        Directory.CreateDirectory(intermediateDir);
                    // Different configurations/targets must not overwrite each other's link arguments.
                    string outputId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(Path.GetFullPath(outputFilePath))))[..12];
                    responseFile = Path.Combine(intermediateDir, Path.GetFileName(outputFilePath) + "_" + outputId + ".response");
                    var newContent = string.Join(Environment.NewLine, args);
                    // Only rewrite if content changed to avoid unnecessary relinking
                    if (!File.Exists(responseFile) || File.ReadAllText(responseFile, new UTF8Encoding()) != newContent)
                        File.WriteAllText(responseFile, newContent, new UTF8Encoding());
                    PrerequisiteFiles.Add(responseFile);
            }
            ProducedFiles.Add(outputFilePath + ExtentionFile);
            Executor.AddCommand(new CommandContext(ActiveLinker(LinkEnv.LinkTimeCodeGeneration),
                            useResponseFile ? string.Format("@\"{0}\"", responseFile) : string.Join(" ", args),
                            CommandType.Link,
                            ProducedFiles, 
                            PrerequisiteFiles,
                            false,
                            $"Linking files for {Path.GetFileName(outputFilePath) + ExtentionFile}...",
                            contentDirectories: SystemLibraryPaths.Concat(LinkEnv.LibraryPaths)));
            
        }

        protected void AddIncludePath(List<string> Args, string Path, bool bIsSystemInclude)
        {
            string Prefix = bIsSystemInclude ? "/external:I " : "/I ";
            if (!Args.Contains("/external:W0"))
                Args.Add("/external:W0");

            if (Path.Contains(' '))
                Args.Add(string.Format("{0}\"{1}\"", Prefix, Path));
            else
                Args.Add(string.Format("{0}{1}", Prefix, Path));
        }



        public static bool TryGetLatestWindowsSdkDir(out string? sdkDir, out List<Version>? sdkVersion)
        {
            sdkDir = null;
            sdkVersion = null;

            const string registryKeyPath = @"Microsoft\\Windows Kits\\Installed Roots";
            const string registryValueName = "KitsRoot10";

            if (TryReadInstallDirRegistryKey32(registryKeyPath, registryValueName, out sdkDir))
            {
                if (Directory.Exists(sdkDir))
                {
                    var includeRootDir = Path.Combine(sdkDir, "Include");
                    sdkVersion = new List<Version>();
                    foreach (var includeDir in Directory.GetDirectories(includeRootDir))
                    {
                        if (Version.TryParse(Path.GetFileName(includeDir), out var version) && File.Exists(Path.Combine(includeDir, "um", "windows.h")))
                        {
                            sdkVersion.Add(version);
                        }
                    }
                    return true;
                }
            }
            return false;
        }
        public static bool TryReadDirRegistryKey(string keyName, string valueName, out string? value)
        {
            if (!OperatingSystem.IsWindows())
            {
                value = null;
                return false;
            }

            value = Registry.GetValue(keyName, valueName, null) as string;
            if (string.IsNullOrEmpty(value))
            {
                value = null;
                return false;
            }
            return true;
        }
        public static bool TryReadInstallDirRegistryKey32(string keySuffix, string valueName, out string? dir)
        {
            if (TryReadDirRegistryKey("HKEY_CURRENT_USER\\SOFTWARE\\" + keySuffix, valueName, out dir))
                return true;
            if (TryReadDirRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keySuffix, valueName, out dir))
                return true;
            if (TryReadDirRegistryKey("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keySuffix, valueName, out dir))
                return true;
            if (TryReadDirRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keySuffix, valueName, out dir))
                return true;
            return false;
        }
    }

}

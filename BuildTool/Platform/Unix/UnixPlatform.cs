using System.Security.Cryptography;
using System.Text;

namespace Beast.BuildTool;

/// <summary>
/// Common Clang/GCC backend of the desktop Unix platforms (Linux, macOS): executables, static
/// archives and shared libraries that find their dependencies through an rpath beside them.
/// </summary>
public abstract class UnixPlatform : Platform
{
    protected UnixPlatform(PlatformArch architecture) => _architecture = architecture;
    private readonly PlatformArch _architecture;
    public override PlatformArch Architecture => _architecture;
    public override string StaticLibraryFileExtension => ".a";

    /// <summary>C++ driver used for C, C++, Objective-C and assembly sources and for linking.</summary>
    public abstract string Compiler { get; }
    /// <summary>Static library archiver (ar, llvm-ar, gcc-ar or libtool).</summary>
    protected abstract string Archiver { get; }
    /// <summary>True for Clang (Apple clang included), false for GCC.</summary>
    public abstract bool IsClang { get; }
    /// <summary>Platform macro, e.g. PLATFORM_LINUX.</summary>
    protected abstract IEnumerable<string> PlatformDefinitions { get; }
    /// <summary>Target selection: --target, -arch, --sysroot, -isysroot, deployment target...</summary>
    protected abstract IEnumerable<string> TargetArguments { get; }
    /// <summary>Short name in progress messages ("Linux", "macOS arm64").</summary>
    protected abstract string Label { get; }
    /// <summary>Linker arguments of an executable or shared library (rpath, soname/install_name...).</summary>
    protected abstract IEnumerable<string> PlatformLinkArguments(LinkEnvironment environment, string output);
    /// <summary>Directories whose files can satisfy -l (their inventory is part of the link key).</summary>
    protected virtual IEnumerable<string> LibraryContentDirectories => [];

    public override bool IsValid() => File.Exists(Compiler);
    public override string? ModuleInterfaceExtension => IsClang ? ".pcm" : ".gcm";

    public override void SetupCPPEnvironment(CPPEnvironment environment)
    {
        foreach (string definition in PlatformDefinitions) environment.PreprocessorDefinitions.Add(definition);
    }

    public override CPPOutput CompileCppFiles(CommandExecutor executor, CPPEnvironment environment, List<string> sourceFiles, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var output = new CPPOutput();
        bool hasPch = sourceFiles.Any(s => ModuleCompilation.UsesPch(environment, s));
        if (hasPch && environment.PrecompiledHeaderUsage != PrecompiledHeaderFileUsage.CreateManual)
            throw new InvalidOperationException("UseManual PCH is unsupported; let the module create its PCH.");
        // Clang: an explicit .pch with -include-pch. GCC finds <header>.gch beside the header named by
        // -include, so it precompiles a generated wrapper header (also avoiding "#pragma once in main file").
        string? pch = null, pchInclude = null, pchSource = null;
        if (hasPch)
        {
            if (IsClang) pch = Path.Combine(outputPath, "BeastPch.pch");
            else
            {
                pchInclude = Path.Combine(outputPath, "BeastPch.h");
                pch = pchInclude + ".gch";
                ModuleCompilation.WriteIfChanged(pchInclude, $"#include \"{environment.PrecompiledHeaderSource.Replace('\\', '/')}\"\n");
            }
            pchSource = IsClang ? environment.PrecompiledHeaderSource : pchInclude;
        }
        output.PrecompiledHeaderFile = pch;
        var compileSources = hasPch ? sourceFiles.Prepend(environment.PrecompiledHeaderSource) : sourceFiles;
        var searchDirectories = environment.IncludePaths.Concat(environment.ExternalIncludePaths).Concat(SystemIncludePaths).ToList();
        foreach (string source in compileSources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool creatingPch = hasPch && source == environment.PrecompiledHeaderSource;
            bool usesPch = !creatingPch && hasPch && ModuleCompilation.UsesPch(environment, source);
            string sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(GlobalEnvironment.WorkspacePath, source))))[..12];
            string objectFile = creatingPch ? pch! : Path.Combine(outputPath, Path.GetFileNameWithoutExtension(source) + "_" + sourceId + ".o");
            if (!creatingPch) output.ObjectFiles.Add(objectFile);
            var args = new List<string> { "-c", "-fPIC", "-ffunction-sections", "-fdata-sections" };
            args.AddRange(TargetArguments);
            args.Add(!environment.Optimization ? "-O0" : environment.FavorSizeOrSpeed switch
            {
                FavorSizeOrSpeed.SmallCode => "-Os", FavorSizeOrSpeed.FastCode => "-O3", _ => "-O2"
            });
            if (environment.DebugInformation) args.Add("-g");
            string extension = Path.GetExtension(source);
            string lower = extension.ToLowerInvariant();
            // .S is preprocessed (includes, macros, depfile); .s is passed to the assembler as is.
            bool isAssembly = lower == ".s";
            bool hasDependencies = !isAssembly || extension == ".S";
            bool objectiveC = lower is ".m" or ".mm";
            if (creatingPch) args.AddRange(["-x", "c++-header"]);
            else if (isAssembly) args.AddRange(["-x", extension == ".S" ? "assembler-with-cpp" : "assembler"]);
            else if (lower == ".c") args.AddRange(["-x", "c", environment.CStandard == CLanguageStandard.C17 ? "-std=c17" : "-std=c11"]);
            else if (lower == ".m") args.AddRange(["-x", "objective-c", environment.CStandard == CLanguageStandard.C17 ? "-std=c17" : "-std=c11", "-fobjc-arc"]);
            if (!isAssembly && lower is not ".c" and not ".m")
            {
                if (lower == ".mm") args.AddRange(["-x", "objective-c++", "-fobjc-arc"]);
                args.Add(CppStandard(environment.CppVersion));
                args.Add(environment.EnableExceptions ? "-fexceptions" : "-fno-exceptions");
                args.Add(environment.RuntimeTypeInfo ? "-frtti" : "-fno-rtti");
            }
            if (!isAssembly) AddLanguageOptions(environment, args, creatingPch);
            if (creatingPch && IsClang) args.AddRange(["-Xclang", "-fno-pch-timestamp"]);
            if (usesPch) args.Add(IsClang ? $"-include-pch \"{pch}\"" : $"-Winvalid-pch -include \"{pchInclude}\"");
            foreach (string define in environment.PreprocessorDefinitions) args.Add("-D" + CommandUtilities.QuoteArgument(define));
            foreach (string include in environment.IncludePaths.Concat(SystemIncludePaths).Where(Directory.Exists)) args.Add("-I\"" + include + "\"");
            foreach (string include in environment.ExternalIncludePaths.Where(Directory.Exists)) args.Add("-isystem \"" + include + "\"");
            args.AddRange(environment.CustomArgs);
            string dependencyFile = objectFile + ".d";
            if (hasDependencies) args.Add("-MD -MF \"" + dependencyFile + "\"");
            var produced = hasDependencies ? new List<string> { objectFile, dependencyFile } : [objectFile];
            var moduleUnit = environment.ModuleUnits.GetValueOrDefault(source);
            string? mapper = null;
            if (moduleUnit != null && IsClang) CppModules.AddClangArguments(source, moduleUnit, args, produced);
            else if (moduleUnit != null)
            {
                // GCC reads module name -> BMI pairs from a mapper file; it writes the unit's own BMI there too.
                mapper = objectFile + ".modmap";
                var lines = moduleUnit.References.Select(r => r.Name + " " + r.Path).ToList();
                if (moduleUnit.InterfaceOutput != null)
                {
                    lines.Insert(0, moduleUnit.Info.FullName + " " + moduleUnit.InterfaceOutput);
                    produced.Add(moduleUnit.InterfaceOutput);
                }
                ModuleCompilation.WriteIfChanged(mapper, string.Join("\n", lines) + "\n");
                args.Add("-fmodules-ts");
                args.Add($"-fmodule-mapper=\"{mapper}\"");
                if (CppModules.IsInterfaceExtension(source)) args.AddRange(["-x", "c++"]);
            }
            args.Add("-o \"" + objectFile + "\"");
            args.Add("\"" + (creatingPch ? pchSource : source) + "\"");
            var prerequisites = new List<string> { source, GetType().Assembly.Location };
            if (creatingPch && pchInclude != null) prerequisites.Add(pchInclude);
            if (mapper != null) prerequisites.Add(mapper);
            prerequisites.AddRange(environment.AdditionalPrerequisites);
            prerequisites.AddRange(environment.SourcePrerequisites.GetValueOrDefault(source) ?? []);
            if (usesPch) prerequisites.Add(pch!);
            executor.AddCommand(new CommandContext(Compiler, string.Join(" ", args), creatingPch ? CommandType.GeneratePCH : CommandType.Compile,
                produced, prerequisites, true,
                creatingPch ? $"Creating PCH {Path.GetFileName(source)} for {Label}..." : $"Compiling {Path.GetFileName(source)} for {Label}...",
                dependencyFile: hasDependencies ? dependencyFile : null, dependencyFormat: hasDependencies ? DependencyFormat.Makefile : DependencyFormat.None,
                searchDirectories: hasDependencies ? searchDirectories.Append(Path.GetDirectoryName(source)!) : [],
                allowArtifactCache: !creatingPch && !usesPch));
        }
        return output;
    }

    protected virtual string CppStandard(CppVersion version) => version switch
    {
        CppVersion.Cpp14 => "-std=c++14", CppVersion.Cpp17 => "-std=c++17", CppVersion.Latest => "-std=c++2b", _ => "-std=c++20"
    };

    protected void AddLanguageOptions(CPPEnvironment environment, List<string> args, bool creatingPch)
    {
        args.AddRange(environment.Warnings switch
        {
            WarningLevel.Off => ["-w"],
            WarningLevel.High => ["-Wall", "-Wextra"],
            WarningLevel.Pedantic => ["-Wall", "-Wextra", "-Wpedantic"],
            _ => Array.Empty<string>()
        });
        if (environment.TreatWarningsAsErrors) args.Add("-Werror");
        if (environment.Sanitizers != Sanitizer.None)
        {
            args.Add("-fsanitize=" + SanitizerSupport.ClangList(environment.Sanitizers));
            args.Add("-fno-omit-frame-pointer");
        }
        if (environment.WholeProgramOptimization && environment.Optimization && !creatingPch) args.Add(IsClang ? "-flto=thin" : "-flto=auto");
        if (environment.FloatingPoint == FloatingPointModel.Fast) args.Add("-ffast-math");
        else if (environment.FloatingPoint == FloatingPointModel.Strict) args.AddRange(IsClang ? ["-ffp-model=strict"] : ["-ffp-contract=off", "-frounding-math", "-fsignaling-nans"]);
    }

    public override void LinkFiles(CommandExecutor executor, LinkEnvironment environment, string outputFilePath)
    {
        bool shared = environment.Output == LinkerOutput.SharedLibrary;
        string output = shared && !outputFilePath.EndsWith(SharedLibraryFileExtension, StringComparison.Ordinal) ? outputFilePath + SharedLibraryFileExtension : outputFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var args = new List<string>();
        if (shared) args.Add(SharedLibraryFlag);
        args.AddRange(TargetArguments);
        args.Add("-o \"" + output + "\"");
        args.AddRange(PlatformLinkArguments(environment, output));
        if (environment.Sanitizers != Sanitizer.None) args.Add("-fsanitize=" + SanitizerSupport.ClangList(environment.Sanitizers));
        if (environment.LinkTimeCodeGeneration) args.AddRange(LinkTimeOptimizationArguments());
        args.AddRange(environment.CustomArgs);
        args.AddRange(environment.InputFiles.Select(x => "\"" + x + "\""));
        foreach (string path in environment.LibraryPaths) args.Add("-L\"" + path + "\"");
        // Library files (archives of dependency targets and packages) before -l names, so that
        // their references to system libraries resolve in a single pass of GNU ld.
        var files = environment.InputLibraries.Where(IsLibraryFile).ToList();
        if (files.Count > 0) args.AddRange(GroupLibraries(files.Select(f => "\"" + f + "\"")));
        foreach (string library in environment.InputLibraries.Where(l => !IsLibraryFile(l)))
            args.Add(library.StartsWith('-') ? library : "-l" + library);
        executor.AddCommand(new CommandContext(Compiler, string.Join(" ", args), CommandType.Link, [output],
            environment.InputFiles.Concat(files.Where(p => File.Exists(p) || Path.IsPathFullyQualified(p))).ToList(), false, $"Linking {Path.GetFileName(output)}...",
            contentDirectories: environment.LibraryPaths.Concat(LibraryContentDirectories), statePath: LinkStatePath(output)));
    }

    /// <summary>Where the link action keeps its state; null: beside the output.</summary>
    protected virtual string? LinkStatePath(string output) => null;

    protected virtual string SharedLibraryFlag => "-shared";
    protected static bool IsLibraryFile(string library) => library.IndexOfAny(['/', '\\']) >= 0;
    protected virtual IEnumerable<string> GroupLibraries(IEnumerable<string> files) => files;
    protected virtual IEnumerable<string> LinkTimeOptimizationArguments() => [IsClang ? "-flto=thin" : "-flto=auto"];

    public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // The operation stays on the command line: gcc-ar inserts --plugin before the arguments
        // and does not accept it from the response file. recreateOutputs drops members of removed sources.
        string response = CommandUtilities.WriteResponseFile(outputPath, string.Join(" ", objects.Select(x => "\"" + x + "\"")));
        executor.AddCommand(new CommandContext(Archiver, $"rcs \"{outputPath}\" @\"{response}\"", CommandType.Link, [outputPath], objects.Append(response).ToList(), false,
            $"Archiving {Path.GetFileName(outputPath)}...", recreateOutputs: true));
    }

    /// <summary>Finds an executable on PATH (or returns null).</summary>
    internal static string? FindOnPath(string name)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? name : null;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

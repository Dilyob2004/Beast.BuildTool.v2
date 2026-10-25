using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>Compiler warning level, mapped to the closest option of each compiler.</summary>
public enum WarningLevel
{
    /// <summary>Compiler default: MSVC /W3 (C files /W0, as before), Clang without extra -W flags.</summary>
    Default,
    /// <summary>No warnings: MSVC /W0, Clang -w.</summary>
    Off,
    /// <summary>MSVC /W4, Clang -Wall -Wextra.</summary>
    High,
    /// <summary>High plus pedantic checks: MSVC /W4 /permissive-, Clang -Wall -Wextra -Wpedantic.</summary>
    Pedantic
}

public enum FloatingPointModel
{
    /// <summary>MSVC /fp:precise, Clang default. This is the default model.</summary>
    Precise,
    /// <summary>MSVC /fp:fast, Clang -ffast-math. May change results; opt in per module.</summary>
    Fast,
    /// <summary>MSVC /fp:strict, Clang -ffp-model=strict.</summary>
    Strict
}

public enum CLanguageStandard
{
    /// <summary>MSVC: compiler default; Clang/NDK: C11 (as before).</summary>
    Default,
    C11,
    C17
}

/// <summary>
/// Per-target defaults (<see cref="TargetRules.CompileOptions"/>) and per-module overrides
/// (<see cref="ModuleRules.CompileOptions"/>). An unset (null) value inherits the target value,
/// then the built-in default. <see cref="Arguments"/> from both levels are appended in order.
/// </summary>
public sealed class CompileOptions
{
    public CppVersion? CppStandard;
    public CLanguageStandard? CStandard;
    public WarningLevel? Warnings;
    public bool? WarningsAsErrors;
    public bool? Exceptions;
    public bool? RTTI;
    public FloatingPointModel? FloatingPoint;
    /// <summary>Optimized configurations: SmallCode = MSVC /Os, Clang -Os; FastCode = MSVC /Ot, Clang -O3.</summary>
    public FavorSizeOrSpeed? Favor;

    /// <summary>Raw compiler arguments appended after Beast's own options. Each item is one argument.</summary>
    public List<string> Arguments { get; } = [];

    internal static void Apply(CPPEnvironment environment, CompileOptions target, CompileOptions module)
    {
        environment.CppVersion = module.CppStandard ?? target.CppStandard ?? environment.CppVersion;
        environment.CStandard = module.CStandard ?? target.CStandard ?? CLanguageStandard.Default;
        environment.Warnings = module.Warnings ?? target.Warnings ?? WarningLevel.Default;
        environment.TreatWarningsAsErrors = module.WarningsAsErrors ?? target.WarningsAsErrors ?? environment.TreatWarningsAsErrors;
        environment.EnableExceptions = module.Exceptions ?? target.Exceptions ?? environment.EnableExceptions;
        environment.RuntimeTypeInfo = module.RTTI ?? target.RTTI ?? environment.RuntimeTypeInfo;
        environment.FloatingPoint = module.FloatingPoint ?? target.FloatingPoint ?? FloatingPointModel.Precise;
        environment.FavorSizeOrSpeed = module.Favor ?? target.Favor ?? FavorSizeOrSpeed.Neither;
        environment.CustomArgs.AddRange(target.Arguments);
        environment.CustomArgs.AddRange(module.Arguments);
    }
}

public static class SanitizerSupport
{
    public static Sanitizer Validate(PlatformType platform, Sanitizer sanitizers, string target)
    {
        Sanitizer supported = platform switch
        {
            PlatformType.Windows => Sanitizer.Address,
            PlatformType.Android or PlatformType.IOS => Sanitizer.Address | Sanitizer.Undefined,
            _ => Sanitizer.Address | Sanitizer.Undefined | Sanitizer.Thread
        };
        if (sanitizers.HasFlag(Sanitizer.Address) && sanitizers.HasFlag(Sanitizer.Thread))
            throw new InvalidOperationException($"Target '{target}': Address and Thread sanitizers cannot be combined.");
        Sanitizer rejected = sanitizers & ~supported;
        if (rejected != Sanitizer.None)
            throw new InvalidOperationException($"Target '{target}': sanitizer {rejected} is not supported for {platform}. Supported: {supported}.");
        return sanitizers;
    }

    /// <summary>Clang -fsanitize value (Android/clang-cl).</summary>
    public static string ClangList(Sanitizer sanitizers) => string.Join(',', new[]
    {
        sanitizers.HasFlag(Sanitizer.Address) ? "address" : null,
        sanitizers.HasFlag(Sanitizer.Undefined) ? "undefined" : null,
        sanitizers.HasFlag(Sanitizer.Thread) ? "thread" : null,
    }.OfType<string>());
}

/// <summary>Decides which files under a module directory are compiled for the current platform.</summary>
public static class SourceFilter
{
    // Directory names that are compiled only for their own platform. Names of platforms
    // Beast does not build yet are reserved so that such code is never compiled by accident.
    private static readonly Dictionary<string, PlatformType?> PlatformDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows"] = PlatformType.Windows, ["Win64"] = PlatformType.Windows, ["Win32"] = PlatformType.Windows,
        ["Android"] = PlatformType.Android,
        ["Linux"] = PlatformType.Linux,
        ["Mac"] = PlatformType.Mac, ["MacOS"] = PlatformType.Mac, ["OSX"] = PlatformType.Mac,
        ["IOS"] = PlatformType.IOS,
    };

    public static IReadOnlyCollection<string> PlatformDirectoryNames => PlatformDirectories.Keys;

    /// <summary>C/C++ translation units, Objective-C(++) (.m/.mm, macOS) and assembly sources (.asm: MASM/armasm64, .S/.s: Clang/GCC).</summary>
    public static bool IsCompilable(string path) => Path.GetExtension(path).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".asm" or ".s" or ".m" or ".mm" or ".ixx" or ".cppm";
    /// <summary>Objective-C/C++ sources are compiled on their own: no Unity batching, no C++ PCH.</summary>
    public static bool IsObjectiveC(string path) => Path.GetExtension(path).ToLowerInvariant() is ".m" or ".mm";
    public static bool IsAssembly(string path) => Path.GetExtension(path).ToLowerInvariant() is ".asm" or ".s";

    /// <summary>
    /// Assembly and Objective-C files are compiled only by the toolchains that understand them:
    /// .asm by MSVC tools (Windows), .S/.s by Clang/GCC (Android, Linux, macOS), .m/.mm on macOS.
    /// </summary>
    public static bool IsCompiledFor(string path, PlatformType platform) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".asm" => platform == PlatformType.Windows,
        ".s" => platform != PlatformType.Windows,
        ".m" or ".mm" => platform is PlatformType.Mac or PlatformType.IOS,
        _ => true
    };

    /// <summary>True when a directory segment of the module-relative path belongs to another platform.</summary>
    public static bool IsOtherPlatform(string relativePath, PlatformType platform)
    {
        string[] segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The last segment is the file name; only directories select a platform.
        for (int i = 0; i < segments.Length - 1; ++i)
            if (PlatformDirectories.TryGetValue(segments[i], out var owner) && owner != platform) return true;
        return false;
    }

    /// <summary>
    /// Compiles ExcludedSources entries. An entry is a module-relative file, a directory (everything
    /// below it), or a pattern with *, ? and ** (for example "Private/**/Test*.cpp").
    /// </summary>
    public static Func<string, bool> CreateExclusion(ModuleRules module)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        var patterns = new List<Regex>();
        foreach (string raw in module.ExcludedSources)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string entry = raw.Replace('\\', '/').Trim();
            if (entry.IndexOfAny(['*', '?']) >= 0)
            {
                if (Path.IsPathRooted(entry)) throw new InvalidOperationException($"{module.ModuleName}: ExcludedSources pattern must be module-relative: {raw}");
                patterns.Add(new Regex("^" + GlobToRegex(entry.TrimStart('.', '/')) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                continue;
            }
            string path = ProjectModel.ResolvePath(module, raw);
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path)) directories.Add(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar);
            else throw new InvalidOperationException($"{module.ModuleName}: ExcludedSources entry does not exist: {raw}");
        }
        return file =>
        {
            if (files.Contains(file) || directories.Any(d => file.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return true;
            if (patterns.Count == 0) return false;
            string relative = Path.GetRelativePath(module.ModuleDirectory, file).Replace('\\', '/');
            return patterns.Any(p => p.IsMatch(relative));
        };
    }

    internal static string GlobToRegex(string glob)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < glob.Length; ++i)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool slash = i + 2 < glob.Length && glob[i + 2] == '/';
                result.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') result.Append("[^/]*");
            else if (c == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(c.ToString()));
        }
        return result.ToString();
    }
}

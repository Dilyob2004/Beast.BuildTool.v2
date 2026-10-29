using System.Reflection;
using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>The tool's version ("2.1.0" for a release tag, "2.1.0-dev+&lt;commit&gt;" for local builds).</summary>
public static class BuildToolVersion
{
    public static string Informational =>
        typeof(BuildToolVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Major.minor.patch of a version string; a pre-release or build suffix is ignored ("2.1.0-dev+abc" -> 2.1.0).</summary>
    public static Version? Parse(string value)
    {
        var match = Regex.Match(value.Trim().TrimStart('v', 'V'), @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0, match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0)
            : null;
    }

    /// <summary>
    /// minimumBuildToolVersion: an older tool stops with a clear error. Development builds count as
    /// their base version (2.1.0-dev satisfies 2.1.0).
    /// </summary>
    public static void Require(string minimum, string configPath, string? current = null)
    {
        if (string.IsNullOrWhiteSpace(minimum)) return;
        var required = Parse(minimum) ?? throw new InvalidOperationException($"{configPath}: minimumBuildToolVersion '{minimum}' is not a version (expected e.g. \"2.1.0\").");
        current ??= Informational;
        var actual = Parse(current);
        if (actual != null && actual < required)
            throw new InvalidOperationException($"This project needs Beast BuildTool {minimum} or newer ({configPath}: minimumBuildToolVersion); this is {current}. " +
                "Update the tool: git pull and Build.sh/Build.bat, or a newer release from https://github.com/Dilyob2004/Beast.BuildTool.v2/releases.");
    }
}

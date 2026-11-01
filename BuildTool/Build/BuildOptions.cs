using System.Text.RegularExpressions;

namespace Beast.BuildTool;

/// <summary>
/// Named values the rules read through ModulesInfo.GetOption: "options" in the built project's
/// Config/BuildConfig.json, overridden on the command line by --option NAME=VALUE (repeatable).
/// They only steer the rules; what the rules produce (definitions, sources, libraries) is what the
/// incremental build and the cache compare, so changing an option rebuilds exactly what it changes.
/// </summary>
public static class BuildOptions
{
    private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configured options with the command-line overrides applied.</summary>
    public static IReadOnlyDictionary<string, string> Current
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in BuildConfigManager.Config.Options)
            {
                Validate(name, "Config/BuildConfig.json \"options\"");
                result[name] = value;
            }
            foreach (var (name, value) in _overrides) result[name] = value;
            return result;
        }
    }

    /// <summary>Sets the --option overrides ("NAME=VALUE" each); later values of the same name win.</summary>
    public static void SetOverrides(IEnumerable<string> assignments)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string assignment in assignments)
        {
            int equals = assignment.IndexOf('=');
            if (equals <= 0) throw new ArgumentException($"--option expects NAME=VALUE (got '{assignment}').");
            string name = assignment[..equals].Trim();
            Validate(name, "--option");
            overrides[name] = assignment[(equals + 1)..];
        }
        _overrides = overrides;
    }

    /// <summary>Command-line form of the overrides, for commands that start another build (IDE projects, watch).</summary>
    public static IReadOnlyList<string> OverrideArguments => _overrides.SelectMany(o => new[] { "--option", o.Key + "=" + o.Value }).ToList();

    internal static bool ParseBool(string name, string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" or "" => false,
        _ => throw new InvalidOperationException($"Build option '{name}' must be a boolean (true/false, 1/0, yes/no, on/off), got '{value}'.")
    };

    private static void Validate(string name, string source)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_.-]*$"))
            throw new ArgumentException($"{source}: invalid option name '{name}' (letters, digits, '_', '.', '-'; not starting with a digit).");
    }
}

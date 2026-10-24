namespace Beast.BuildTool;

/// <summary>A trusted, local generator with explicitly declared file inputs and outputs.</summary>
public sealed class BuildStep
{
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; } = [];
    public List<string> Inputs { get; } = [];
    public List<string> Outputs { get; } = [];
    public string WorkingDirectory { get; set; } = "";
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AffectsCompilation { get; set; } = true;
    public bool AlwaysRun { get; set; }
}

public sealed record RuntimeDependency(string Source, string? Destination = null);

public sealed class RuntimeDependencyCollection : List<RuntimeDependency>
{
    public void Add(string source) => Add(new RuntimeDependency(source));
    public void Add(string source, string destination) => Add(new RuntimeDependency(source, destination));
}

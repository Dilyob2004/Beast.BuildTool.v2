using System.Text;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>JSON / Graphviz DOT view of a planned build graph ('beast graph').</summary>
public static class BuildGraphDescription
{
    public static string Describe(BuildActionGraph graph, IReadOnlyList<(string Target, IReadOnlyList<CommandContext> Actions)> targets, string format)
    {
        // Stable short ids in planning order (action GUIDs change on every run).
        var ids = graph.Actions.Select((a, i) => (a, i)).ToDictionary(p => p.a, p => "a" + p.i);
        var owner = new Dictionary<CommandContext, string>();
        foreach (var (target, actions) in targets)
            foreach (var action in actions) owner.TryAdd(action, target);
        return format == "dot" ? Dot(graph, ids, owner, targets) : Json(graph, ids, owner, targets);
    }

    private static string Json(BuildActionGraph graph, Dictionary<CommandContext, string> ids, Dictionary<CommandContext, string> owner,
        IReadOnlyList<(string Target, IReadOnlyList<CommandContext> Actions)> targets) =>
        JsonSerializer.Serialize(new
        {
            targets = targets.Select(t => t.Target),
            actions = graph.Actions.Select(a => new
            {
                id = ids[a],
                target = owner.GetValueOrDefault(a),
                type = a.Type.ToString(),
                description = a.Description,
                outputs = a.ProducedFiles,
                inputs = a.DeclaredInputs,
                dependencies = graph.Dependencies[a].Select(d => ids[d]).Order(StringComparer.Ordinal),
            }),
            // Critical path length in actions: the minimum number of sequential steps of this graph.
            depth = Depth(graph),
        }, new JsonSerializerOptions { WriteIndented = true });

    private static string Dot(BuildActionGraph graph, Dictionary<CommandContext, string> ids, Dictionary<CommandContext, string> owner,
        IReadOnlyList<(string Target, IReadOnlyList<CommandContext> Actions)> targets)
    {
        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static string Shape(CommandType type) => type switch
        {
            CommandType.Link => "box3d", CommandType.Custom => "hexagon", CommandType.Copy => "note", CommandType.GeneratePCH => "component", _ => "box"
        };
        var text = new StringBuilder("digraph BeastBuild {\n  rankdir=LR;\n  node [fontname=\"Helvetica\", fontsize=10];\n");
        int cluster = 0;
        foreach (var (target, actions) in targets)
        {
            text.Append($"  subgraph cluster_{cluster++} {{\n    label={Quote(target)};\n");
            foreach (var action in actions.Where(ids.ContainsKey))
                text.Append($"    {ids[action]} [label={Quote(action.Description.TrimEnd('.', ' '))}, shape={Shape(action.Type)}];\n");
            text.Append("  }\n");
        }
        foreach (var action in graph.Actions.Where(a => !owner.ContainsKey(a)))
            text.Append($"  {ids[action]} [label={Quote(action.Description.TrimEnd('.', ' '))}, shape={Shape(action.Type)}];\n");
        foreach (var action in graph.Actions)
            foreach (var dependency in graph.Dependencies[action].OrderBy(d => ids[d], StringComparer.Ordinal))
                text.Append($"  {ids[dependency]} -> {ids[action]};\n");
        return text.Append("}\n").ToString();
    }

    private static int Depth(BuildActionGraph graph)
    {
        var memo = new Dictionary<CommandContext, int>();
        int Of(CommandContext action) => memo.TryGetValue(action, out int known) ? known
            : memo[action] = 1 + graph.Dependencies[action].Select(Of).DefaultIfEmpty(0).Max();
        return graph.Actions.Select(Of).DefaultIfEmpty(0).Max();
    }
}

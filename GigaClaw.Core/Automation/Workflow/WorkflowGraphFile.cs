using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaClaw.Core.Automation.Workflow;

/// <summary>
/// Reads <c>&lt;workspace&gt;/.agents/workflow.json</c> — the workflow graph, which lives beside
/// <c>automations.json</c> because it is the same kind of thing: declarative workspace config the
/// engine reads, the owner edits and a pack may compose into.
/// <para>
/// It is validated at the <b>same load point</b> as the automations, inside
/// <see cref="AutomationStore.LoadAsync"/>, and an invalid graph is reported the way an invalid
/// <c>automations.json</c> already is: by throwing out of that method, which
/// <c>ProjectRuntimeManager.ReloadProjectAsync</c> catches and logs as a failed reload, leaving the
/// project's previous runtime in place. Failing closed is the point — a workflow with an unreachable
/// state or an ungated loop must not be half-applied.
/// </para>
/// </summary>
public static class WorkflowGraphFile
{
    public const string FileName = "workflow.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // State kinds and transition labels are hand-written, so they are words, not ordinals.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonSerializerOptions JsonOptions => Json;

    public static string PathFor(string agentsDir) => Path.Combine(agentsDir, FileName);

    /// <summary>
    /// The workspace's graph, or null when it has none — a workspace without a
    /// <c>workflow.json</c> is the normal case and not an error.
    /// </summary>
    /// <exception cref="WorkflowGraphException">
    /// The file exists but cannot be parsed, or parses into a graph
    /// <see cref="WorkflowGraph.Validate"/> rejects. Every problem is named in one message rather
    /// than one per load, so a broken graph is fixed in a single pass.
    /// </exception>
    public static WorkflowGraph? Read(string agentsDir)
    {
        var path = PathFor(agentsDir);
        if (!File.Exists(path)) return null;

        WorkflowGraph? graph;
        try
        {
            using var stream = File.OpenRead(path);
            graph = JsonSerializer.Deserialize<WorkflowGraph>(stream, Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new WorkflowGraphException($"{FileName} could not be read: {exception.Message}", exception);
        }

        // An empty document is not "no workflow": the file was written on purpose, and silently
        // treating it as absent would hide a truncated write.
        if (graph is null)
            throw new WorkflowGraphException($"{FileName} is empty.");

        var problems = graph.Validate();
        if (problems.Count > 0)
            throw new WorkflowGraphException($"{FileName} is invalid: {string.Join(" ", problems)}");

        return graph;
    }
}

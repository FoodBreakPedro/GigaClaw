using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Workflow;

namespace GigaClaw.Core.Automation;

internal sealed class ProjectRuntime
{
    public ProjectRuntime(string slug) { Slug = slug; }
    public string Slug { get; }
    public string? Workspace { get; set; }
    public AutomationConfig? Config { get; set; }

    /// <summary>
    /// The workspace's validated workflow graph, or null when it has none. Only ever set from a
    /// load that succeeded, so a runtime never holds a graph that failed validation.
    /// </summary>
    public WorkflowGraph? Workflow { get; set; }
    public Dictionary<string, ITrigger> Triggers { get; set; } = new();
    public bool ConfigDirty { get; set; }
}

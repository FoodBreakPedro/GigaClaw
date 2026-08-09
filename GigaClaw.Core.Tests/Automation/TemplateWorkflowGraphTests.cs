using System.Text.Json;
using GigaClaw.Core.Automation.Workflow;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The shipped <c>ProjectTemplate/Agents/workflow.json</c> is validated at every
/// <c>AutomationStore.LoadAsync</c>, and an invalid graph fails the <b>whole</b> automation reload —
/// so a typo here would silently freeze a project on its last good runtime. These tests are the gate
/// that keeps that from ever reaching a workspace.
/// <para>
/// The graph ships as a <b>declaration</b>: it names each deliverable's stages so the board can place
/// a ticket among them. Nothing executes it — the pipeline is driven by the handoff automations, and
/// no <c>startWorkflow</c> action fires it. The last test pins that.
/// </para>
/// </summary>
public class TemplateWorkflowGraphTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "GigaClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "GigaClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string TemplateAgentsDir() =>
        Path.Combine(RepoRoot(), "ProjectTemplate", "Agents");

    private static WorkflowGraph Graph() =>
        WorkflowGraphFile.Read(TemplateAgentsDir())
        ?? throw new InvalidOperationException("ProjectTemplate/Agents/workflow.json is missing.");

    [Fact]
    public void Template_graph_parses_and_validates()
    {
        // Read() already throws on a rejected graph; asserting Validate() directly names every
        // problem at once rather than only the first, which is how the validator is meant to be read.
        var problems = Graph().Validate();
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void Every_deliverable_entry_agent_reaches_a_task_state_that_dispatches_to_it()
    {
        var graph = Graph();
        var roles = graph.States
            .Where(state => state.Kind == WorkflowStateKind.Task)
            .Select(state => state.Role!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var deliverable in DeliverableCatalog.GetAll())
        {
            Assert.True(
                roles.Contains(deliverable.EntryAgent),
                $"Deliverable '{deliverable.Slug}' enters at '{deliverable.EntryAgent}', "
                + "which no task state in workflow.json dispatches to.");
        }
    }

    [Fact]
    public void Every_deliverable_is_routed_by_a_gate_on_its_entry_agent()
    {
        var graph = Graph();
        var gated = graph.States
            .Where(state => state.Kind == WorkflowStateKind.Gate)
            .Select(state => state.Gate)
            .OfType<AssignedToConditionSpec>()
            .SelectMany(spec => spec.Slugs)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var deliverable in DeliverableCatalog.GetAll())
        {
            Assert.True(
                gated.Contains(deliverable.EntryAgent),
                $"Deliverable '{deliverable.Slug}' has no routing gate on entry agent "
                + $"'{deliverable.EntryAgent}', so a ticket created for it would fall through to "
                + "the unrouted terminal.");
        }
    }

    [Fact]
    public void Graph_is_declared_only_and_nothing_starts_a_walk()
    {
        // Executing the graph would put a second engine on a pipeline the handoff automations
        // already drive: the walker materializes a sub-ticket per task state, so both would run.
        // If a startWorkflow action is ever added, that reconciliation has to be designed first.
        var automations = File.ReadAllText(Path.Combine(TemplateAgentsDir(), "automations.json"));
        Assert.DoesNotContain("startWorkflow", automations, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_does_not_claim_unconfigured_delivery()
    {
        var graph = Graph();

        Assert.DoesNotContain(graph.States, state => state.Name == "published");
        var terminal = Assert.Single(graph.States, state => state.Name == "complete");
        Assert.Contains("does not imply publishing or sending", terminal.Description, StringComparison.OrdinalIgnoreCase);
    }
}

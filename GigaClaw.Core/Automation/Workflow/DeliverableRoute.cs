using GigaClaw.Core.Models;

namespace GigaClaw.Core.Automation.Workflow;

/// <summary>
/// One stage of a deliverable's declared journey, in the order a ticket meets them.
/// </summary>
/// <param name="State">The <c>workflow.json</c> state this stage came from.</param>
/// <param name="Role">The agent that works the stage.</param>
/// <param name="Description">The state's own prose, shown to the owner. Never load-bearing.</param>
public sealed record DeliverableStage(string State, string Role, string? Description);

/// <summary>
/// Where a ticket sits among its deliverable's stages. <see cref="CurrentIndex"/> is -1 when the
/// ticket's assignee is not one of the stages — an owner-assigned specialist, or a recovery hop to
/// the groomer — which the board must render as "off-route" rather than silently as stage one.
/// </summary>
public sealed record DeliverableProgress(
    IReadOnlyList<DeliverableStage> Stages,
    int CurrentIndex)
{
    public bool IsOnRoute => CurrentIndex >= 0;
}

/// <summary>
/// Reads a deliverable's route out of the workspace's declared <see cref="WorkflowGraph"/>.
/// <para>
/// The route is <b>declared, not inferred</b>. Deriving it from <c>automations.json</c> was tried and
/// does not work: <c>blog-reviewer-on-review</c> dispatches with <c>runAgent</c> and never reassigns
/// the ticket, so a walk over <c>assignTicket</c> edges omits the reviewer entirely and reports the
/// blog route one stage short. See <c>doc/workflow-graph.md</c>.
/// </para>
/// <para>
/// Nothing here executes the graph — <c>WorkflowWalker</c> does that, and no template automation
/// starts a walk. This is the read-only view the board renders.
/// </para>
/// </summary>
public static class DeliverableRoute
{
    /// <summary>
    /// The stages a ticket for <paramref name="deliverable"/> is declared to pass through, or an
    /// empty list when the graph routes it nowhere.
    /// </summary>
    /// <remarks>
    /// Routing gates are matched structurally on <see cref="AssignedToConditionSpec"/> rather than by
    /// evaluating them, because there is no ticket yet at creation time — the board has to answer
    /// "what will happen" before anything exists to evaluate against.
    /// </remarks>
    public static IReadOnlyList<DeliverableStage> Resolve(WorkflowGraph? graph, DeliverableDefinition? deliverable)
    {
        if (graph is null || deliverable is null) return [];

        var entry = FindEntryState(graph, deliverable.EntryAgent);
        if (entry is null) return [];

        var stages = new List<DeliverableStage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = entry;

        // The graph is cycle-bounded and validated, but this view is rendered on every board paint —
        // stop on a revisit rather than trusting that and looping in the UI thread.
        while (state is not null && seen.Add(state.Name))
        {
            if (state.Kind == WorkflowStateKind.Task && !string.IsNullOrWhiteSpace(state.Role))
                stages.Add(new DeliverableStage(state.Name, state.Role!, state.Description));

            if (state.Kind == WorkflowStateKind.Terminal) break;
            state = NextOnHappyPath(graph, state);
        }

        return stages;
    }

    /// <summary>
    /// Places <paramref name="assignedTo"/> among the deliverable's stages.
    /// </summary>
    public static DeliverableProgress Locate(
        WorkflowGraph? graph, DeliverableDefinition? deliverable, string? assignedTo)
    {
        var stages = Resolve(graph, deliverable);
        if (stages.Count == 0 || string.IsNullOrWhiteSpace(assignedTo))
            return new DeliverableProgress(stages, -1);

        for (var i = 0; i < stages.Count; i++)
        {
            if (string.Equals(stages[i].Role, assignedTo, StringComparison.OrdinalIgnoreCase))
                return new DeliverableProgress(stages, i);
        }

        return new DeliverableProgress(stages, -1);
    }

    /// <summary>
    /// Follows the routing gates from the graph's entry state to the task state that dispatches to
    /// <paramref name="entryAgent"/>. A gate whose condition names the agent is taken on its
    /// <c>PASS</c> arm; any other gate falls through on <c>FAIL</c>, which is how the chain of
    /// per-deliverable gates in the shipped graph is meant to be read.
    /// </summary>
    private static WorkflowState? FindEntryState(WorkflowGraph graph, string entryAgent)
    {
        var state = graph.Find(graph.Initial ?? graph.States.FirstOrDefault()?.Name ?? "");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (state is not null && seen.Add(state.Name))
        {
            if (state.Kind == WorkflowStateKind.Task)
            {
                return string.Equals(state.Role, entryAgent, StringComparison.OrdinalIgnoreCase)
                    ? state
                    : null;
            }

            if (state.Kind != WorkflowStateKind.Gate) return null;

            var matches = state.Gate is AssignedToConditionSpec assigned
                          && assigned.Slugs.Contains(entryAgent, StringComparer.OrdinalIgnoreCase);
            var arm = matches ? "PASS" : "FAIL";
            state = graph.Find(Arm(state, arm) ?? "");
        }

        return null;
    }

    /// <summary>
    /// The stage that follows <paramref name="state"/> when nothing goes wrong: a task's only exit,
    /// or a gate's shipping arm. Repair and escalation arms are deliberately not followed — the board
    /// is showing the route, and a repair loop is a detour from it, not a stage of it.
    /// </summary>
    private static WorkflowState? NextOnHappyPath(WorkflowGraph graph, WorkflowState state)
    {
        if (state.Kind == WorkflowStateKind.Gate)
        {
            var ship = Arm(state, "SHIP") ?? Arm(state, "PASS");
            return ship is null ? null : graph.Find(ship);
        }

        var next = state.Next.FirstOrDefault(t => t.When is null) ?? state.Next.FirstOrDefault();
        return next is null ? null : graph.Find(next.To);
    }

    private static string? Arm(WorkflowState state, string when) =>
        state.Next.FirstOrDefault(t => string.Equals(t.When, when, StringComparison.OrdinalIgnoreCase))?.To;
}

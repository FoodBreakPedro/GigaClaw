using System.Text.Json.Serialization;

namespace GigaClaw.Core.Automation.Workflow;

/// <summary>What a state does when a ticket is in it.</summary>
public enum WorkflowStateKind
{
    /// <summary>One role works the ticket. The ordinary case.</summary>
    Task,
    /// <summary>The ticket fans out into every state in <see cref="WorkflowState.Next"/> at once.</summary>
    FanOut,
    /// <summary>Closes the fan-out named by <see cref="WorkflowState.JoinOf"/>.</summary>
    Join,
    /// <summary>Routes on a condition — this is where <c>verdictIs</c> lives.</summary>
    Gate,
    /// <summary>The ticket stops here. No outgoing transition.</summary>
    Terminal
}

/// <summary>
/// One edge out of a state. <see cref="When"/> is the outcome label a gate routes on — the
/// vocabulary <c>verdictIs</c> already uses (<c>SHIP</c>, <c>FIX</c>, <c>BLOCK</c>, <c>MISSING</c>,
/// <c>INVALID</c>, <c>STALE</c>) — and is null for an unconditional edge.
/// </summary>
public sealed record WorkflowTransition(string To)
{
    public string? When { get; init; }
}

/// <summary>
/// A named state of a <see cref="WorkflowGraph"/>.
/// </summary>
public sealed record WorkflowState(string Name, WorkflowStateKind Kind)
{
    /// <summary>
    /// Role that owns the state. Required for <see cref="WorkflowStateKind.Task"/> — a task state
    /// with nobody to dispatch to is not a state, it is a typo — and it is what visited-role
    /// tracking records as the ticket moves.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>Human-readable purpose. Never load-bearing.</summary>
    public string? Description { get; init; }

    public IReadOnlyList<WorkflowTransition> Next { get; init; } = [];

    /// <summary>
    /// The condition a <see cref="WorkflowStateKind.Gate"/> routes on, in the ordinary automation
    /// vocabulary (<see cref="VerdictIsConditionSpec"/> and friends). Reusing <see cref="ConditionSpec"/>
    /// rather than inventing a second condition language is the same choice
    /// <c>TeamDefinition.EntryConditions</c> made: one evaluator set, one editor, one meaning.
    /// </summary>
    public ConditionSpec? Gate { get; init; }

    /// <summary>For a <see cref="WorkflowStateKind.Join"/>: the fan-out state it closes.</summary>
    public string? JoinOf { get; init; }
}

/// <summary>
/// A typed workflow graph over tickets: named states, fan-out and join nodes, gates that reuse the
/// automation condition vocabulary, terminal states, visited-role tracking and a cycle bound.
/// <para>
/// The graph is <b>declarative and validated at config load</b> — see
/// <see cref="WorkflowGraphFile"/>. Its job in C5 is to make an illegal workflow un-loadable rather
/// than discoverable at 3am: a state nothing can reach is dead prose, and a cycle with no gate in it
/// is an unbounded loop with a ticket and a token budget inside.
/// </para>
/// </summary>
public sealed record WorkflowGraph
{
    /// <summary>Highest schema version this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Name of the entry state. Null means the first declared state.</summary>
    public string? Initial { get; init; }

    public IReadOnlyList<WorkflowState> States { get; init; } = [];

    /// <summary>
    /// How many times one state may be re-entered before the run is escalated instead of looping.
    /// Must be at least 1 whenever the graph contains a cycle; a bound of 0 on a cyclic graph is a
    /// workflow that can never take its own loop.
    /// </summary>
    public int MaxCycles { get; init; } = 3;

    /// <summary>
    /// Whether the run records the <see cref="WorkflowState.Role"/> of every state it enters, so a
    /// gate can ask what has already been seen. On by default; turning it off does not remove the
    /// requirement that a task state name its role.
    /// </summary>
    public bool TrackVisitedRoles { get; init; } = true;

    [JsonIgnore]
    public string? EntryState => Initial ?? States.FirstOrDefault()?.Name;

    public WorkflowState? Find(string name) =>
        States.FirstOrDefault(state => string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Structural problems that make a graph un-runnable. Empty means valid. I/O-free, like
    /// <c>TeamDefinition.Validate()</c>, so the loader, a future editor and the tests share one
    /// verdict rather than three readings of the same document.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        // A newer document is refused, not best-effort parsed: System.Text.Json drops unknown
        // members silently, so a v2 graph would otherwise load as a quietly wrong v1 one.
        if (SchemaVersion > CurrentSchemaVersion)
        {
            problems.Add(
                $"Workflow schemaVersion {SchemaVersion} is newer than this build understands ({CurrentSchemaVersion}).");
            return problems;
        }
        if (SchemaVersion < 1)
            problems.Add($"Workflow schemaVersion {SchemaVersion} is not a version.");

        if (States.Count == 0)
        {
            problems.Add("Workflow graph has no states.");
            return problems;
        }

        foreach (var name in States
            .GroupBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key))
        {
            problems.Add($"Duplicate state '{name}'.");
        }

        foreach (var state in States)
        {
            if (string.IsNullOrWhiteSpace(state.Name))
            {
                problems.Add("A state is missing its name.");
                continue;
            }

            foreach (var transition in state.Next)
            {
                if (string.IsNullOrWhiteSpace(transition.To))
                    problems.Add($"State '{state.Name}' has a transition with no target.");
                else if (Find(transition.To) is null)
                    problems.Add($"State '{state.Name}' transitions to unknown state '{transition.To}'.");
            }

            if (state.Kind == WorkflowStateKind.Terminal)
            {
                if (state.Next.Count > 0)
                    problems.Add($"Terminal state '{state.Name}' has {state.Next.Count} outgoing transition(s).");
            }
            else if (state.Next.Count == 0)
            {
                problems.Add($"State '{state.Name}' is not terminal but has nowhere to go.");
            }

            // Visited-role tracking has nothing to record without a role, and the state would have
            // nobody to dispatch to. Both failures come from the same missing word.
            if (state.Kind == WorkflowStateKind.Task && string.IsNullOrWhiteSpace(state.Role))
                problems.Add($"Task state '{state.Name}' names no role.");

            if (state.Kind == WorkflowStateKind.Gate && state.Gate is null)
                problems.Add($"Gate state '{state.Name}' has no condition to route on.");

            if (state.Kind != WorkflowStateKind.Gate && state.Gate is not null)
                problems.Add($"State '{state.Name}' carries a gate condition but is a {state.Kind} state.");

            if (state.Kind == WorkflowStateKind.FanOut && state.Next.Count < 2)
                problems.Add($"Fan-out state '{state.Name}' has {state.Next.Count} branch(es); a fan-out needs at least 2.");

            if (state.Kind == WorkflowStateKind.Join)
            {
                if (string.IsNullOrWhiteSpace(state.JoinOf))
                    problems.Add($"Join state '{state.Name}' does not say which fan-out it closes.");
                else if (Find(state.JoinOf) is not { Kind: WorkflowStateKind.FanOut })
                    problems.Add($"Join state '{state.Name}' closes '{state.JoinOf}', which is not a fan-out state.");
            }
        }

        if (EntryState is null || Find(EntryState) is null)
            problems.Add($"Initial state '{Initial}' is not one of the graph's states.");

        if (!States.Any(state => state.Kind == WorkflowStateKind.Terminal))
            problems.Add("Workflow graph has no terminal state; every path would run forever.");

        problems.AddRange(UnreachableStates());
        problems.AddRange(GatelessCycles());

        if (MaxCycles < 0)
            problems.Add($"maxCycles {MaxCycles} is negative.");
        else if (MaxCycles == 0 && HasCycle(States))
            problems.Add("maxCycles is 0 but the graph has a cycle, so the loop could never be taken.");

        return problems;
    }

    /// <summary>
    /// States no path from the entry can reach. Dead prose at best; more often a transition that was
    /// renamed on one side only, which is invisible until the ticket that needed it is stuck.
    /// </summary>
    private IEnumerable<string> UnreachableStates()
    {
        if (EntryState is null || Find(EntryState) is null) yield break;

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(EntryState);
        reached.Add(EntryState);
        while (queue.Count > 0)
        {
            var state = Find(queue.Dequeue());
            if (state is null) continue;
            foreach (var transition in state.Next)
                if (Find(transition.To) is { } target && reached.Add(target.Name))
                    queue.Enqueue(target.Name);
        }

        foreach (var state in States)
            if (!string.IsNullOrWhiteSpace(state.Name) && !reached.Contains(state.Name))
                yield return $"State '{state.Name}' is unreachable from '{EntryState}'.";
    }

    /// <summary>
    /// Cycles with no gate on them.
    /// <para>
    /// Found by deleting every <see cref="WorkflowStateKind.Gate"/> state and looking for a cycle in
    /// what is left. That is exact rather than approximate: a cycle survives the deletion precisely
    /// when it contained no gate, and if nothing survives then every cycle in the full graph passes
    /// through a gate. A gate is the only thing in this model that can decide to leave a loop, so a
    /// cycle without one is an unbounded loop with a ticket and a token budget inside it.
    /// </para>
    /// </summary>
    private IEnumerable<string> GatelessCycles()
    {
        var ungated = States.Where(state => state.Kind != WorkflowStateKind.Gate).ToArray();
        // Indexer, not ToDictionary: duplicate names are one of the things Validate() reports, and a
        // validator that throws on the input it exists to reject is worse than no validator.
        var reachableByName = new Dictionary<string, WorkflowState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in ungated)
            if (!string.IsNullOrWhiteSpace(state.Name))
                reachableByName[state.Name] = state;

        var state0 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var cycles = new List<string>();

        void Visit(string name)
        {
            if (state0.TryGetValue(name, out var visited))
            {
                if (visited == 1)
                {
                    var start = path.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
                    cycles.Add(string.Join(" -> ", path.Skip(start).Append(name)));
                }
                return;
            }

            state0[name] = 1;
            path.Add(name);
            if (reachableByName.TryGetValue(name, out var node))
                foreach (var transition in node.Next)
                    if (reachableByName.ContainsKey(transition.To))
                        Visit(transition.To);
            path.RemoveAt(path.Count - 1);
            state0[name] = 2;
        }

        foreach (var state in ungated)
            if (!string.IsNullOrWhiteSpace(state.Name))
                Visit(state.Name);

        return cycles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(cycle => $"Workflow graph has a cycle with no gate on it: {cycle}. Add a gate the loop can exit through.");
    }

    /// <summary>Whether the graph has any cycle at all, gated or not.</summary>
    private static bool HasCycle(IReadOnlyList<WorkflowState> states)
    {
        var byName = new Dictionary<string, WorkflowState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
            if (!string.IsNullOrWhiteSpace(state.Name))
                byName[state.Name] = state;

        var mark = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var found = false;

        void Visit(string name)
        {
            if (found) return;
            if (mark.TryGetValue(name, out var visited))
            {
                if (visited == 1) found = true;
                return;
            }
            mark[name] = 1;
            if (byName.TryGetValue(name, out var node))
                foreach (var transition in node.Next)
                    if (byName.ContainsKey(transition.To))
                        Visit(transition.To);
            mark[name] = 2;
        }

        foreach (var state in byName.Keys)
            Visit(state);
        return found;
    }
}

/// <summary>
/// Raised when a workflow graph cannot be read or is structurally invalid. Thrown out of
/// <c>AutomationStore.LoadAsync</c>, which is the same way a malformed <c>automations.json</c>
/// already reports itself.
/// </summary>
public sealed class WorkflowGraphException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

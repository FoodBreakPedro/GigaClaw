using System.Text.Json.Serialization;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Models;

/// <summary>How a <see cref="TeamRun"/> decides it is finished once its tasks report back.</summary>
public enum TeamJoinMode
{
    /// <summary>Join when every task reached a terminal state; any failure fails the run.</summary>
    AllDone,
    /// <summary>Join as soon as <see cref="TeamJoinPolicy.Quorum"/> tasks completed successfully.</summary>
    Quorum,
    /// <summary>Join (and stop) as soon as the first task fails; otherwise behaves like AllDone.</summary>
    FirstFailure
}

/// <summary>
/// What a run does when the join fires <b>without</b> every lane having reported — one branch
/// failed, was cancelled, or the quorum went out of reach.
/// </summary>
public enum TeamPartialFailure
{
    /// <summary>
    /// Hand the synthesizer what did report, plus a named list of every gap and its reason
    /// (synthesize-with-gaps). The C4 behaviour and the default: a partial answer with its holes
    /// stated is worth more than none.
    /// </summary>
    Synthesize,

    /// <summary>
    /// Skip the synthesizer entirely and close the run as failed, the gaps in its receipt. For
    /// branches whose results are worthless unless all of them arrived.
    /// </summary>
    FailFast
}

/// <summary>
/// Join semantics of a team definition. <see cref="Quorum"/> is only meaningful for
/// <see cref="TeamJoinMode.Quorum"/> and must then name at least one task.
/// </summary>
public sealed record TeamJoinPolicy(TeamJoinMode Mode, int Quorum = 0)
{
    public static TeamJoinPolicy AllDone { get; } = new(TeamJoinMode.AllDone);
    public static TeamJoinPolicy FirstFailure { get; } = new(TeamJoinMode.FirstFailure);
    public static TeamJoinPolicy OfQuorum(int quorum) => new(TeamJoinMode.Quorum, quorum);
}

/// <summary>
/// A named seat in a team, bound to the agent slug that fills it. Filter-only teams have one
/// role per member; executable teams reference roles from their task graph and synthesizer.
/// </summary>
public sealed record TeamRole(string RoleId, string AgentSlug)
{
    public string? Description { get; init; }
}

/// <summary>
/// One node of a definition's task graph. Materialized into a sub-ticket (a <see cref="TeamTask"/>)
/// when a run fans out; <see cref="DependsOn"/> names sibling <see cref="Key"/> values and becomes
/// real ticket dependency edges — the same edges the board and <c>dependenciesResolved</c> already use.
/// </summary>
public sealed record TeamTaskTemplate(string Key, string RoleId, string Title)
{
    /// <summary>Prompt handed to the role's agent. Null means "use the sub-ticket description".</summary>
    public string? Prompt { get; init; }

    /// <summary>Sibling task keys that must resolve before this task may start.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// A runnable team: roles, the entry conditions that admit a ticket, a task-graph template, the
/// join policy, and the role that synthesizes the join result.
/// <para>
/// An <b>empty task graph is valid</b> and means "pure member filter" — exactly what the nine
/// built-in teams are. Such a definition never fans out; it only narrows the member list.
/// </para>
/// Entry conditions reuse the automation <see cref="ConditionSpec"/> vocabulary rather than
/// inventing a second one, so the existing evaluators can gate team entry unchanged.
/// </summary>
public sealed record TeamDefinition(string Slug, string Name, string Description, string Icon)
{
    public IReadOnlyList<TeamRole> Roles { get; init; } = [];
    public IReadOnlyList<ConditionSpec> EntryConditions { get; init; } = [];
    public IReadOnlyList<TeamTaskTemplate> TaskGraph { get; init; } = [];
    public TeamJoinPolicy JoinPolicy { get; init; } = TeamJoinPolicy.AllDone;

    /// <summary>Role that receives the join result. Null for filter-only definitions.</summary>
    public string? SynthesizerRole { get; init; }

    /// <summary>
    /// What the join does when it fires without every lane reporting. Defaults to
    /// <see cref="TeamPartialFailure.Synthesize"/>, which is what every C4 team already did.
    /// </summary>
    public TeamPartialFailure PartialFailure { get; init; } = TeamPartialFailure.Synthesize;

    /// <summary>
    /// Most tasks of this run that may sit in the dispatch column at the same time. <c>0</c> (the
    /// default) means unlimited — the run releases every task whose blockers resolved and lets the
    /// host-wide <c>RunConcurrencyGate</c> be the only limit.
    /// <para>
    /// This is a <i>second</i>, per-run ceiling, not a replacement for the gate: a task is still
    /// dispatched by the ordinary <c>ticketInColumn</c> automation and still queues behind the gate
    /// and the file leases. Capping it here only decides how many sub-tickets are offered to that
    /// path at once.
    /// </para>
    /// </summary>
    public int MaxConcurrency { get; init; }

    /// <summary>
    /// C8: when true, the synthesizer's brief (<c>TeamRunService.ComposeBrief</c>) is preceded by a
    /// deduplicated view of every reporting lane's findings (<c>RunHandoff.OpenLoops</c>), merged by
    /// <see cref="Automation.Handoffs.FindingDeduplicator"/> and attributed back to the lane(s) that
    /// raised each one, and the join's outcome is distilled into a host-authored
    /// <c>GIGACLAW-VERDICT</c> receipt on the parent ticket (agent <c>team-synthesis</c>) so an
    /// ordinary <c>verdictIs</c> automation can gate on it — the same vocabulary C2 already reads,
    /// no second one. A team that does not opt in keeps the C4 brief exactly as it was; a lane with
    /// no parseable open loops degrades to an empty, honestly-labeled section rather than an error.
    /// </summary>
    public bool DedupeFindings { get; init; }

    /// <summary>True when the definition can fan out; false for the pure member filters.</summary>
    [JsonIgnore]
    public bool IsExecutable => TaskGraph.Count > 0;

    /// <summary>Member filter derived from the roles, in declaration order and de-duplicated.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AgentSlugs =>
        Roles.Select(role => role.AgentSlug)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Projects the definition onto the filter-only shape the team UI and catalog consume.</summary>
    public AgentTeam ToAgentTeam() => new(Slug, Name, Description, Icon, AgentSlugs);

    public TeamTaskTemplate? FindTask(string key) =>
        TaskGraph.FirstOrDefault(task => string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase));

    public TeamRole? FindRole(string roleId) =>
        Roles.FirstOrDefault(role => string.Equals(role.RoleId, roleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Structural problems that make a definition un-runnable. Empty means valid. Pure I/O-free
    /// so callers (the store, config loading, the future editor) share one verdict.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Slug))
            problems.Add("Team slug is required.");
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Team name is required.");

        var duplicateRoles = Roles
            .GroupBy(role => role.RoleId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var roleId in duplicateRoles)
            problems.Add($"Duplicate role '{roleId}'.");

        foreach (var role in Roles)
        {
            if (string.IsNullOrWhiteSpace(role.RoleId))
                problems.Add("A role is missing its id.");
            if (string.IsNullOrWhiteSpace(role.AgentSlug))
                problems.Add($"Role '{role.RoleId}' is missing its agent slug.");
        }

        var duplicateKeys = TaskGraph
            .GroupBy(task => task.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var key in duplicateKeys)
            problems.Add($"Duplicate task key '{key}'.");

        foreach (var task in TaskGraph)
        {
            if (string.IsNullOrWhiteSpace(task.Key))
                problems.Add("A task template is missing its key.");
            if (FindRole(task.RoleId) is null)
                problems.Add($"Task '{task.Key}' references unknown role '{task.RoleId}'.");
            foreach (var dependency in task.DependsOn)
            {
                if (string.Equals(dependency, task.Key, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Task '{task.Key}' depends on itself.");
                else if (FindTask(dependency) is null)
                    problems.Add($"Task '{task.Key}' depends on unknown task '{dependency}'.");
            }
        }

        foreach (var cycle in FindCycles())
            problems.Add($"Task graph has a dependency cycle: {cycle}.");

        if (SynthesizerRole is not null && FindRole(SynthesizerRole) is null)
            problems.Add($"Synthesizer role '{SynthesizerRole}' is not one of the team roles.");

        if (MaxConcurrency < 0)
            problems.Add($"Max concurrency {MaxConcurrency} is negative; use 0 for unlimited.");

        if (JoinPolicy.Mode == TeamJoinMode.Quorum)
        {
            if (JoinPolicy.Quorum < 1)
                problems.Add("Quorum join policy needs a quorum of at least 1.");
            else if (TaskGraph.Count > 0 && JoinPolicy.Quorum > TaskGraph.Count)
                problems.Add($"Quorum {JoinPolicy.Quorum} exceeds the {TaskGraph.Count} task(s) in the graph.");
        }

        return problems;
    }

    /// <summary>Depth-first cycle detection over the template graph, reported as readable paths.</summary>
    private IEnumerable<string> FindCycles()
    {
        var byKey = new Dictionary<string, TeamTaskTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in TaskGraph)
            byKey[task.Key] = task;

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var cycles = new List<string>();

        void Visit(string key)
        {
            if (state.TryGetValue(key, out var visited))
            {
                if (visited == 1)
                {
                    var start = path.FindIndex(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
                    cycles.Add(string.Join(" -> ", path.Skip(start).Append(key)));
                }
                return;
            }

            state[key] = 1;
            path.Add(key);
            if (byKey.TryGetValue(key, out var task))
                foreach (var dependency in task.DependsOn)
                    if (byKey.ContainsKey(dependency))
                        Visit(dependency);
            path.RemoveAt(path.Count - 1);
            state[key] = 2;
        }

        foreach (var task in TaskGraph)
            Visit(task.Key);

        return cycles.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Convenience builder for the filter-only definitions the nine built-in teams use.</summary>
    public static TeamDefinition FilterOnly(
        string slug,
        string name,
        string description,
        string icon,
        IEnumerable<string> agentSlugs) =>
        new(slug, name, description, icon)
        {
            Roles = agentSlugs.Select(agentSlug => new TeamRole(agentSlug, agentSlug)).ToArray()
        };
}

/// <summary>Raised when a definition is rejected — carries a stable code for API/UI mapping.</summary>
public sealed class TeamDefinitionException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

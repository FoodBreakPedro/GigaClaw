using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

/// <summary>
/// The lifecycle of a <see cref="TeamRun"/>: fan-out, dispatch ordering and cancellation.
/// <para>
/// Everything this service does, it does <b>on the board</b>. Fan-out creates one sub-ticket per
/// task template; ordering is expressed as ordinary <c>TicketDependencies</c> edges plus the column
/// a sub-ticket sits in; a task is dispatched by the same <c>ticketInColumn</c> automations that
/// dispatch every other ticket. There is no in-memory graph, no scheduler and no queue — which is
/// exactly why a run survives an engine restart: <see cref="ReconcileProjectAsync"/> rebuilds
/// everything it needs from the run rows, the task rows and the tickets they point at.
/// </para>
/// <para>
/// Readiness is not a second rule. A task is released when
/// <see cref="ConditionEvaluators.DependenciesResolved"/> — the evaluator behind the
/// <c>dependenciesResolved</c> automation condition — says the ticket's live <c>blockedBy</c> edges
/// are all resolved. Removing an edge on the board really does unblock a task.
/// </para>
/// The join policy and the synthesizer are deliberately <b>not</b> here: a run whose tasks have all
/// finished stays <see cref="TeamRunStatus.Running"/> until that slice lands.
/// </summary>
public sealed class TeamRunService
{
    /// <summary>Column a task's sub-ticket is released into once nothing blocks it. The dispatch
    /// column the per-agent <c>ticketInColumn</c> automations already watch.</summary>
    public const string ReadyStatus = "Todo";

    /// <summary>Column a task's sub-ticket waits in while one of its blockers is unresolved.</summary>
    public const string HoldStatus = "Blocked";

    /// <summary>Column open sub-tickets are parked in when the run is cancelled — deliberately one
    /// no dispatch automation watches, so a cancelled task cannot be picked up.</summary>
    public const string ParkedStatus = "Backlog";

    /// <summary>Parent statuses that close a run: reaching one cancels every still-open task.</summary>
    private static readonly string[] ClosingParentStatuses = ["Done"];

    // The default spec is "every blocker is Done" — the same default the condition ships with.
    private static readonly DependenciesResolvedConditionSpec Readiness = new();

    private readonly TeamStore _teams;
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly AgentTeamService _builtInTeams;
    private readonly ILogger _logger;

    public TeamRunService(
        TeamStore teams,
        TicketService tickets,
        MemberService members,
        AgentTeamService builtInTeams,
        ILogger<TeamRunService> logger)
    {
        _teams = teams;
        _tickets = tickets;
        _members = members;
        _builtInTeams = builtInTeams;
        _logger = logger;
    }

    /// <summary>
    /// Definition behind a slug: a project-scoped definition wins over the built-in of the same
    /// name, so a project can specialize a team without forking the catalog.
    /// </summary>
    public async Task<TeamDefinition?> ResolveDefinitionAsync(string projectSlug, string teamSlug)
    {
        if (string.IsNullOrWhiteSpace(teamSlug)) return null;
        return await _teams.GetDefinitionAsync(projectSlug, teamSlug)
            ?? _builtInTeams.GetDefinitionBySlug(teamSlug);
    }

    /// <summary>
    /// Starts (or re-attaches to) a run of <paramref name="teamSlug"/> for
    /// <paramref name="parentTicketId"/> and fans it out.
    /// <para>
    /// Idempotent per (parent ticket, team): a trigger that fires again while the run is open
    /// returns the existing run instead of fanning out a second set of sub-tickets.
    /// </para>
    /// </summary>
    public async Task<TeamRun> StartRunAsync(string projectSlug, string teamSlug, int parentTicketId)
    {
        var definition = await ResolveDefinitionAsync(projectSlug, teamSlug)
            ?? throw new TeamStoreException("team_not_found", $"No team definition '{teamSlug}' in project '{projectSlug}'.");

        var existing = (await _teams.ListRunsAsync(projectSlug, parentTicketId, openOnly: true))
            .FirstOrDefault(run => string.Equals(run.TeamSlug, definition.Slug, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _logger.LogDebug(
                "[{Slug}] team '{Team}' already has run #{RunId} open on ticket #{TicketId} — reusing it",
                projectSlug, definition.Slug, existing.Id, parentTicketId);
            return await FanOutAsync(projectSlug, existing);
        }

        // A role bound to somebody who is not a member of this project would only surface halfway
        // through fan-out, as a sub-ticket that cannot be assigned — leaving a run with a truncated
        // graph that every later reconcile would fail on the same way. Check before anything exists.
        await AssertRolesAreMembersAsync(projectSlug, definition);

        // CreateRunAsync refuses an invalid or filter-only definition, so a run row never exists
        // for something that cannot be executed.
        var created = await _teams.CreateRunAsync(projectSlug, definition, parentTicketId);
        var run = await FanOutAsync(projectSlug, created);
        _logger.LogInformation(
            "[{Slug}] team '{Team}' run #{RunId} fanned out {Count} task(s) under ticket #{TicketId}",
            projectSlug, definition.Slug, run.Id, definition.TaskGraph.Count, parentTicketId);
        return run;
    }

    /// <summary>
    /// Every role the task graph actually uses must map to a member of the project, because the
    /// sub-ticket it produces is assigned to that member.
    /// </summary>
    private async Task AssertRolesAreMembersAsync(string projectSlug, TeamDefinition definition)
    {
        var members = (await _members.ListMembersAsync(projectSlug))
            .Select(member => member.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = definition.TaskGraph
            .Select(template => definition.FindRole(template.RoleId)?.AgentSlug)
            .Where(agentSlug => agentSlug is not null && !members.Contains(agentSlug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
            throw new TeamStoreException(
                "team_member_missing",
                $"Team '{definition.Slug}' needs member(s) {string.Join(", ", missing)}, which project '{projectSlug}' does not have.");
    }

    /// <summary>
    /// Materializes every task template that does not have a task row yet, in dependency order.
    /// <para>
    /// Re-entrant on purpose: a fan-out interrupted halfway (process killed between two sub-tickets)
    /// is finished by the next call rather than leaving a run with a truncated graph, and a run whose
    /// graph is already complete costs one query.
    /// </para>
    /// </summary>
    private async Task<TeamRun> FanOutAsync(string projectSlug, TeamRun run)
    {
        var definition = run.Definition;
        var tasks = await _teams.ListTasksAsync(projectSlug, run.Id);
        var done = new HashSet<string>(tasks.Select(task => task.TemplateKey), StringComparer.OrdinalIgnoreCase);
        foreach (var template in TopologicalOrder(definition))
        {
            if (done.Contains(template.Key)) continue;

            var role = definition.FindRole(template.RoleId);
            if (role is null)
            {
                // Validate() already rejects this; belt and braces so a hand-edited snapshot cannot
                // produce a sub-ticket assigned to nobody.
                throw new TeamStoreException(
                    "task_role_unknown",
                    $"Task '{template.Key}' of team '{definition.Slug}' references unknown role '{template.RoleId}'.");
            }

            // A task with blockers is born in the hold column: creating it in the dispatch column
            // would let the ordinary ticketInColumn automation start it before its blockers resolve.
            var status = template.DependsOn.Count == 0 ? ReadyStatus : HoldStatus;
            var ticket = await _tickets.CreateTicketAsync(
                projectSlug,
                template.Title,
                description: template.Prompt ?? "",
                createdBy: "automation",
                status: status,
                assignedTo: role.AgentSlug,
                parentId: run.ParentTicketId);

            var task = await _teams.AddTaskAsync(
                projectSlug,
                run.Id,
                new TeamTaskDraft(template.Key, role.RoleId, role.AgentSlug, ticket.Id)
                {
                    DependsOn = template.DependsOn
                });

            if (status == ReadyStatus)
                await _teams.UpdateTaskStatusAsync(projectSlug, task.Id, TeamTaskStatus.Dispatched);

            done.Add(template.Key);
        }

        return run.Status == TeamRunStatus.Pending
            ? await _teams.UpdateRunStatusAsync(projectSlug, run.Id, TeamRunStatus.Running)
            : run;
    }

    /// <summary>
    /// Brings one run back in line with the board: finishes a partial fan-out, records tasks whose
    /// sub-ticket reached a resolved status, and releases tasks whose blockers are now resolved.
    /// <para>
    /// This is the whole resume story. It reads the run row, its definition snapshot, its task rows
    /// and the live tickets, and needs nothing that was in memory before a restart.
    /// </para>
    /// </summary>
    public async Task<TeamRun?> ReconcileRunAsync(string projectSlug, long runId)
    {
        var run = await _teams.GetRunAsync(projectSlug, runId);
        if (run is null || !run.IsOpen) return run;

        var parent = await _tickets.GetTicketAsync(projectSlug, run.ParentTicketId);
        if (parent is null)
            return await CancelRunAsync(projectSlug, runId, "Parent ticket no longer exists.");
        if (ClosingParentStatuses.Contains(parent.Status))
            return await CancelRunAsync(projectSlug, runId, $"Parent ticket #{parent.Id} was closed ({parent.Status}).");

        run = await FanOutAsync(projectSlug, run);

        foreach (var task in await _teams.ListTasksAsync(projectSlug, run.Id))
        {
            if (!task.IsOpen) continue;

            var ticket = await _tickets.GetTicketAsync(projectSlug, task.TicketId);
            if (ticket is null)
            {
                await _teams.UpdateTaskStatusAsync(
                    projectSlug, task.Id, TeamTaskStatus.Cancelled,
                    failureReason: $"Sub-ticket #{task.TicketId} no longer exists.");
                continue;
            }

            if (Readiness.ResolvedStatuses.Contains(ticket.Status))
            {
                await _teams.UpdateTaskStatusAsync(projectSlug, task.Id, TeamTaskStatus.Done);
                continue;
            }

            if (task.Status != TeamTaskStatus.Pending) continue;

            // The one readiness rule in the system. An unresolved blocker holds the task where it is.
            if (!ConditionEvaluators.DependenciesResolved(Readiness, ticket.BlockedBy)) continue;

            if (!string.Equals(ticket.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase))
                await MoveAsync(projectSlug, ticket.Id, ReadyStatus);
            await _teams.UpdateTaskStatusAsync(projectSlug, task.Id, TeamTaskStatus.Dispatched);
        }

        return await _teams.GetRunAsync(projectSlug, runId);
    }

    /// <summary>Reconciles every open run of a project. Safe to call on every engine tick.</summary>
    public async Task ReconcileProjectAsync(string projectSlug)
    {
        IReadOnlyList<TeamRun> open;
        try { open = await _teams.ListRunsAsync(projectSlug, openOnly: true); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[{Slug}] could not list open team runs", projectSlug);
            return;
        }

        foreach (var run in open)
        {
            try { await ReconcileRunAsync(projectSlug, run.Id); }
            catch (Exception exception)
            {
                // One wedged run must not stop the others, nor the engine tick that called us.
                _logger.LogWarning(exception, "[{Slug}] team run #{RunId} could not be reconciled", projectSlug, run.Id);
            }
        }
    }

    /// <summary>
    /// Cancels a run and every task still open under it. Terminal tasks are left exactly as they
    /// are — a task that already reported Done stays Done — and a run that already reached a
    /// terminal state is returned unchanged, so a late cancellation cannot rewrite history.
    /// <para>
    /// Cancelling a task also moves its sub-ticket out of the dispatch column, because the board is
    /// what actually starts agents: a cancelled task left sitting in <see cref="ReadyStatus"/> would
    /// be picked up on the next tick regardless of its row.
    /// </para>
    /// </summary>
    public async Task<TeamRun?> CancelRunAsync(string projectSlug, long runId, string reason)
    {
        var run = await _teams.GetRunAsync(projectSlug, runId);
        if (run is null || !run.IsOpen) return run;

        foreach (var task in await _teams.ListTasksAsync(projectSlug, run.Id))
        {
            if (!task.IsOpen) continue;

            await _teams.UpdateTaskStatusAsync(
                projectSlug, task.Id, TeamTaskStatus.Cancelled, failureReason: reason);

            var ticket = await _tickets.GetTicketAsync(projectSlug, task.TicketId);
            if (ticket is null || Readiness.ResolvedStatuses.Contains(ticket.Status)) continue;
            if (!string.Equals(ticket.Status, ParkedStatus, StringComparison.OrdinalIgnoreCase))
                await MoveAsync(projectSlug, ticket.Id, ParkedStatus);
            try { await _tickets.AddActivityAsync(projectSlug, ticket.Id, $"Team run #{runId} cancelled: {reason}"); }
            catch { /* the row is already Cancelled; the activity line is a courtesy */ }
        }

        return await _teams.UpdateRunStatusAsync(projectSlug, runId, TeamRunStatus.Cancelled, failureReason: reason);
    }

    /// <summary>Cancels every open run bound to a parent ticket. The "cancel the parent" entry point.</summary>
    public async Task CancelRunsForParentAsync(string projectSlug, int parentTicketId, string reason)
    {
        foreach (var run in await _teams.ListRunsAsync(projectSlug, parentTicketId, openOnly: true))
            await CancelRunAsync(projectSlug, run.Id, reason);
    }

    // A project may have renamed or deleted the standard columns; MoveTicketAsync throws then.
    // A task that cannot be moved is logged rather than allowed to abort the whole reconcile.
    private async Task MoveAsync(string projectSlug, int ticketId, string status)
    {
        try { await _tickets.MoveTicketAsync(projectSlug, ticketId, status, "automation"); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception, "[{Slug}] could not move team sub-ticket #{TicketId} to '{Status}'",
                projectSlug, ticketId, status);
        }
    }

    /// <summary>
    /// Task templates in dependency order, so <see cref="TeamStore.AddTaskAsync"/> always finds the
    /// sibling it must draw an edge to. The graph is acyclic — <c>TeamDefinition.Validate()</c>
    /// rejects cycles before a definition can be stored or run.
    /// </summary>
    private static IEnumerable<TeamTaskTemplate> TopologicalOrder(TeamDefinition definition)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TeamTaskTemplate>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(TeamTaskTemplate template)
        {
            if (emitted.Contains(template.Key) || !visiting.Add(template.Key)) return;
            foreach (var key in template.DependsOn)
                if (definition.FindTask(key) is { } dependency)
                    Visit(dependency);
            visiting.Remove(template.Key);
            if (emitted.Add(template.Key)) ordered.Add(template);
        }

        foreach (var template in definition.TaskGraph)
            Visit(template);
        return ordered;
    }
}

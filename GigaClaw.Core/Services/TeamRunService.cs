using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

/// <summary>
/// The whole lifecycle of a <see cref="TeamRun"/>: fan-out, dispatch ordering, the join, the
/// synthesizer, and cancellation.
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
/// <para>
/// The join is the same idea one level up. <see cref="TeamJoinEvaluator"/> decides — purely, from
/// the task rows — whether the run may stop waiting; if it may, lanes that are still open are
/// cancelled, the synthesizer gets a sub-ticket naming both what reported and what is missing, and
/// the run's outcome is recomputed from the rows when that sub-ticket resolves. Nothing about the
/// join is remembered in memory either, which is why reconciling twice cannot dispatch the
/// synthesizer twice: the second pass sees a run already in <see cref="TeamRunStatus.Joining"/>
/// with a synthesis ticket, and only checks whether it is finished.
/// </para>
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
        return await StartRunAsync(projectSlug, definition, parentTicketId);
    }

    /// <summary>
    /// Starts (or re-attaches to) a run of a definition handed in directly, rather than looked up by
    /// slug. This is the entry point the <c>parallelRunAgents</c> action uses: the branches it
    /// declares are materialized into an <b>ad-hoc</b> definition that is never stored as a
    /// <c>TeamDefinition</c> row.
    /// <para>
    /// That works because a run already carries its own definition snapshot for restart-safe resume
    /// (see <see cref="TeamRun"/>) — the snapshot, not a definition row, is what every later
    /// reconcile reads. An ad-hoc run is therefore resumable on exactly the same terms as a named
    /// one, and inherits fan-out, join policy, cancellation and synthesize-with-gaps unchanged.
    /// </para>
    /// Idempotency is still per (parent ticket, team slug), so an ad-hoc definition needs a stable
    /// slug — see <c>ParallelRunAgentsActionSpec.RunSlug</c>.
    /// </summary>
    public async Task<TeamRun> StartRunAsync(string projectSlug, TeamDefinition definition, int parentTicketId)
    {
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
    /// Every role the run will actually assign work to — each task's role and the synthesizer's —
    /// must map to a member of the project, because the sub-ticket it produces is assigned to that
    /// member. The synthesizer is checked here, before fan-out, rather than at join time: a team
    /// whose synthesizer is not a member would otherwise run every lane and only then discover it
    /// has nobody to hand the results to.
    /// </summary>
    private async Task AssertRolesAreMembersAsync(string projectSlug, TeamDefinition definition)
    {
        var members = (await _members.ListMembersAsync(projectSlug))
            .Select(member => member.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = definition.TaskGraph
            .Select(template => template.RoleId)
            .Append(definition.SynthesizerRole)
            .Where(roleId => roleId is not null)
            .Select(roleId => definition.FindRole(roleId!)?.AgentSlug)
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
    /// <para>
    /// <see cref="TeamDefinition.MaxConcurrency"/> is enforced here and in the release loop by the
    /// same rule: an unblocked task is only born into the dispatch column while fewer than the cap
    /// are already there. Over the cap it waits in the hold column like a blocked task and is
    /// released by a later reconcile — the ceiling is expressed on the board, so it survives a
    /// restart with everything else.
    /// </para>
    /// </summary>
    private async Task<TeamRun> FanOutAsync(string projectSlug, TeamRun run)
    {
        var definition = run.Definition;
        var tasks = await _teams.ListTasksAsync(projectSlug, run.Id);
        var inFlight = tasks.Count(task => task.Status == TeamTaskStatus.Dispatched);
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
            // A task over the concurrency ceiling is held for the same reason — the board is what
            // starts agents, so "not yet" has to be a column, not a flag.
            var status = template.DependsOn.Count == 0 && HasSlot(definition, inFlight)
                ? ReadyStatus
                : HoldStatus;
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
            {
                await _teams.UpdateTaskStatusAsync(projectSlug, task.Id, TeamTaskStatus.Dispatched);
                inFlight++;
            }

            done.Add(template.Key);
        }

        return run.Status == TeamRunStatus.Pending
            ? await _teams.UpdateRunStatusAsync(projectSlug, run.Id, TeamRunStatus.Running)
            : run;
    }

    /// <summary>
    /// Brings one run back in line with the board: finishes a partial fan-out, records tasks whose
    /// sub-ticket reached a resolved status, releases tasks whose blockers are now resolved, and
    /// then asks the join policy whether the run may stop waiting.
    /// <para>
    /// This is the whole resume story. It reads the run row, its definition snapshot, its task rows
    /// and the live tickets, and needs nothing that was in memory before a restart.
    /// </para>
    /// <para>
    /// Idempotent: a terminal run is returned untouched, and a run already joining is only checked
    /// for whether its synthesizer finished — never joined or dispatched a second time.
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

        // The join already fired: the only thing left to watch is the synthesizer's own sub-ticket.
        // Returning here is what makes a second reconcile a no-op instead of a second synthesis.
        if (run.Status == TeamRunStatus.Joining)
            return await ContinueJoinAsync(projectSlug, run, parent);

        run = await FanOutAsync(projectSlug, run);

        var current = await _teams.ListTasksAsync(projectSlug, run.Id);
        var inFlight = current.Count(task => task.Status == TeamTaskStatus.Dispatched);
        foreach (var task in current)
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
                // A lane reports through its handoff, so the reference is captured at the moment the
                // lane finishes — the comment it points at can be edited later, but which handoff
                // this task produced cannot.
                await _teams.UpdateTaskStatusAsync(
                    projectSlug, task.Id, TeamTaskStatus.Done,
                    resultHandoffRef: HandoffRefOf(ticket));
                // A lane that just reported gives its slot back inside this same pass, so a capped
                // run does not have to wait a whole tick to start the next branch.
                if (task.Status == TeamTaskStatus.Dispatched) inFlight--;
                continue;
            }

            if (task.Status != TeamTaskStatus.Pending) continue;

            // The one readiness rule in the system. An unresolved blocker holds the task where it is.
            if (!ConditionEvaluators.DependenciesResolved(Readiness, ticket.BlockedBy)) continue;

            // Everything this task waits on is resolved, but the run may already have as many
            // branches in the dispatch column as it is allowed. It stays in the hold column and the
            // next reconcile picks it up when a slot frees.
            if (!HasSlot(run.Definition, inFlight)) continue;

            if (!string.Equals(ticket.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase))
                await MoveAsync(projectSlug, ticket.Id, ReadyStatus);
            await _teams.UpdateTaskStatusAsync(projectSlug, task.Id, TeamTaskStatus.Dispatched);
            inFlight++;
        }

        return await JoinAsync(projectSlug, runId, parent);
    }

    /// <summary>
    /// Reports that a lane failed: the task is recorded <see cref="TeamTaskStatus.Failed"/>, its
    /// sub-ticket is parked out of the dispatch column, and the join is evaluated straight away —
    /// which is what makes <see cref="TeamJoinMode.FirstFailure"/> stop the run "immediately".
    /// <para>
    /// Failure is reported <b>in</b> rather than sniffed out of an agent-run registry on purpose:
    /// that registry is in-memory, so a run that failed before a restart would silently look open
    /// forever. A row write is the only failure signal that survives.
    /// </para>
    /// A task or run that already reached a terminal state is returned unchanged.
    /// </summary>
    public async Task<TeamTask?> FailTaskAsync(string projectSlug, int ticketId, string reason)
    {
        var task = await _teams.GetTaskByTicketAsync(projectSlug, ticketId);
        if (task is null || !task.IsOpen) return task;

        var run = await _teams.GetRunAsync(projectSlug, task.TeamRunId);
        if (run is null || !run.IsOpen) return task;

        var failed = await _teams.UpdateTaskStatusAsync(
            projectSlug, task.Id, TeamTaskStatus.Failed, failureReason: reason);
        await ParkAsync(projectSlug, task.TicketId, $"Team run #{run.Id}: lane failed — {reason}");

        _logger.LogInformation(
            "[{Slug}] team run #{RunId} lane '{Task}' failed: {Reason}",
            projectSlug, run.Id, task.TemplateKey, reason);

        await ReconcileRunAsync(projectSlug, run.Id);
        return failed;
    }

    /// <summary>
    /// Whether the run may put one more task in the dispatch column. A definition with
    /// <see cref="TeamDefinition.MaxConcurrency"/> of 0 is unlimited, which is what every team
    /// declared before C5 is.
    /// </summary>
    private static bool HasSlot(TeamDefinition definition, int inFlight) =>
        definition.MaxConcurrency <= 0 || inFlight < definition.MaxConcurrency;

    // ── The join ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the policy whether the run may stop waiting. When it may: still-open lanes are cancelled
    /// (a join that fired is a decision, and leaving a lane running would let a cancelled branch keep
    /// spending tokens on an answer nobody will read), a receipt lands on the parent ticket, and the
    /// synthesizer — if the definition names one — gets a sub-ticket. Without a synthesizer the run
    /// goes straight to its terminal state.
    /// </summary>
    private async Task<TeamRun?> JoinAsync(string projectSlug, long runId, Ticket parent)
    {
        var run = await _teams.GetRunAsync(projectSlug, runId);
        if (run is null || !run.IsOpen || run.Status == TeamRunStatus.Joining) return run;

        var tasks = await _teams.ListTasksAsync(projectSlug, runId);
        var verdict = TeamJoinEvaluator.Evaluate(run.JoinPolicy, tasks);
        if (!verdict.Fires) return run;

        if (verdict.StillOpen.Count > 0)
        {
            foreach (var task in verdict.StillOpen)
                await CancelTaskAsync(
                    projectSlug, task,
                    $"the join fired before this lane finished ({run.JoinPolicy.Mode}: {verdict.Reason})");

            // Re-read: the lanes just cancelled carry their reason now, and that reason is exactly
            // what the synthesizer has to be told about each gap.
            var settled = await _teams.ListTasksAsync(projectSlug, runId);
            verdict = verdict with
            {
                Reported = [.. settled.Where(task => task.Status == TeamTaskStatus.Done)],
                Missing = [.. settled.Where(task => task.Status != TeamTaskStatus.Done)],
                StillOpen = []
            };
        }

        await NoteAsync(
            projectSlug, parent.Id,
            $"Team run #{runId} ({run.TeamSlug}) joined — {run.JoinPolicy.Mode}: {verdict.Reason}.");
        _logger.LogInformation(
            "[{Slug}] team run #{RunId} joined ({Mode}): {Reason}",
            projectSlug, runId, run.JoinPolicy.Mode, verdict.Reason);

        var synthesizer = run.SynthesizerRole is null ? null : run.Definition.FindRole(run.SynthesizerRole);
        if (synthesizer is null)
            return await FinalizeAsync(projectSlug, runId, parent);

        // Fail-fast: the branches that did report are worthless without the ones that did not, so
        // there is nothing worth synthesizing. The run closes on FinalizeAsync's receipt, which
        // names every gap — the decision changes what happens next, never whether it is recorded.
        if (!verdict.Success && run.Definition.PartialFailure == TeamPartialFailure.FailFast)
        {
            await NoteAsync(
                projectSlug, parent.Id,
                $"Team run #{runId} ({run.TeamSlug}) is fail-fast: skipping synthesis by '{synthesizer.AgentSlug}' "
                + $"because {verdict.Missing.Count} of {verdict.Reported.Count + verdict.Missing.Count} lane(s) did not report.");
            _logger.LogInformation(
                "[{Slug}] team run #{RunId} fail-fast: synthesizer '{Agent}' not dispatched",
                projectSlug, runId, synthesizer.AgentSlug);
            return await FinalizeAsync(projectSlug, runId, parent);
        }

        return await DispatchSynthesizerAsync(projectSlug, run, parent, synthesizer, verdict);
    }

    /// <summary>
    /// Creates the synthesizer's sub-ticket and hands the run to it. The brief carries a rendering
    /// of every reporting lane's handoff <b>and</b> a named list of the lanes that are missing, with
    /// the reason each one is: a synthesis that quietly drops a failed branch is worse than no
    /// synthesis, because it reads as complete.
    /// </summary>
    private async Task<TeamRun> DispatchSynthesizerAsync(
        string projectSlug,
        TeamRun run,
        Ticket parent,
        TeamRole synthesizer,
        TeamJoinVerdict verdict)
    {
        var reported = new List<(TeamTask Task, RunHandoff? Handoff)>();
        foreach (var task in verdict.Reported)
        {
            var ticket = await _tickets.GetTicketAsync(projectSlug, task.TicketId);
            reported.Add((task, ticket is null ? null : LatestHandoff(ticket)));
        }

        // C8: findings are already fully known once the join has fired — the dedup and the receipt
        // it drives do not need to wait for the dispatched synthesizer to read the brief.
        var deduped = run.Definition.DedupeFindings ? DedupeLaneFindings(reported) : null;
        if (deduped is not null)
            await PostSynthesisVerdictAsync(projectSlug, run, parent, verdict, deduped);

        var ticketForSynthesis = await _tickets.CreateTicketAsync(
            projectSlug,
            $"Synthesize: {parent.Title}",
            description: ComposeBrief(run, parent, verdict, reported, deduped),
            createdBy: "automation",
            status: ReadyStatus,
            assignedTo: synthesizer.AgentSlug,
            parentId: parent.Id);

        _logger.LogInformation(
            "[{Slug}] team run #{RunId} handed {Reported} lane(s) and {Missing} gap(s) to '{Agent}' on ticket #{TicketId}",
            projectSlug, run.Id, verdict.Reported.Count, verdict.Missing.Count,
            synthesizer.AgentSlug, ticketForSynthesis.Id);

        return await _teams.UpdateRunStatusAsync(
            projectSlug, run.Id, TeamRunStatus.Joining, synthesisTicketId: ticketForSynthesis.Id);
    }

    /// <summary>
    /// A run that already joined: it finishes when the synthesizer's sub-ticket resolves. A synthesis
    /// ticket that disappeared ends the run rather than leaving it waiting on nothing.
    /// </summary>
    private async Task<TeamRun?> ContinueJoinAsync(string projectSlug, TeamRun run, Ticket parent)
    {
        if (run.SynthesisTicketId is not int synthesisTicketId)
            return await FinalizeAsync(projectSlug, run.Id, parent);

        var ticket = await _tickets.GetTicketAsync(projectSlug, synthesisTicketId);
        if (ticket is null)
            return await _teams.UpdateRunStatusAsync(
                projectSlug, run.Id, TeamRunStatus.Failed,
                failureReason: $"Synthesis ticket #{synthesisTicketId} no longer exists.");

        if (!Readiness.ResolvedStatuses.Contains(ticket.Status)) return run;
        return await FinalizeAsync(projectSlug, run.Id, parent);
    }

    /// <summary>
    /// Closes the run, recomputing success from the task rows rather than from anything remembered:
    /// quorum runs succeed on the count they asked for, the other modes need every lane. Either way
    /// the outcome names the lanes that did not report, so a partial success leaves a receipt.
    /// </summary>
    private async Task<TeamRun> FinalizeAsync(string projectSlug, long runId, Ticket parent)
    {
        var run = await _teams.GetRunAsync(projectSlug, runId)
            ?? throw new TeamStoreException("run_not_found", $"Team run #{runId} does not exist.");
        var tasks = await _teams.ListTasksAsync(projectSlug, runId);
        var missing = tasks.Where(task => task.Status != TeamTaskStatus.Done).ToArray();
        var succeeded = TeamJoinEvaluator.Succeeded(run.JoinPolicy, tasks);

        if (run.Definition.RequireEvidenceCitingArbitration)
            await CloseLosingLanesAsync(projectSlug, run, tasks);

        var gaps = missing.Length == 0 ? null : TeamJoinEvaluator.DescribeMissing(missing);
        await NoteAsync(
            projectSlug, parent.Id,
            succeeded
                ? $"Team run #{runId} ({run.TeamSlug}) completed" + (gaps is null ? "." : $" with gaps: {gaps}.")
                : $"Team run #{runId} ({run.TeamSlug}) failed: {gaps ?? "no lane reported"}.");

        return await _teams.UpdateRunStatusAsync(
            projectSlug,
            runId,
            succeeded ? TeamRunStatus.Completed : TeamRunStatus.Failed,
            failureReason: succeeded ? null : gaps ?? "no lane reported");
    }

    /// <summary>
    /// C8's mechanically-enforceable half of "losing hypotheses closed with reasons": reads the
    /// debug-lead's <see cref="ArbitrationReader"/> marker off the synthesis ticket and, when one
    /// names a task in this run, posts a comment on every <em>other</em> reported lane's own ticket
    /// naming the winner and the stated reason. No marker (the lead never arbitrated, or this run
    /// has no synthesizer) is a no-op — the board is left exactly as C4 already would leave it.
    /// Called once, from <see cref="FinalizeAsync"/>, which "terminal is terminal" guarantees never
    /// runs twice for the same run.
    /// </summary>
    private async Task CloseLosingLanesAsync(string projectSlug, TeamRun run, IReadOnlyList<TeamTask> tasks)
    {
        if (run.SynthesisTicketId is not int synthesisTicketId) return;

        var synthesisTicket = await _tickets.GetTicketAsync(projectSlug, synthesisTicketId);
        if (synthesisTicket is null) return;

        var decision = ArbitrationReader.Latest(
            synthesisTicket.Comments.OrderBy(comment => comment.CreatedAt).Select(comment => comment.Content).ToList());
        if (decision is null) return;

        var winner = tasks.FirstOrDefault(task =>
            string.Equals(task.TemplateKey, decision.Winner, StringComparison.OrdinalIgnoreCase));
        if (winner is null) return; // The lead named a key that is not one of this run's lanes — nothing to close.

        foreach (var loser in tasks.Where(task =>
            task.Status == TeamTaskStatus.Done && task.Id != winner.Id))
        {
            try
            {
                await _tickets.AddCommentAsync(
                    projectSlug, loser.TicketId,
                    $"Hypothesis debug run #{run.Id}: '{winner.TemplateKey}' ({winner.AgentSlug}) was selected "
                    + $"as the winning hypothesis. This lane's hypothesis was not selected.\n\nReason: {decision.Reason}",
                    "automation");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception, "[{Slug}] team run #{RunId} could not close lane '{Key}' on ticket #{TicketId}",
                    projectSlug, run.Id, loser.TemplateKey, loser.TicketId);
            }
        }
    }

    /// <summary>
    /// The synthesizer's prompt. Reuses <see cref="HandoffReader.Render"/> — the same rendering a
    /// serial hand-off injects — so there is exactly one summary format in the system, and the
    /// gaps are stated as plainly as the results.
    /// </summary>
    private static string ComposeBrief(
        TeamRun run,
        Ticket parent,
        TeamJoinVerdict verdict,
        IReadOnlyList<(TeamTask Task, RunHandoff? Handoff)> reported,
        IReadOnlyList<DedupedFinding>? deduped)
    {
        var total = verdict.Reported.Count + verdict.Missing.Count;
        var brief = new System.Text.StringBuilder();
        brief.AppendLine(
            $"Synthesize team \"{run.Definition.Name}\" (run #{run.Id}) for ticket #{parent.Id} — {parent.Title}.");
        brief.AppendLine();
        brief.AppendLine($"Join: {run.JoinPolicy.Mode} — {verdict.Reason}.");
        brief.AppendLine();

        // C8: the deduped view comes first — a shorter, attributed summary of what every lane
        // agreed and disagreed on — with the full per-lane rendering kept right below it for
        // whatever the merge left out or a lane never expressed as an open loop.
        if (deduped is not null)
        {
            brief.AppendLine($"## Deduplicated findings ({deduped.Count})");
            if (deduped.Count == 0)
            {
                brief.AppendLine();
                brief.AppendLine("No parseable findings on any reporting lane's handoff.");
            }
            else
            {
                foreach (var finding in deduped)
                {
                    var lanes = string.Join(", ", finding.Lanes.Select(lane => $"{lane.TaskKey} ({lane.AgentSlug})"));
                    brief.AppendLine(
                        $"- {(finding.Blocking ? "[blocking] " : "")}{finding.Statement} — reported by: {lanes}");
                }
            }
            brief.AppendLine();
        }

        brief.AppendLine($"## Lanes that reported ({verdict.Reported.Count} of {total})");
        if (reported.Count == 0)
            brief.AppendLine().AppendLine("None. No lane produced a result.");
        foreach (var (task, handoff) in reported)
        {
            brief.AppendLine();
            brief.AppendLine($"### {task.TemplateKey} — {task.AgentSlug} (ticket #{task.TicketId})");
            brief.AppendLine(handoff is null
                ? $"No handoff artifact on ticket #{task.TicketId}; read the ticket for what this lane produced."
                : HandoffReader.Render(handoff));
        }

        if (verdict.Missing.Count > 0)
        {
            brief.AppendLine();
            brief.AppendLine($"## Lanes missing ({verdict.Missing.Count} of {total})");
            brief.AppendLine();
            foreach (var task in verdict.Missing)
                brief.AppendLine(
                    $"- {task.TemplateKey} — {task.AgentSlug} (ticket #{task.TicketId}): " +
                    $"{task.Status.ToString().ToLowerInvariant()} — {task.FailureReason ?? "no reason recorded"}");
            brief.AppendLine();
            brief.AppendLine(
                "These lanes produced nothing. Do not present their subject matter as covered: name "
                + "each gap and its reason in your synthesis, and say what would still have to be done.");
        }

        if (run.Definition.RequireEvidenceCitingArbitration)
        {
            brief.AppendLine();
            brief.AppendLine("## Arbitration required");
            brief.AppendLine();
            brief.AppendLine(
                "Pick the hypothesis the evidence above actually supports, citing which lane's evidence "
                + "decided it. Record the decision as its own comment on this ticket, in exactly this shape "
                + "(the host closes the losing lane(s) once it reads this — no further action needed there):");
            brief.AppendLine();
            brief.AppendLine("GIGACLAW-ARBITRATION v1 winner=<task-key-of-the-winning-lane>");
            brief.AppendLine("reason: <one line citing the deciding evidence>");
        }

        return brief.ToString().TrimEnd();
    }

    /// <summary>
    /// C8: extracts each reporting lane's <see cref="RunHandoff.OpenLoops"/> as raw
    /// <see cref="LaneFinding"/>s and merges them through <see cref="FindingDeduplicator"/>. A lane
    /// with no handoff, or a handoff with no open loops, simply contributes nothing — degrading to
    /// an empty (not missing) deduped list is the "no parseable findings" no-op the dedup opt-in
    /// promises for teams whose lanes don't happen to use open loops for findings.
    /// </summary>
    private static IReadOnlyList<DedupedFinding> DedupeLaneFindings(
        IReadOnlyList<(TeamTask Task, RunHandoff? Handoff)> reported) =>
        FindingDeduplicator.Dedupe(
            reported.Where(entry => entry.Handoff is not null)
                .SelectMany(entry => entry.Handoff!.OpenLoops.Select(loop =>
                    new LaneFinding(entry.Task.TemplateKey, entry.Task.AgentSlug, loop.Statement, loop.Blocking))));

    /// <summary>
    /// C8: distills the join's outcome and the deduped findings into a host-authored
    /// <c>GIGACLAW-VERDICT</c> receipt on the parent ticket, agent <c>team-synthesis</c> — a
    /// synthetic identity, not a claim that any real reviewer wrote it. This is what lets an
    /// ordinary <c>verdictIs</c> automation gate on a <c>parallel-review</c> run without parsing the
    /// dispatched synthesizer's own free-text comment: BLOCK when the join did not get what it
    /// asked for, FIX when it did but a deduped finding is blocking, SHIP otherwise.
    /// </summary>
    private async Task PostSynthesisVerdictAsync(
        string projectSlug,
        TeamRun run,
        Ticket parent,
        TeamJoinVerdict verdict,
        IReadOnlyList<DedupedFinding> deduped)
    {
        var allTasks = verdict.Reported.Concat(verdict.Missing).ToArray();
        if (allTasks.Length == 0) return;

        var blocking = deduped.Where(finding => finding.Blocking).ToArray();
        var decision = !verdict.Success ? "BLOCK" : blocking.Length > 0 ? "FIX" : "SHIP";
        var digest = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{run.TeamSlug}|{run.Id}|{parent.Id}")));

        var categories = allTasks.Select(task => new
        {
            name = task.TemplateKey,
            score = task.Status == TeamTaskStatus.Done ? 1 : 0,
            max = 1,
            notes = task.Status == TeamTaskStatus.Done
                ? $"{task.AgentSlug} reported."
                : $"{task.Status.ToString().ToLowerInvariant()} — {task.FailureReason ?? "no reason recorded"}"
        }).ToArray();

        var vetoItems = blocking.Select(finding => new
        {
            code = SlugifyVetoCode(finding.Key),
            statement = finding.Statement,
            evidenceRefs = new[] { digest }
        }).ToArray();

        var payload = new
        {
            schemaVersion = 1,
            agent = "team-synthesis",
            ticketId = parent.Id,
            verdict = decision,
            summary = $"{deduped.Count} deduplicated finding(s) across {verdict.Reported.Count} of {allTasks.Length} " +
                $"lane(s) reporting; {blocking.Length} blocking.",
            categories,
            vetoItems,
            evidence = new[] { new { kind = "hash", @ref = digest, note = (string?)"team run #" + run.Id } },
            reviewedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            inputDigest = digest
        };

        var comment =
            $"GIGACLAW-VERDICT v1 team-synthesis {decision} artifact-{digest}\n\n"
            + "```json\n"
            + System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            + "\n```";

        try { await _tickets.AddCommentAsync(projectSlug, parent.Id, comment, "automation"); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception, "[{Slug}] team run #{RunId} could not post its synthesis verdict on ticket #{TicketId}",
                projectSlug, run.Id, parent.Id);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex NonSlugChars = new(
        "[^a-z0-9-]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A finding's dedup key, reshaped into the slug a veto <c>code</c> must be.</summary>
    private static string SlugifyVetoCode(string findingKey)
    {
        var slug = NonSlugChars.Replace(findingKey.ToLowerInvariant().Replace('|', '-').Replace('.', '-').Replace('/', '-'), "-")
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "finding" : slug;
    }

    /// <summary>
    /// Marker identity of the newest readable handoff on a ticket, or null. Points at the comment
    /// the contract calls authoritative rather than copying it — see doc/handoff-contract.md.
    /// </summary>
    private static string? HandoffRefOf(Ticket ticket)
    {
        var handoff = LatestHandoff(ticket);
        return handoff is null ? null : $"ticket-{ticket.Id}/run-{handoff.RunId}";
    }

    private static RunHandoff? LatestHandoff(Ticket ticket) =>
        HandoffReader.Latest(ticket.Comments.OrderBy(c => c.CreatedAt).Select(c => c.Content).ToList());

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
            await CancelTaskAsync(projectSlug, task, reason);
        }

        // A run cancelled while the synthesizer had the floor must take its sub-ticket out of the
        // dispatch column too, for the same reason a task's is parked.
        if (run.SynthesisTicketId is int synthesisTicketId)
            await ParkAsync(projectSlug, synthesisTicketId, $"Team run #{runId} cancelled: {reason}");

        return await _teams.UpdateRunStatusAsync(projectSlug, runId, TeamRunStatus.Cancelled, failureReason: reason);
    }

    /// <summary>Cancels one open task and parks its sub-ticket. Shared by cancellation and the join.</summary>
    private async Task CancelTaskAsync(string projectSlug, TeamTask task, string reason)
    {
        await _teams.UpdateTaskStatusAsync(
            projectSlug, task.Id, TeamTaskStatus.Cancelled, failureReason: reason);
        await ParkAsync(projectSlug, task.TicketId, $"Team run #{task.TeamRunId}: lane cancelled — {reason}");
    }

    /// <summary>
    /// Moves a sub-ticket out of the dispatch column and leaves a receipt. A ticket that already
    /// resolved is left alone: the board is history, not a place to rewrite an outcome.
    /// </summary>
    private async Task ParkAsync(string projectSlug, int ticketId, string note)
    {
        var ticket = await _tickets.GetTicketAsync(projectSlug, ticketId);
        if (ticket is null || Readiness.ResolvedStatuses.Contains(ticket.Status)) return;
        if (!string.Equals(ticket.Status, ParkedStatus, StringComparison.OrdinalIgnoreCase))
            await MoveAsync(projectSlug, ticketId, ParkedStatus);
        await NoteAsync(projectSlug, ticketId, note);
    }

    /// <summary>Activity receipt. Never the reason a state transition fails — the row is the truth.</summary>
    private async Task NoteAsync(string projectSlug, int ticketId, string text)
    {
        try { await _tickets.AddActivityAsync(projectSlug, ticketId, text); }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "[{Slug}] could not write the activity line on ticket #{TicketId}", projectSlug, ticketId);
        }
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

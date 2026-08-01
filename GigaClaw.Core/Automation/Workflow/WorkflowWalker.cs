using Microsoft.Extensions.Logging;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation.Workflow;

/// <summary>What a gate condition decided, as a routing label rather than a bare bool.</summary>
/// <param name="Outcome">
/// The label a transition's <c>when</c> is matched against: <c>SHIP</c>/<c>FIX</c>/<c>BLOCK</c>/
/// <c>MISSING</c>/<c>INVALID</c>/<c>STALE</c> for a <c>verdictIs</c> gate — which is what makes
/// <c>verdictIs</c> the gate language rather than merely a gate — and <c>PASS</c>/<c>FAIL</c> for
/// every other condition in the vocabulary.
/// </param>
internal sealed record WorkflowGateResult(string Outcome, bool Matched, string? Diagnostic = null);

/// <summary>Evaluates a gate's <see cref="ConditionSpec"/> against one ticket.</summary>
internal delegate Task<WorkflowGateResult> WorkflowGateEvaluator(
    ProjectRuntime runtime, ConditionSpec gate, int subjectTicketId);

/// <summary>
/// Walks a ticket through its project's <see cref="WorkflowGraph"/>: enters states, dispatches the
/// ones that have work, routes the gates, fans out and joins, counts cycles and records the role
/// that handled every traversal.
/// <para>
/// <b>The board stays the system of record.</b> The walk has no table and no memory: every step is a
/// receipt comment on the ticket (see <see cref="WorkflowWalk"/>), and every pass replays those
/// receipts before doing anything. That is the C3 repair-loop pattern one level up — restart-proof
/// by construction rather than by a resume routine, and auditable by rereading the ticket.
/// </para>
/// <para>
/// <b>Nothing here executes agents.</b> A <c>task</c> state materializes a sub-ticket in the dispatch
/// column assigned to the state's role, exactly as a team run's lane does, and the ordinary per-agent
/// <c>ticketInColumn</c> automation starts it — which is what makes a walk's work queue behind
/// <c>RunConcurrencyGate</c>, take its file leases and honour its contract's worktree isolation for
/// free. A <c>fanOut</c> is handed to <see cref="TeamRunService"/> as an ad-hoc team, so C4/C5's
/// fan-out, dependency edges, join policy, cancellation and restart reconcile are reused rather than
/// re-implemented. A <c>gate</c> is evaluated through the ordinary <see cref="ConditionSpec"/> path.
/// </para>
/// <para>
/// <b>Fail closed with receipts.</b> An undecidable transition — a gate outcome no arm declares, a
/// gate that throws, a role nobody in the project fills, a fan-out that ended other than completed,
/// or a state entered more often than <see cref="WorkflowGraph.MaxCycles"/> allows — parks the ticket
/// in <see cref="ParkStatus"/> with a receipt carrying the whole walk history. The walk never stalls
/// silently and never loops without a bound.
/// </para>
/// </summary>
internal sealed class WorkflowWalker
{
    /// <summary>Column a task state's sub-ticket is born into: the one the dispatch automations watch.</summary>
    public const string DispatchStatus = TeamRunService.ReadyStatus;

    /// <summary>Column a parked walk leaves the ticket in. The owner's inbox.</summary>
    public const string ParkStatus = TeamRunService.HoldStatus;

    /// <summary>Statuses that mean a task state's sub-ticket reported. Same rule team lanes use.</summary>
    private static readonly string[] ResolvedStatuses = ["Done"];

    /// <summary>
    /// Transitions one pass may take before it stops. Gates and joins resolve without waiting, so a
    /// pass legitimately crosses several states; this only bounds a graph that somehow routes in a
    /// tight circle despite <see cref="WorkflowGraph.MaxCycles"/>, so a tick can never hang.
    /// </summary>
    private const int MaxTransitionsPerPass = 64;

    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly TeamRunService _teamRuns;
    private readonly WorkflowGateEvaluator _gate;
    private readonly ILogger _logger;

    public WorkflowWalker(
        TicketService tickets,
        MemberService members,
        TeamRunService teamRuns,
        WorkflowGateEvaluator gate,
        ILogger logger)
    {
        _tickets = tickets;
        _members = members;
        _teamRuns = teamRuns;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Advances every walking ticket of the project by as much as the board allows.
    /// <para>
    /// The candidate set is found by searching the tickets for the walk marker, not from a registry:
    /// the receipts are the index as well as the state, so a restart needs no rebuild and a project
    /// with no walks costs one query.
    /// </para>
    /// </summary>
    public async Task ReconcileProjectAsync(ProjectRuntime runtime, CancellationToken ct = default)
    {
        if (runtime.Workflow is null) return;

        List<TicketSummary> candidates;
        try
        {
            candidates = await _tickets.ListTicketsAsync(runtime.Slug, search: WorkflowWalk.MarkerPrefix);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[{Slug}] could not list walking tickets", runtime.Slug);
            return;
        }

        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await AdvanceAsync(runtime, candidate.Id, ct);
            }
            catch (Exception exception)
            {
                // One broken walk must not stop the others, and must not stop the engine tick.
                _logger.LogWarning(
                    exception, "[{Slug}] workflow walk on ticket #{TicketId} failed to advance",
                    runtime.Slug, candidate.Id);
            }
        }
    }

    /// <summary>
    /// Advances one ticket's walk as far as it can go without waiting, and returns where it stands.
    /// Re-derives everything from the ticket first, so calling it twice cannot repeat a completed
    /// state or dispatch the same one twice.
    /// </summary>
    public async Task<WorkflowWalkState> AdvanceAsync(ProjectRuntime runtime, int ticketId, CancellationToken ct = default)
    {
        var graph = runtime.Workflow;
        if (graph is null) return WorkflowWalkState.None;

        var ticket = await _tickets.GetTicketAsync(runtime.Slug, ticketId);
        if (ticket is null) return WorkflowWalkState.None;

        var walk = Replay(ticket);
        if (!walk.IsOpen) return walk;

        for (var guard = 0; guard < MaxTransitionsPerPass && walk.IsOpen; guard++)
        {
            if (ct.IsCancellationRequested) return walk;

            if (walk.Open is null)
            {
                var target = walk.Steps.Count == 0
                    ? walk.StartAt ?? graph.EntryState
                    : walk.Steps[^1].To;
                if (string.IsNullOrWhiteSpace(target))
                {
                    walk = await ParkAsync(
                        runtime, ticket, walk, walk.Steps.Count == 0 ? "(entry)" : walk.Steps[^1].State,
                        "the walk has nowhere to go: no target state was recorded.");
                    break;
                }

                walk = await EnterAsync(runtime, ticket, graph, walk, target!);
                continue;
            }

            var (advanced, next) = await TryLeaveAsync(runtime, ticket, graph, walk);
            walk = next;
            if (!advanced) break;
        }

        return walk;
    }

    /// <summary>Replays the walk from the ticket's comments. The only place walk state comes from.</summary>
    public static WorkflowWalkState Replay(Ticket ticket) =>
        WorkflowWalk.Replay(ticket.Comments.OrderBy(comment => comment.CreatedAt).Select(comment => comment.Content));

    // ── Entering ────────────────────────────────────────────────────────────

    private async Task<WorkflowWalkState> EnterAsync(
        ProjectRuntime runtime, Ticket ticket, WorkflowGraph graph, WorkflowWalkState walk, string target)
    {
        var state = graph.Find(target);
        if (state is null)
        {
            return await ParkAsync(
                runtime, ticket, walk, target,
                $"'{target}' is not a state of this workflow. The graph changed under a live walk.");
        }

        // The cycle bound, checked before the state is entered rather than after: a walk that has
        // already spent its budget must not dispatch one more round first. Escalating with the whole
        // argument attached is what C3 does when the repair budget runs out, for the same reason.
        if (walk.EntryCount(state.Name) > graph.MaxCycles)
        {
            return await ParkAsync(
                runtime, ticket, walk, state.Name,
                $"maxCycles is {graph.MaxCycles} and '{state.Name}' has already been entered "
                + $"{walk.EntryCount(state.Name)} time(s). The walk escalates to the owner instead of looping.",
                reasonCode: "max-cycles",
                includeHistory: true);
        }

        var step = walk.NextStep;
        var role = graph.TrackVisitedRoles ? state.Role : null;
        var inherited = walk.NewestSubject ?? ticket.Id;

        switch (state.Kind)
        {
            case WorkflowStateKind.Task:
            {
                var agent = state.Role;
                if (string.IsNullOrWhiteSpace(agent))
                    return await ParkAsync(runtime, ticket, walk, state.Name, $"task state '{state.Name}' names no role.");

                var members = (await _members.ListMembersAsync(runtime.Slug))
                    .Select(member => member.Slug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!members.Contains(agent))
                {
                    return await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"role '{agent}' is not a member of this project, so state '{state.Name}' has "
                        + "nobody to dispatch to.",
                        reasonCode: "role-not-dispatchable");
                }

                // Created before the receipt is written, and found back by its deterministic title:
                // a crash between the two re-enters the same step number, finds this sub-ticket and
                // adopts it rather than dispatching a second one.
                var subject = await FindOrCreateStateTicketAsync(runtime, ticket, step, state, agent!);
                return await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(step, WorkflowWalkEvent.Entered, state.Name)
                {
                    Kind = state.Kind,
                    Role = role,
                    Subject = subject.Id,
                    At = DateTime.UtcNow,
                });
            }

            case WorkflowStateKind.FanOut:
            {
                var definition = BuildFanOutDefinition(graph, state, step, out var problem);
                if (definition is null)
                    return await ParkAsync(runtime, ticket, walk, state.Name, problem!, reasonCode: "fan-out-undeclarable");

                Models.TeamRun run;
                try
                {
                    // Idempotent per (parent ticket, slug), and the slug carries the step number —
                    // so re-entering the same fan-out on a later cycle starts a new run while a
                    // repeated pass over the same step re-attaches to the one already open.
                    run = await _teamRuns.StartRunAsync(runtime.Slug, definition, ticket.Id);
                }
                catch (Exception exception)
                {
                    return await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"the fan-out could not be started: {exception.Message}",
                        reasonCode: "fan-out-failed");
                }

                return await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(step, WorkflowWalkEvent.Entered, state.Name)
                {
                    Kind = state.Kind,
                    Role = role,
                    Subject = inherited,
                    RunId = run.Id,
                    Branches = definition.TaskGraph.Select(task => task.Key).ToArray(),
                    At = DateTime.UtcNow,
                });
            }

            default:
                return await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(step, WorkflowWalkEvent.Entered, state.Name)
                {
                    Kind = state.Kind,
                    Role = role,
                    Subject = inherited,
                    At = DateTime.UtcNow,
                });
        }
    }

    // ── Leaving ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the state the walk is inside may be left yet. <c>advanced: false</c> means the
    /// walk is legitimately waiting (a sub-ticket that has not reported, a fan-out still running) —
    /// the pass stops without a receipt, because "still working" is not an event.
    /// </summary>
    private async Task<(bool Advanced, WorkflowWalkState Walk)> TryLeaveAsync(
        ProjectRuntime runtime, Ticket ticket, WorkflowGraph graph, WorkflowWalkState walk)
    {
        var open = walk.Open!;
        var state = graph.Find(open.State);
        if (state is null)
        {
            return (false, await ParkAsync(
                runtime, ticket, walk, open.State,
                $"'{open.State}' is no longer a state of this workflow."));
        }

        switch (state.Kind)
        {
            case WorkflowStateKind.Terminal:
                return (false, await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(open.Step, WorkflowWalkEvent.Finished, state.Name)
                {
                    Kind = state.Kind,
                    Role = open.Role,
                    Subject = open.Subject,
                    At = DateTime.UtcNow,
                }));

            case WorkflowStateKind.Task:
            {
                if (open.Subject is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name, "the state's sub-ticket was never recorded."));
                }

                var subject = await _tickets.GetTicketAsync(runtime.Slug, open.Subject.Value);
                if (subject is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"sub-ticket #{open.Subject} no longer exists, so the state can never report.",
                        reasonCode: "subject-missing"));
                }

                if (!ResolvedStatuses.Contains(subject.Status, StringComparer.OrdinalIgnoreCase))
                    return (false, walk); // Still being worked. Not an event.

                var arm = DefaultArm(state);
                if (arm is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"'{state.Name}' finished but declares no unconditional exit; "
                        + $"its arms are {DescribeArms(state)}.",
                        reasonCode: "no-exit"));
                }

                return (true, await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(open.Step, WorkflowWalkEvent.Left, state.Name)
                {
                    Kind = state.Kind,
                    Role = open.Role,
                    Subject = open.Subject,
                    Outcome = WorkflowWalk.DoneOutcome,
                    To = arm.To,
                    At = DateTime.UtcNow,
                }));
            }

            case WorkflowStateKind.Gate:
            {
                WorkflowGateResult result;
                try
                {
                    result = await _gate(runtime, state.Gate!, open.Subject ?? ticket.Id);
                }
                catch (Exception exception)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"the gate condition could not be evaluated: {exception.Message}",
                        reasonCode: "gate-error"));
                }

                var arm = state.Next.FirstOrDefault(transition =>
                        string.Equals(transition.When, result.Outcome, StringComparison.OrdinalIgnoreCase))
                    ?? state.Next.FirstOrDefault(transition => string.IsNullOrWhiteSpace(transition.When));
                if (arm is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"the gate resolved to {result.Outcome}"
                        + (string.IsNullOrWhiteSpace(result.Diagnostic) ? "" : $" ({result.Diagnostic})")
                        + $", which none of its arms declare; they are {DescribeArms(state)}.",
                        reasonCode: "gate-undecidable"));
                }

                return (true, await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(open.Step, WorkflowWalkEvent.Left, state.Name)
                {
                    Kind = state.Kind,
                    Role = open.Role,
                    Subject = open.Subject,
                    Outcome = result.Outcome,
                    To = arm.To,
                    At = DateTime.UtcNow,
                }));
            }

            case WorkflowStateKind.FanOut:
            {
                if (open.RunId is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name, "the fan-out's team run was never recorded."));
                }

                var run = await _teamRuns.GetRunAsync(runtime.Slug, open.RunId.Value);
                if (run is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"team run #{open.RunId} no longer exists, so the fan-out can never close.",
                        reasonCode: "run-missing"));
                }

                if (run.IsOpen) return (false, walk); // Branches still working.

                if (run.Status != Models.TeamRunStatus.Completed)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"team run #{run.Id} ended {run.Status}"
                        + (string.IsNullOrWhiteSpace(run.FailureReason) ? "" : $" ({run.FailureReason})")
                        + ", so the join cannot claim its branches reported.",
                        reasonCode: "fan-out-not-completed"));
                }

                var join = graph.States.FirstOrDefault(candidate =>
                    candidate.Kind == WorkflowStateKind.Join
                    && string.Equals(candidate.JoinOf, state.Name, StringComparison.OrdinalIgnoreCase));
                if (join is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"no join state closes fan-out '{state.Name}'.",
                        reasonCode: "join-missing"));
                }

                return (true, await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(open.Step, WorkflowWalkEvent.Left, state.Name)
                {
                    Kind = state.Kind,
                    Role = open.Role,
                    Subject = open.Subject,
                    RunId = open.RunId,
                    Branches = open.Branches,
                    Outcome = run.Status.ToString().ToUpperInvariant(),
                    To = join.Name,
                    At = DateTime.UtcNow,
                }));
            }

            default: // Join — a pass-through: its fan-out already closed before it was entered.
            {
                var arm = DefaultArm(state);
                if (arm is null)
                {
                    return (false, await ParkAsync(
                        runtime, ticket, walk, state.Name,
                        $"join '{state.Name}' declares no unconditional exit; its arms are {DescribeArms(state)}.",
                        reasonCode: "no-exit"));
                }

                return (true, await WriteAsync(runtime, ticket, walk, new WorkflowWalkStep(open.Step, WorkflowWalkEvent.Left, state.Name)
                {
                    Kind = state.Kind,
                    Role = open.Role,
                    Subject = open.Subject,
                    Outcome = "JOINED",
                    To = arm.To,
                    At = DateTime.UtcNow,
                }));
            }
        }
    }

    // ── Board effects ───────────────────────────────────────────────────────

    /// <summary>
    /// The sub-ticket a task state's work happens on. Keyed by <c>[wf:step:state]</c> in the title
    /// so it is found again after a crash — the alternative, remembering the id, is exactly the kind
    /// of in-memory progress this walker refuses to have.
    /// </summary>
    private async Task<Ticket> FindOrCreateStateTicketAsync(
        ProjectRuntime runtime, Ticket parent, int step, WorkflowState state, string agent)
    {
        var key = TicketKey(step, state.Name);
        var existing = (await _tickets.ListTicketsAsync(runtime.Slug, parentId: parent.Id))
            .FirstOrDefault(child => child.Title.StartsWith(key, StringComparison.Ordinal));
        if (existing is not null)
        {
            var reloaded = await _tickets.GetTicketAsync(runtime.Slug, existing.Id);
            if (reloaded is not null) return reloaded;
        }

        return await _tickets.CreateTicketAsync(
            runtime.Slug,
            $"{key} {state.Name}",
            description: state.Description ?? "",
            createdBy: "automation",
            status: DispatchStatus,
            assignedTo: agent,
            parentId: parent.Id);
    }

    internal static string TicketKey(int step, string state) => $"[wf:{step}:{state}]";

    /// <summary>
    /// Translates a fan-out state into the ad-hoc <see cref="TeamDefinition"/> it runs as — one role
    /// and one task per branch. The same trick <c>parallelRunAgents</c> plays, and for the same
    /// reason: the graph adds vocabulary, not a second execution engine.
    /// </summary>
    private static TeamDefinition? BuildFanOutDefinition(
        WorkflowGraph graph, WorkflowState fanOut, int step, out string? problem)
    {
        problem = null;
        var roles = new List<TeamRole>();
        var tasks = new List<TeamTaskTemplate>();

        foreach (var transition in fanOut.Next)
        {
            var branch = graph.Find(transition.To);
            if (branch is null)
            {
                problem = $"branch '{transition.To}' is not a state of this workflow.";
                return null;
            }

            if (branch.Kind != WorkflowStateKind.Task || string.IsNullOrWhiteSpace(branch.Role))
            {
                problem =
                    $"branch '{branch.Name}' is a {branch.Kind} state; a fan-out's branches must be "
                    + "task states with a role to dispatch to.";
                return null;
            }

            roles.Add(new TeamRole(branch.Name, branch.Role!));
            tasks.Add(new TeamTaskTemplate(branch.Name, branch.Name, $"{TicketKey(step, branch.Name)} {branch.Name}")
            {
                Prompt = branch.Description
            });
        }

        var definition = new TeamDefinition(
            SanitizeSlug($"wf-{step}-{fanOut.Name}"),
            $"Workflow fan-out '{fanOut.Name}'",
            $"Branches declared by workflow state '{fanOut.Name}'.",
            "⑂")
        {
            Roles = roles,
            TaskGraph = tasks,
            JoinPolicy = TeamJoinPolicy.AllDone
        };

        var problems = definition.Validate();
        if (problems.Count > 0)
        {
            problem = string.Join(" ", problems);
            return null;
        }

        return definition;
    }

    private static string SanitizeSlug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static WorkflowTransition? DefaultArm(WorkflowState state)
        => state.Next.FirstOrDefault(transition => string.IsNullOrWhiteSpace(transition.When))
            ?? (state.Next.Count == 1 ? state.Next[0] : null);

    private static string DescribeArms(WorkflowState state)
        => state.Next.Count == 0
            ? "(none)"
            : string.Join(", ", state.Next.Select(transition =>
                string.IsNullOrWhiteSpace(transition.When) ? $"→ {transition.To}" : $"{transition.When} → {transition.To}"));

    // ── Receipts ────────────────────────────────────────────────────────────

    private async Task<WorkflowWalkState> WriteAsync(
        ProjectRuntime runtime, Ticket ticket, WorkflowWalkState walk, WorkflowWalkStep step)
    {
        await _tickets.AddCommentAsync(
            runtime.Slug, ticket.Id, WorkflowWalk.Render(ticket.Id, step), WorkflowWalk.ReceiptAuthor);
        _logger.LogInformation(
            "[{Slug}] ticket #{TicketId} workflow: {Description}",
            runtime.Slug, ticket.Id, WorkflowWalk.Describe(step));
        return With(walk, step);
    }

    private async Task<WorkflowWalkState> ParkAsync(
        ProjectRuntime runtime,
        Ticket ticket,
        WorkflowWalkState walk,
        string state,
        string reason,
        string? reasonCode = null,
        bool includeHistory = true)
    {
        var step = new WorkflowWalkStep(walk.Open?.Step ?? walk.NextStep, WorkflowWalkEvent.Parked, state)
        {
            Kind = walk.Open?.Kind,
            Role = walk.Open?.Role,
            Subject = walk.Open?.Subject,
            Reason = reasonCode is null ? reason : $"{reasonCode}: {reason}",
            At = DateTime.UtcNow,
        };

        var prose = includeHistory
            ? $"{WorkflowWalk.Describe(step)}\n\nThe walk so far:\n{WorkflowWalk.RenderHistory(walk)}"
            : WorkflowWalk.Describe(step);

        await _tickets.AddCommentAsync(
            runtime.Slug, ticket.Id, WorkflowWalk.Render(ticket.Id, step, prose), WorkflowWalk.ReceiptAuthor);

        // The receipt is written before the move: a ticket that reaches the owner's column with no
        // explanation on it is worse than one that is still where it was.
        try
        {
            await _tickets.MoveTicketAsync(runtime.Slug, ticket.Id, ParkStatus, WorkflowWalk.ReceiptAuthor);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception, "[{Slug}] could not park ticket #{TicketId} in {Status}",
                runtime.Slug, ticket.Id, ParkStatus);
        }

        _logger.LogWarning(
            "[{Slug}] ticket #{TicketId} workflow parked at '{State}': {Reason}",
            runtime.Slug, ticket.Id, state, reason);
        return With(walk, step);
    }

    /// <summary>
    /// Folds a receipt the walker just wrote into the in-pass copy of the walk, by the same rule
    /// <see cref="WorkflowWalk.Replay"/> uses — so the pass and a fresh replay always agree.
    /// </summary>
    private static WorkflowWalkState With(WorkflowWalkState walk, WorkflowWalkStep step)
    {
        var steps = walk.Steps.Append(step).ToList();
        var closed = steps
            .Where(entry => entry.Event is WorkflowWalkEvent.Left or WorkflowWalkEvent.Parked or WorkflowWalkEvent.Finished)
            .Select(entry => entry.Step)
            .ToHashSet();
        var open = steps.LastOrDefault(entry => entry.Event == WorkflowWalkEvent.Entered && !closed.Contains(entry.Step));

        var status = step.Event switch
        {
            WorkflowWalkEvent.Parked => WorkflowWalkStatus.Parked,
            WorkflowWalkEvent.Finished => WorkflowWalkStatus.Finished,
            _ => WorkflowWalkStatus.Running,
        };

        return new WorkflowWalkState(status, steps) { Open = open, StartAt = walk.StartAt };
    }
}

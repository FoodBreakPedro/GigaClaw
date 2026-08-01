using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Runners;
using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Evaluates automation conditions and executes action sequences.
/// Owns the git semaphore and all Execute*ActionAsync helpers.
/// </summary>
internal sealed class ActionExecutor
{
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly LabelService _labels;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly IAgentRunner _runner;
    private readonly CostTracker _cost;
    private readonly LocalizationService _loc;
    private readonly ProjectService _projects;
    private readonly RunStateManager _runState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TeamRunService _teamRuns;
    private readonly ILogger _logger;
    private readonly OutboundApprovalGate _outboundGate;
    // R4: null means dispatch is unleased, exactly pre-R4 behavior — every existing test harness
    // that never passes this parameter keeps working unchanged. Production (AutomationEngine)
    // wires a real store backed by the per-project SQLite db.
    private readonly FileLeaseStore? _leases;
    // R6: null means enqueueMerge is a no-op (logged, not enqueued) — same "unwired = pre-feature
    // behavior" shape as _leases. Production (AutomationEngine) wires a real store.
    private readonly MergeQueueStore? _mergeQueue;
    private readonly MergeApprovalGate _mergeApproval;

    // Serializes in-process git operations per repository. Keyed by the git cwd so one
    // repo's slow/hung git (bounded by ProcessRunner's timeout) can't stall other projects.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gitLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _labelLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks in-flight action chains keyed by "{automationId}:{ticketId}".
    // Prevents concurrent chains for the same (automation, ticket) pair.
    private readonly ConcurrentDictionary<string, byte> _inFlightChains = new();

    public ActionExecutor(
        TicketService tickets,
        MemberService members,
        LabelService labels,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        IAgentRunner runner,
        CostTracker cost,
        LocalizationService loc,
        ProjectService projects,
        RunStateManager runState,
        IHttpClientFactory httpClientFactory,
        TeamRunService teamRuns,
        ILogger logger,
        OutboundApprovalGate? outboundGate = null,
        FileLeaseStore? leases = null,
        MergeQueueStore? mergeQueue = null,
        MergeApprovalGate? mergeApproval = null)
    {
        _tickets = tickets;
        _members = members;
        _labels = labels;
        _sessions = sessions;
        _runs = runs;
        _runner = runner;
        _cost = cost;
        _loc = loc;
        _projects = projects;
        _runState = runState;
        _httpClientFactory = httpClientFactory;
        _teamRuns = teamRuns;
        _logger = logger;
        // Fail closed: a caller that never wired a gate gets deny-all, not allow-all. Production
        // (AutomationEngine) passes a gate anchored on the owner's app settings.json.
        _outboundGate = outboundGate ?? new OutboundApprovalGate(static () => []);
        _leases = leases;
        _mergeQueue = mergeQueue;
        // Fail closed: a caller that never wired an approval gate gets deny-all (every project
        // held), not allow-all. Production (AutomationEngine) passes a gate anchored on the
        // owner's app settings.json, exactly like _outboundGate above.
        _mergeApproval = mergeApproval ?? new MergeApprovalGate(static () => []);
    }

    // ── Condition evaluation ────────────────────────────────────────────────

    public async Task<bool> ConditionsMatchAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing)
    {
        foreach (var cond in automation.Conditions)
        {
            var result = await EvaluateSingleConditionAsync(rt, cond, firing);
            if (cond.Negate) result = !result;
            if (!result) return false;
        }
        return true;
    }

    private Task<bool> EvaluateSingleConditionAsync(ProjectRuntime rt, ConditionSpec cond, TriggerFiring firing) =>
        cond switch
        {
            TicketInColumnConditionSpec c         => EvaluateTicketInColumnAsync(rt, c, firing),
            MinDescriptionLengthConditionSpec c    => EvaluateMinDescriptionLengthAsync(rt, c, firing),
            FieldLengthConditionSpec c             => EvaluateFieldLengthAsync(rt, c, firing),
            PriorityConditionSpec c                => EvaluatePriorityAsync(rt, c, firing),
            LabelsConditionSpec c                  => EvaluateLabelsAsync(rt, c, firing),
            AssignedToConditionSpec c              => EvaluateAssignedToAsync(rt, c, firing),
            HasParentConditionSpec c               => EvaluateHasParentAsync(rt, c, firing),
            AllSubTicketsInStatusConditionSpec c   => EvaluateAllSubTicketsInStatusAsync(rt, c, firing),
            TicketCountInColumnConditionSpec c     => EvaluateTicketCountInColumnAsync(rt, c, firing),
            TicketAgeConditionSpec c               => EvaluateTicketAgeAsync(rt, c, firing),
            VerdictIsConditionSpec c               => EvaluateVerdictIsAsync(rt, c, firing),
            RepairBudgetConditionSpec c            => EvaluateRepairBudgetAsync(rt, c, firing),
            DependenciesResolvedConditionSpec c    => EvaluateDependenciesResolvedAsync(rt, c, firing),
            _                                      => Task.FromResult(true),
        };

    // Signal-path firings (e.g. ticketCommentAdded) carry only the ticket id, so the status
    // is resolved live here; otherwise ticketInColumn could never pass on the signal path and
    // event-driven automations would only ever fire via the slow poll.
    private async Task<bool> EvaluateTicketInColumnAsync(ProjectRuntime rt, TicketInColumnConditionSpec c, TriggerFiring firing)
    {
        var status = firing.TicketStatus;
        if (status is null && firing.TicketId is not null)
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            if (ticket is null) return false;
            status = ticket.Status;
        }
        return ConditionEvaluators.TicketInColumn(c, status);
    }

    private async Task<bool> EvaluateMinDescriptionLengthAsync(ProjectRuntime rt, MinDescriptionLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        return ticket is not null && ConditionEvaluators.MinDescriptionLength(c, ticket.Description);
    }

    private async Task<bool> EvaluateFieldLengthAsync(ProjectRuntime rt, FieldLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.FieldLength(c, ticket.Title, ticket.Description);
    }

    private async Task<bool> EvaluatePriorityAsync(ProjectRuntime rt, PriorityConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.Priority(c, ticket.Priority);
    }

    private async Task<bool> EvaluateLabelsAsync(ProjectRuntime rt, LabelsConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.Labels(c, ticket.Labels.Select(l => l.Name).ToList());
    }

    private async Task<bool> EvaluateAssignedToAsync(ProjectRuntime rt, AssignedToConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.AssignedTo(c, ticket.AssignedTo);
    }

    private async Task<bool> EvaluateHasParentAsync(ProjectRuntime rt, HasParentConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.HasParent(c, ticket.ParentId);
    }

    private async Task<bool> EvaluateAllSubTicketsInStatusAsync(ProjectRuntime rt, AllSubTicketsInStatusConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.AllSubTicketsInStatus(c, ticket.SubTickets);
    }

    private async Task<bool> EvaluateTicketCountInColumnAsync(ProjectRuntime rt, TicketCountInColumnConditionSpec c, TriggerFiring firing)
    {
        string? slug = c.AssigneeSlug;
        if (c.SameAssignee)
        {
            if (firing.TicketId is null) return false;
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            slug = ticket?.AssignedTo;
            if (string.IsNullOrEmpty(slug)) return false;
        }

        var cols = c.Columns.Count > 0 ? c.Columns : new List<string> { "Todo", "InProgress" };
        int count = 0;
        foreach (var col in cols)
        {
            var list = await _tickets.ListTicketsAsync(rt.Slug, statusFilter: col);
            count += string.IsNullOrEmpty(slug) ? list.Count : list.Count(t => t.AssignedTo == slug);
        }
        return ConditionEvaluators.CompareCount(c.Operator, count, c.Value);
    }

    // The verdict gate never passes on missing data: no ticket, no comments and an unreadable
    // workspace all resolve to MISSING/STALE rather than to a silent "condition satisfied".
    private async Task<bool> EvaluateVerdictIsAsync(ProjectRuntime rt, VerdictIsConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;

        var agent = string.IsNullOrWhiteSpace(c.Agent)
            ? null
            : ConditionEvaluators.ResolveAgentPlaceholder(c.Agent, ticket.AssignedTo);
        if (c.Agent is not null && c.Agent.Contains("{assignee}") && agent is null)
            return false; // Placeholder with nothing to resolve to: fail closed.

        var resolution = VerdictScanner.Resolve(
            VerdictCommentsOf(ticket),
            agent,
            c.RequireFreshArtifact
                ? verdict => (VerdictReader.IsFresh(verdict, rt.Workspace, out var reason), reason)
                : null);

        if (resolution.Outcome is VerdictOutcome.Invalid or VerdictOutcome.Stale)
        {
            _logger.LogWarning(
                "[{Slug}] ticket #{TicketId}: {Outcome} verdict — {Diagnostic}",
                rt.Slug, ticket.Id, resolution.Outcome, resolution.Diagnostic);
        }

        return ConditionEvaluators.VerdictIs(c, resolution.Outcome);
    }

    // ── Repair loop (C3) ────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the repair loop still has a round left. The count is recounted from the
    /// ticket's comments on every evaluation rather than remembered, which is what makes a resumed
    /// or re-triggered run respect the cap instead of restarting it.
    /// </summary>
    private async Task<bool> EvaluateRepairBudgetAsync(ProjectRuntime rt, RepairBudgetConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;

        var agent = string.IsNullOrWhiteSpace(c.Agent)
            ? null
            : ConditionEvaluators.ResolveAgentPlaceholder(c.Agent, ticket.AssignedTo);
        if (c.Agent is not null && c.Agent.Contains("{assignee}") && agent is null)
            return false; // Placeholder with nothing to resolve to: fail closed.

        var state = await ResolveRepairStateAsync(rt, ticket, agent, c.MaxCycles);
        if (state is not null && state.Exhausted)
        {
            _logger.LogInformation(
                "[{Slug}] ticket #{TicketId}: repair budget spent ({Used}/{Max} FIX verdicts)",
                rt.Slug, ticket.Id, state.CyclesUsed, state.MaxCycles);
        }

        return ConditionEvaluators.RepairBudget(c, state);
    }

    /// <summary>
    /// Recounts the ticket's repair budget. Null means the budget could not be established
    /// (a contract manifest that exists but cannot be read) — callers treat that as exhausted.
    /// </summary>
    private async Task<RepairLoopState?> ResolveRepairStateAsync(
        ProjectRuntime rt,
        Models.Ticket ticket,
        string? agent,
        int? maxOverride)
    {
        var max = maxOverride ?? await ResolveMaxReviewCyclesAsync(rt, ticket.AssignedTo, agent);
        return max is null ? null : RepairLoop.Resolve(VerdictCommentsOf(ticket), max.Value, agent);
    }

    /// <summary>
    /// Resolves <c>maxReviewCycles</c> from the workspace contract manifest: the first of the given
    /// agents that declares one, then the manifest defaults, then
    /// <see cref="RepairLoop.DefaultMaxCycles"/>. A workspace with no manifest is not an error —
    /// most projects ship none — but a manifest that cannot be parsed returns null so the loop
    /// escalates rather than running on a guessed budget.
    /// </summary>
    private async Task<int?> ResolveMaxReviewCyclesAsync(ProjectRuntime rt, params string?[] agents)
    {
        if (string.IsNullOrWhiteSpace(rt.Workspace))
            return RepairLoop.DefaultMaxCycles;

        var manifestPath = Path.Combine(rt.Workspace, ".agents", "contracts.json");
        if (!File.Exists(manifestPath))
            return RepairLoop.DefaultMaxCycles;

        string manifest;
        try
        {
            manifest = await File.ReadAllTextAsync(manifestPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[{Slug}] could not read {Path} — the repair loop escalates instead of looping", rt.Slug, manifestPath);
            return null;
        }

        if (!RepairLoop.TryReadMaxCycles(manifest, agents, out var cycles))
        {
            _logger.LogWarning(
                "[{Slug}] {Path} is malformed — the repair loop escalates instead of looping", rt.Slug, manifestPath);
            return null;
        }

        return cycles ?? RepairLoop.DefaultMaxCycles;
    }

    private static List<VerdictComment> VerdictCommentsOf(Models.Ticket ticket)
        => ticket.Comments
            .OrderBy(x => x.CreatedAt)
            .Select(x => new VerdictComment(x.Content, x.Author, x.CreatedAt))
            .ToList();

    // A firing without a ticket has no dependency edges to consult, so it cannot claim to be
    // unblocked: this gate exists to hold work back, and fails closed when it cannot check.
    private async Task<bool> EvaluateDependenciesResolvedAsync(ProjectRuntime rt, DependenciesResolvedConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.DependenciesResolved(c, ticket.BlockedBy);
    }

    private async Task<bool> EvaluateTicketAgeAsync(ProjectRuntime rt, TicketAgeConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.TicketAge(c, ticket.CreatedAt, ticket.UpdatedAt, DateTime.UtcNow);
    }

    /// <summary>
    /// Prepends what the ticket already knows to the action's own context, so the next agent starts
    /// from what happened instead of re-deriving it: the outstanding <c>FIX</c> verdict's findings
    /// first (that is the reason this dispatch exists), then any unanswered GitHub owner feedback
    /// (C7 part 2 — the same mechanism, a different source of "you must address this"), then the
    /// previous run's handoff. An unreadable handoff, verdict or feedback comment is skipped rather
    /// than injected half-parsed, and a ticket with none of them dispatches exactly as before.
    /// </summary>
    internal async Task<string?> ComposeDispatchContextAsync(ProjectRuntime rt, int? ticketId, string? actionContext)
    {
        if (ticketId is null)
            return actionContext;

        RunHandoff? handoff = null;
        string? repairBrief = null;
        string? feedbackBrief = null;
        try
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, ticketId.Value);
            if (ticket is not null)
            {
                var bodies = ticket.Comments.OrderBy(c => c.CreatedAt).Select(c => c.Content).ToList();
                handoff = HandoffReader.Latest(bodies);

                // A FIX with no SHIP or escalation after it means a repair is outstanding: whoever
                // is dispatched next must see the categories and veto items that were refused.
                var repair = await ResolveRepairStateAsync(rt, ticket, agent: null, maxOverride: null);
                if (repair?.Newest is not null)
                    repairBrief = RepairLoop.RenderBrief(repair, $"ticket #{ticket.Id}");

                // Owner feedback rides the same rail rather than a second one: comments posted
                // since the agent last handed off are what this dispatch has to answer.
                var feedback = Github.OwnerFeedback.Outstanding(bodies);
                if (feedback.Count > 0)
                    feedbackBrief = Github.OwnerFeedback.RenderBrief(feedback, ticket.Id);
            }
        }
        catch (Exception exception)
        {
            // Context enrichment must never be the reason a dispatch fails.
            _logger.LogWarning(exception, "[{Slug}] could not read the ticket context for #{TicketId}", rt.Slug, ticketId);
        }

        if (handoff is null
            && string.IsNullOrWhiteSpace(repairBrief)
            && string.IsNullOrWhiteSpace(feedbackBrief))
            return actionContext;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(repairBrief)) parts.Add(repairBrief);
        if (!string.IsNullOrWhiteSpace(feedbackBrief)) parts.Add(feedbackBrief);
        if (handoff is not null) parts.Add(HandoffReader.Render(handoff));
        if (!string.IsNullOrWhiteSpace(actionContext)) parts.Add(actionContext);
        return string.Join("\n\n", parts);
    }

    // ── Action execution ────────────────────────────────────────────────────

    // ActionState (including the ChainValues output→input bag) lives in ActionTemplate.cs
    // alongside the render helper that consumes it.

    public async Task<AgentRun?> ExecuteAutomationAsync(
        ProjectRuntime rt,
        Automation automation,
        TriggerFiring firing,
        CancellationToken ct,
        ITrigger? trigger = null,
        TriggerContext? tctx = null)
    {
        string? chainKey = null;
        if (firing.TicketId is int tid)
            chainKey = $"{automation.Id}:{tid}";
        if (chainKey is not null && !_inFlightChains.TryAdd(chainKey, 0))
        {
            _logger.LogDebug("Chain {Key} already in flight — skipping", chainKey);
            return null;
        }

        var state = new ActionState();
        bool finalized = false;
        bool runAgentDispatched = false;
        bool detached = false;

        async Task FinalizeAsync(bool succeeded, DateTime? completedAt = null)
        {
            if (finalized || trigger is null || tctx is null) return;
            finalized = true;
            try { await trigger.CompleteFiringAsync(tctx, firing, succeeded, completedAt); }
            catch (Exception ex) { _logger.LogWarning(ex, "CompleteFiring failed for {Id}", automation.Id); }
        }

        // The engine tick awaits ExecuteAutomationAsync, so nothing here may block for long:
        // one long action would freeze trigger evaluation for every project. Fast actions run
        // inline; at the first long-running action (a consolidation subprocess, a PowerShell
        // script) the remaining chain is detached to a background task, guarded against
        // overlapping executions of the same (automation, ticket).
        async Task<AgentRun?> ExecuteFromAsync(int startIndex, bool background)
        {
            for (int i = startIndex; i < automation.Actions.Count; i++)
            {
                var action = automation.Actions[i];

                if (!background && action is ConsolidateAgentMemoryActionSpec or ExecutePowerShellActionSpec or HttpRequestActionSpec)
                {
                    var guardKey = chainKey ?? $"{automation.Id}:detached";
                    if (chainKey is null && !_inFlightChains.TryAdd(guardKey, 0))
                    {
                        _logger.LogDebug("Detached actions for {Id} already in flight — skipping", automation.Id);
                        return state.LastRun;
                    }
                    detached = true;
                    var idx = i;
                    _ = Task.Run(async () =>
                    {
                        try { await ExecuteFromAsync(idx, background: true); }
                        catch (OperationCanceledException) { /* engine shutdown */ }
                        catch (Exception ex) { _logger.LogWarning(ex, "Detached automation actions failed for {Id}", automation.Id); }
                        finally
                        {
                            // Mirrors the outer finally: a dispatched runAgent hands chain
                            // ownership to HandleRunCompletionAsync.
                            if (!runAgentDispatched) _inFlightChains.TryRemove(guardKey, out _);
                        }
                    }, CancellationToken.None);
                    return state.LastRun;
                }

                switch (action)
                {
                    case RunAgentActionSpec a:
                    {
                        var remaining = automation.Actions.Skip(i + 1).ToList();
                        var skip = await ExecuteRunAgentActionAsync(rt, automation, firing, a, ct, FinalizeAsync, state, remaining, chainKey);
                        runAgentDispatched = !skip;
                        // Whether skipped or dispatched, remaining actions are NOT processed here.
                        if (skip) return null;
                        return state.LastRun;
                    }
                    case MoveTicketStatusActionSpec m when firing.TicketId is not null:
                        await ExecuteMoveTicketStatusActionAsync(rt, firing, m, state, ct);
                        break;
                    case SetLabelsActionSpec s when firing.TicketId is not null:
                        await ExecuteSetLabelsActionAsync(rt, firing, s);
                        break;
                    case AddCommentActionSpec ac when firing.TicketId is not null:
                        await ExecuteAddCommentActionAsync(rt, firing, ac, state);
                        break;
                    case AssignTicketActionSpec at when firing.TicketId is not null:
                        await ExecuteAssignTicketActionAsync(rt, firing, at);
                        break;
                    case CommitAgentMemoryActionSpec cm:
                        await ExecuteCommitAgentMemoryActionAsync(rt, cm, firing);
                        break;
                    case ConsolidateAgentMemoryActionSpec csm:
                        await ExecuteConsolidateAgentMemoryActionAsync(rt, csm, firing, parentRun: null, ct);
                        break;
                    case ExecutePowerShellActionSpec ps:
                    {
                        var abort = await ExecutePowerShellAsync(ps, rt.Workspace!, rt.Slug, firing, state, ct);
                        if (abort) return state.LastRun;
                        break;
                    }
                    case HttpRequestActionSpec hr:
                    {
                        var abort = await ExecuteHttpRequestAsync(hr, rt.Slug, firing, state, automation.Id, ct);
                        if (abort) return state.LastRun;
                        break;
                    }
                    case CreateTicketActionSpec cta:
                        await ExecuteCreateTicketActionAsync(rt, cta, state);
                        break;
                    case StartTeamRunActionSpec str:
                        await ExecuteStartTeamRunActionAsync(rt, firing, str);
                        break;
                    case ParallelRunAgentsActionSpec pra:
                        await ExecuteParallelRunAgentsActionAsync(rt, firing, pra);
                        break;
                    case EnqueueMergeActionSpec em when firing.TicketId is not null:
                        await ExecuteEnqueueMergeActionAsync(rt, firing, em, ct);
                        break;
                    case StartWorkflowActionSpec sw:
                        await ExecuteStartWorkflowActionAsync(rt, firing, sw);
                        break;
                    default:
                        throw new NotSupportedException($"Unhandled action type {action.GetType().Name}. Register it in ActionExecutor.ExecuteAutomationAsync.");
                }
            }
            await FinalizeAsync(true, DateTime.UtcNow);
            return state.LastRun;
        }

        try
        {
            return await ExecuteFromAsync(0, background: false);
        }
        finally
        {
            if (chainKey is not null && !runAgentDispatched && !detached)
                _inFlightChains.TryRemove(chainKey, out _);
        }
    }

    // Returns true when the caller should abort (gate not passed).
    // When false, the run has been DISPATCHED (not awaited).
    private async Task<bool> ExecuteRunAgentActionAsync(
        ProjectRuntime rt,
        Automation automation,
        TriggerFiring firing,
        RunAgentActionSpec a,
        CancellationToken ct,
        Func<bool, DateTime?, Task> finalizeAsync,
        ActionState state,
        List<ActionSpec> remainingActions,
        string? chainKey)
    {
        var (skip, runTask, agentName, runId) = await StartAgentRunAsync(rt, firing, a, ct);
        if (skip || runTask is null) return true;

        var statusBefore = state.StatusBeforeMove;
        var statusAfter = state.StatusAfterMove;
        var assigneeBefore = state.AssigneeBeforeMove;
        // `state` (not a copy) is handed on: post-run actions must see the chain values published
        // by actions that ran before the agent, and publish their own for the ones after.
        _ = HandleRunCompletionAsync(runTask, rt, firing, a, agentName, runId!, statusBefore, statusAfter, assigneeBefore, remainingActions, finalizeAsync, state, chainKey, ct);
        state.LastRun = null;
        return false;
    }

    // Resolves the agent name, applies the skip gate, and starts the run (without awaiting it).
    // Returns skip=true when the run must not proceed (placeholder unresolved or gate skip);
    // otherwise runTask is the in-flight run and agentName the resolved slug.
    private async Task<(bool skip, Task<AgentRun>? runTask, string agentName, string? runId)> StartAgentRunAsync(
        ProjectRuntime rt,
        TriggerFiring firing,
        RunAgentActionSpec a,
        CancellationToken ct)
    {
        var agentName = a.Agent;
        if (agentName.Contains("{assignee}"))
        {
            if (firing.TicketId is null)
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but no ticketId in firing — skipping");
                return (true, null, agentName, null);
            }
            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            var assignee = t?.AssignedTo;
            if (string.IsNullOrEmpty(assignee))
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but ticket #{Id} has no assignee — skipping", firing.TicketId);
                return (true, null, agentName, null);
            }
            agentName = agentName.Replace("{assignee}", assignee);
        }

        // §5: a quarantined pack's agents are refused at dispatch. Checked after {assignee}
        // resolution so a roster-driven dispatch is caught on the slug it actually resolved to,
        // and before any run state is written so a refusal leaves no half-started run behind.
        var quarantineProject = await _projects.GetProjectAsync(rt.Slug);
        if (quarantineProject is not null)
        {
            var quarantine = PackQuarantine.ForWorkspace(_projects.ResolveWorkspacePath(quarantineProject));
            if (quarantine.PackOfAgent(agentName) is { } quarantinedPack)
            {
                _logger.LogWarning(
                    "Agent '{Agent}' belongs to pack '{Pack}', which is quarantined: it declares a " +
                    "pack-runtime this build has moved past. Dispatch refused until the pack is updated.",
                    agentName, quarantinedPack);
                return (true, null, agentName, null);
            }
        }

        var skillFile = $"{agentName}/SKILL.md";
        var group = string.IsNullOrEmpty(a.ConcurrencyGroup)
            ? agentName
            : ActionTemplate.Render(a.ConcurrencyGroup, ActionTemplate.Values(
                null,
                ("assignee", agentName),
                ("ticketId", firing.TicketId?.ToString() ?? "none")));

        if (await _runState.ShouldSkipAsync(rt, a, firing, agentName, group)) return (true, null, agentName, null);

        var project = await _projects.GetProjectAsync(rt.Slug);
        var fallbackModel = project?.FallbackModel;

        var effectiveModel = a.Model;
        var effectiveEnv = a.Env;
        string? ollamaValidationError = null;

        // Resolve model from member's DefaultModel if action model is null
        if (effectiveModel is null)
        {
            var member = await _members.GetMemberBySlugAsync(rt.Slug, agentName);
            var memberDefault = member?.DefaultModel ?? project?.LocalModelName;
            effectiveModel = string.IsNullOrWhiteSpace(memberDefault) ? null : memberDefault;
        }

        if (effectiveModel is not null && !effectiveModel.StartsWith("claude-"))
        {
            var baseUrl = project?.LocalModelBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ollamaValidationError = $"Local model '{effectiveModel}': LocalModelBaseUrl is not configured for this project";
            }
            else
            {
                var env = new Dictionary<string, string>(effectiveEnv)
                {
                    ["ANTHROPIC_BASE_URL"] = baseUrl,
                    ["ANTHROPIC_AUTH_TOKEN"] = "ollama",
                    ["ANTHROPIC_MODEL"] = effectiveModel,
                };
                effectiveEnv = env;
            }
        }

        var runId = Guid.NewGuid().ToString("N");

        // R4: lease the ticket's declared file scope before anything else about this dispatch is
        // recorded. A conflicting active lease means this dispatch does not proceed concurrently
        // with the one that holds it — see TryAcquireDispatchLeaseAsync for the block/warn split.
        var leaseGate = await TryAcquireDispatchLeaseAsync(rt, firing.TicketId, agentName, runId, ct);
        if (leaseGate.ShouldSkip)
            return (true, null, agentName, null);

        // R5: worktree isolation runs strictly AFTER the lease gate above — never around it. The
        // file lease (keyed on logical scope, not checkout path) is what actually protects the
        // eventual merge; checkout separation is a convenience for the running agent, not a
        // substitute — see FileLeaseStore's remarks and WorktreeManager's file header.
        string? executionPath = null;
        if (string.Equals(a.Isolation, "worktree", StringComparison.OrdinalIgnoreCase))
        {
            executionPath = await EnsureWorktreeIsolationAsync(rt, firing.TicketId, agentName, runId, ct);
            if (executionPath is null)
                return (true, null, agentName, null);
        }

        var runCtx = new ClaudeRunContext
        {
            PresetRunId = runId,
            ProjectSlug = rt.Slug,
            WorkspacePath = rt.Workspace!,
            ExecutionPath = executionPath,
            AgentName = agentName,
            SkillFile = skillFile,
            TicketId = firing.TicketId,
            TicketTitle = firing.TicketTitle,
            TicketStatus = firing.TicketStatus,
            MaxTurns = a.MaxTurns,
            ConcurrencyGroup = group,
            LockTimeoutMinutes = a.LockTimeoutMinutes,
            Env = effectiveEnv,
            Model = effectiveModel,
            FallbackModel = fallbackModel,
            ExtraContext = await ComposeDispatchContextAsync(rt, firing.TicketId, a.Context),
            RetryOnResumeFailure = true,
            OllamaValidationError = ollamaValidationError,
            MaxRunDuration = TimeSpan.FromMinutes(30),
        };
        _sessions.SetLastDispatched(rt.Workspace!, agentName, DateTime.UtcNow);
        if (firing.TicketId is not null)
        {
            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get("ActAgentStarted", agentName), "automation"); }
            catch { /* non-blocking */ }
        }

        _runs.ReserveCompletion(runId);
        try
        {
            return (false, _runner.RunAsync(runCtx, ct), agentName, runId);
        }
        catch
        {
            _runs.ReleaseCompletion(runId);
            await ReleaseDispatchLeaseAsync(rt.Slug, runId);
            throw;
        }
    }

    private async Task HandleRunCompletionAsync(
        Task<AgentRun> runTask,
        ProjectRuntime rt,
        TriggerFiring firing,
        RunAgentActionSpec spec,
        string agentName,
        string runId,
        string? statusBeforeMove,
        string? statusAfterMove,
        string? assigneeBeforeMove,
        List<ActionSpec> remainingActions,
        Func<bool, DateTime?, Task> finalizeAsync,
        ActionState state,
        string? chainKey,
        CancellationToken ct)
    {
        try
        {

        AgentRun run;
        try { run = await runTask; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "runAgent {Agent} crashed for ticket #{Id}", agentName, firing.TicketId);
            _runs.Complete(runId, AgentRunStatus.Failed, -1);
            await finalizeAsync(false, DateTime.UtcNow);
            return;
        }

        var runStatus = _runs.EffectiveStatus(run.RunId);
        if (firing.TicketId is not null)
        {
            var statusKey = runStatus switch
            {
                AgentRunStatus.Completed => "ActAgentCompleted",
                AgentRunStatus.Failed    => "ActAgentFailed",
                AgentRunStatus.Stopped   => "ActAgentStopped",
                _                        => "ActAgentCompleted",
            };
            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get(statusKey, agentName), "automation"); }
            catch { /* non-blocking */ }
        }

        if (spec.RestoreStatusOnFail
            && runStatus is AgentRunStatus.Failed or AgentRunStatus.Stopped
            && statusBeforeMove is not null && statusAfterMove is not null
            && firing.TicketId is not null)
        {
            try
            {
                var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticket is not null
                    && string.Equals(ticket.Status, statusAfterMove, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ticket.AssignedTo ?? "", assigneeBeforeMove ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId.Value, statusBeforeMove, "automation");
                    _logger.LogInformation("Restored #{Id} to {Status} (run {Agent} failed)",
                        firing.TicketId, statusBeforeMove, agentName);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore ticket #{Id} status", firing.TicketId); }
        }

        var postActionsSucceeded = await ProcessPostRunActionsAsync(
            rt, firing, run, remainingActions, finalizeAsync, state, ct);
        // Persist the outcome only after restoration and post-run actions. In particular,
        // restoreStatusOnFail updates the ticket; recording before it would make that engine
        // update look like owner feedback and reset ticketInColumn's retry counter.
        await finalizeAsync(
            runStatus == AgentRunStatus.Completed && postActionsSucceeded,
            DateTime.UtcNow);

        } // end try
        finally
        {
            _runs.ReleaseCompletion(runId);
            if (chainKey is not null)
                _inFlightChains.TryRemove(chainKey, out _);
            await ReleaseDispatchLeaseAsync(rt.Slug, runId);
        }
    }

    // Runs the side-effect actions that follow a runAgent. A second runAgent in the chain
    // (e.g. the judge that decides whether to advance the ticket) is dispatched here, awaited,
    // and its own trailing actions are processed recursively. Without this, the chained judge
    // run would never fire and tickets would stall in their column.
    private async Task<bool> ProcessPostRunActionsAsync(
        ProjectRuntime rt,
        TriggerFiring firing,
        AgentRun precedingRun,
        List<ActionSpec> actions,
        Func<bool, DateTime?, Task> finalizeAsync,
        ActionState state,
        CancellationToken ct)
    {
        var succeeded = true;
        for (int i = 0; i < actions.Count; i++)
        {
            var post = actions[i];
            try
            {
                switch (post)
                {
                    case CommitAgentMemoryActionSpec cm: await ExecuteCommitAgentMemoryActionAsync(rt, cm, firing); break;
                    case ConsolidateAgentMemoryActionSpec csm: await ExecuteConsolidateAgentMemoryActionAsync(rt, csm, firing, precedingRun, ct); break;
                    case AddCommentActionSpec ac when firing.TicketId is not null: await ExecuteAddCommentActionAsync(rt, firing, ac, state); break;
                    case SetLabelsActionSpec sl when firing.TicketId is not null: await ExecuteSetLabelsActionAsync(rt, firing, sl); break;
                    case AssignTicketActionSpec at when firing.TicketId is not null: await ExecuteAssignTicketActionAsync(rt, firing, at); break;
                    case ExecutePowerShellActionSpec ps: await ExecutePowerShellAsync(ps, rt.Workspace!, rt.Slug, firing, state, ct); break;
                    case HttpRequestActionSpec hr: await ExecuteHttpRequestAsync(hr, rt.Slug, firing, state, precedingRun.AgentName, ct); break;
                    // createTicket was missing here: a createTicket placed after a runAgent used to
                    // fall through the (previously default-less) switch and silently do nothing.
                    case CreateTicketActionSpec cta: await ExecuteCreateTicketActionAsync(rt, cta, state); break;
                    // A team run started after a runAgent is the normal shape: the producer decides
                    // the work needs a team, then the team fans out behind its ticket.
                    case StartTeamRunActionSpec str: await ExecuteStartTeamRunActionAsync(rt, firing, str); break;
                    // Same reasoning one level up: a producer finishes, then fans its output out
                    // into parallel branches behind the same ticket.
                    case ParallelRunAgentsActionSpec pra: await ExecuteParallelRunAgentsActionAsync(rt, firing, pra); break;
                    // enqueueMerge after a runAgent is the normal shape too: the committer role
                    // enqueues the ticket's worktree branch once the preceding run finished.
                    case EnqueueMergeActionSpec em when firing.TicketId is not null: await ExecuteEnqueueMergeActionAsync(rt, firing, em, ct); break;
                    case RunAgentActionSpec ra:
                    {
                        var (skip, runTask, agentName, runId) = await StartAgentRunAsync(rt, firing, ra, ct);
                        if (skip || runTask is null) return false;

                        try
                        {
                            AgentRun chainedRun;
                            try { chainedRun = await runTask; }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "chained runAgent {Agent} crashed for ticket #{Id}", agentName, firing.TicketId);
                                _runs.Complete(runId!, AgentRunStatus.Failed, -1);
                                return false;
                            }

                            var chainedStatus = _runs.EffectiveStatus(chainedRun.RunId);
                            if (firing.TicketId is not null)
                            {
                                var statusKey = chainedStatus switch
                                {
                                    AgentRunStatus.Completed => "ActAgentCompleted",
                                    AgentRunStatus.Failed    => "ActAgentFailed",
                                    AgentRunStatus.Stopped   => "ActAgentStopped",
                                    _                        => "ActAgentCompleted",
                                };
                                try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get(statusKey, agentName), "automation"); }
                                catch { /* non-blocking */ }
                            }

                            var rest = actions.Skip(i + 1).ToList();
                            var restSucceeded = await ProcessPostRunActionsAsync(
                                rt, firing, chainedRun, rest, finalizeAsync, state, ct);
                            return chainedStatus == AgentRunStatus.Completed && restSucceeded;
                        }
                        finally
                        {
                            _runs.ReleaseCompletion(runId!);
                            await ReleaseDispatchLeaseAsync(rt.Slug, runId!);
                        }
                    }
                    // Ticket-scoped actions whose `when firing.TicketId is not null` guard did not
                    // match. Nothing to do, and not a registration gap — keep them out of the
                    // default arm so they don't produce misleading warnings.
                    case AddCommentActionSpec:
                    case SetLabelsActionSpec:
                    case AssignTicketActionSpec:
                    case EnqueueMergeActionSpec:
                        break;
                    case MoveTicketStatusActionSpec:
                        // Deliberately unsupported after a runAgent: the pre-run move is what
                        // restoreStatusOnFail reverts, and a second move here would fight it.
                        _logger.LogDebug("Post-run moveTicketStatus is not supported after a runAgent — skipping");
                        break;
                    default:
                        // Previously this switch had no default arm, so an unregistered action type
                        // was a silent no-op. Fail loudly in the log instead.
                        _logger.LogWarning(
                            "Post-run action {Type} is not handled in ProcessPostRunActionsAsync — skipping. Register it there.",
                            post.GetType().Name);
                        break;
                }
            }
            catch (Exception ex)
            {
                succeeded = false;
                _logger.LogWarning(ex, "Post-run action {Type} failed", post.GetType().Name);
            }
        }
        return succeeded;
    }

    private async Task ExecuteMoveTicketStatusActionAsync(ProjectRuntime rt, TriggerFiring firing, MoveTicketStatusActionSpec m, ActionState state, CancellationToken ct)
    {
        if (string.Equals(firing.TicketStatus, m.To, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var ticketBefore = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            state.StatusBeforeMove = ticketBefore?.Status;
            state.AssigneeBeforeMove = ticketBefore?.AssignedTo;
            await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId!.Value, m.To, "automation");
            state.StatusAfterMove = m.To;

            // R6 (doc/roadmap/SESSION-HANDOFF.md, PLAN-remaining.md §1 item 3): worktree cleanup
            // used to be triggered from here only, which meant a user dragging a ticket to Done on
            // the Board UI (TicketService.ReorderTicketAsync — never routed through ActionExecutor)
            // silently orphaned the worktree. Cleanup now lives in TicketService itself
            // (TryCleanupWorktreeOnDoneAsync), called from every status-changing method — including
            // MoveTicketAsync, which the line above already awaits — so it runs exactly once here,
            // as part of that call, with no separate invocation needed on this path.
        }
        catch (Exception ex) { _logger.LogWarning(ex, "moveTicketStatus failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteSetLabelsActionAsync(ProjectRuntime rt, TriggerFiring firing, SetLabelsActionSpec s)
    {
        var labelLock = _labelLocks.GetOrAdd(rt.Slug, _ => new SemaphoreSlim(1, 1));
        await labelLock.WaitAsync();
        try
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            if (ticket is null) return;

            var allLabels = await _labels.ListLabelsAsync(rt.Slug);
            var byName = allLabels.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in s.Add.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!byName.ContainsKey(name))
                    byName[name] = await _labels.CreateLabelAsync(rt.Slug, name, "#6366f1");
            }

            var addIds = s.Add
                .Where(byName.ContainsKey)
                .Select(name => byName[name].Id)
                .Distinct()
                .ToList();
            var removeIds = s.Remove
                .Where(byName.ContainsKey)
                .Select(name => byName[name].Id)
                .Distinct()
                .ToList();
            await _tickets.PatchTicketLabelsAsync(
                rt.Slug, firing.TicketId.Value, addIds, removeIds, "automation");

            var parts = new List<string>();
            if (s.Add.Count > 0) parts.Add(_loc.Get("ActLabelsAdded", string.Join(", ", s.Add)));
            if (s.Remove.Count > 0) parts.Add(_loc.Get("ActLabelsRemoved", string.Join(", ", s.Remove)));
            if (parts.Count > 0)
                try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId!.Value, _loc.Get("ActLabelsChanged", string.Join(" / ", parts)), "automation"); }
                catch { /* non-blocking */ }
        }
        finally
        {
            labelLock.Release();
        }
    }

    private async Task ExecuteAddCommentActionAsync(ProjectRuntime rt, TriggerFiring firing, AddCommentActionSpec ac, ActionState? state = null)
    {
        try
        {
            // Renders {ticketId}/{ticketTitle} plus any chain values (e.g. {http.body.adminUrl})
            // captured by an earlier httpRequest — this is how a CMS receipt lands on the ticket.
            var content = ActionTemplate.Render(ac.Content, state, firing);
            var needsAssignee = content.Contains("{verdictHistory}", StringComparison.Ordinal)
                || content.Contains("{assignee}", StringComparison.Ordinal);
            if (needsAssignee)
            {
                var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                content = ActionTemplate.Render(content, ActionTemplate.Values(
                    null, ("assignee", ticket?.AssignedTo ?? "")));

                // {verdictHistory} is what makes the escalation comment self-contained: every
                // repair round's veto items and below-max categories, plus the receipt marker that
                // closes the episode, so the owner never has to open a run log to see the argument.
                if (content.Contains("{verdictHistory}", StringComparison.Ordinal) && ticket is not null)
                {
                    var repair = await ResolveRepairStateAsync(rt, ticket, agent: null, maxOverride: null)
                        ?? RepairLoop.Resolve(VerdictCommentsOf(ticket), RepairLoop.DefaultMaxCycles);
                    content = content.Replace(
                        "{verdictHistory}", RepairLoop.RenderEscalation(repair, ticket.Id), StringComparison.Ordinal);
                }
            }
            await _tickets.AddCommentAsync(rt.Slug, firing.TicketId!.Value, content, ac.Author);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "addComment failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteAssignTicketActionAsync(ProjectRuntime rt, TriggerFiring firing, AssignTicketActionSpec at)
    {
        try
        {
            var slug = at.Slug;
            if (slug is not null && slug.Contains("{previousAssignee}"))
            {
                var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                slug = slug.Replace("{previousAssignee}", ticket?.AssignedTo ?? "");
            }
            if (string.IsNullOrEmpty(slug))
            {
                await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: "", author: "automation");
            }
            else
            {
                var members = await _members.ListMembersAsync(rt.Slug);
                if (!members.Any(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("assignTicket: member '{Slug}' not found in project {Project}", slug, rt.Slug);
                    return;
                }
                await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: slug, author: "automation");
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "assignTicket failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteConsolidateAgentMemoryActionAsync(
        ProjectRuntime rt,
        ConsolidateAgentMemoryActionSpec spec,
        TriggerFiring? firing,
        AgentRun? parentRun,
        CancellationToken ct)
    {
        try
        {
            var agent = spec.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    _logger.LogInformation("consolidateAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return;
                }
                var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    _logger.LogInformation("consolidateAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping", firing.TicketId);
                    return;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            if (parentRun is not null
                && _runs.EffectiveStatus(parentRun.RunId) == AgentRunStatus.Failed
                && (_runs.EffectiveExitCode(parentRun.RunId) ?? 0) < 0)
            {
                _logger.LogInformation("consolidateAgentMemory: parent run {Id} failed (exit {Exit}) — skipping", parentRun.RunId, parentRun.ExitCode);
                return;
            }

            var instructionPath = Path.Combine(
                rt.Workspace!,
                spec.InstructionFile.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(instructionPath))
            {
                _logger.LogWarning("consolidateAgentMemory: instruction file not found: {Path}", instructionPath);
                return;
            }

            var instructionContent = (await File.ReadAllTextAsync(instructionPath, ct))
                .Replace("{agentSlug}", agent);
            var eventsSummary = BuildEventsSummary(parentRun);

            const string scope = "consolidate";
            _sessions.Clear(rt.Workspace!, $"{scope}:{agent}", ticketId: null);

            var runCtx = new ClaudeRunContext
            {
                ProjectSlug = rt.Slug,
                WorkspacePath = rt.Workspace!,
                AgentName = agent,
                SkillFile = $"{agent}/SKILL.md",
                MaxTurns = spec.MaxTurns,
                ConcurrencyGroup = $"consolidate-{agent}",
                InlineSkillContent = instructionContent,
                ExtraContext = string.IsNullOrWhiteSpace(eventsSummary)
                    ? "No events were recorded for this run."
                    : eventsSummary,
                SessionScope = scope,
                Model = null,
                RetryOnResumeFailure = true,
                MaxRunDuration = TimeSpan.FromMinutes(30),
            };

            var run = await _runner.RunAsync(runCtx, ct);

            var memoryPaths = $"\".agents/{agent}/memory\" \".agents/{agent}/memory.md\"";
            var diff = await RunGitAsync(rt.Workspace!, $"diff --shortstat HEAD -- {memoryPaths}");
            var diffSummary = diff.stdout.Trim();
            _logger.LogInformation("consolidate {Agent}: run {Status} (exit {Exit}){Diff}",
                agent, run.Status, run.ExitCode,
                string.IsNullOrWhiteSpace(diffSummary) ? "" : $" — {diffSummary}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "consolidateAgentMemory: failed for {Agent}", spec.Agent);
        }
    }

    private async Task ExecuteCreateTicketActionAsync(ProjectRuntime rt, CreateTicketActionSpec cta, ActionState? state = null)
    {
        try
        {
            var today = DateTime.Today;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var firstOfMonth = new DateTime(today.Year, today.Month, 1);
            string Resolve(string s) => ActionTemplate.Render(s, ActionTemplate.Values(
                state,
                ("date", today.ToString("yyyy-MM-dd")),
                ("monday", monday.ToString("yyyy-MM-dd")),
                ("firstOfMonth", firstOfMonth.ToString("yyyy-MM-dd"))));

            var title = Resolve(cta.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.LogWarning("createTicket: resolved title is empty — skipping");
                return;
            }

            if (cta.SkipIfExists)
            {
                var existing = await _tickets.ListTicketsAsync(rt.Slug);
                var openStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Backlog", "Todo", "InProgress", "Blocked", "Review" };
                if (existing.Any(t => openStatuses.Contains(t.Status) && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("createTicket: open ticket with title '{Title}' already exists — skipping", title);
                    return;
                }
            }

            List<int>? labelIds = null;
            if (cta.Labels.Count > 0)
            {
                var allLabels = await _labels.ListLabelsAsync(rt.Slug);
                labelIds = allLabels
                    .Where(l => cta.Labels.Any(n => string.Equals(n, l.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(l => l.Id)
                    .ToList();
            }

            var priority = Enum.TryParse<GigaClaw.Core.Models.TicketPriority>(cta.Priority, ignoreCase: true, out var p)
                ? p : GigaClaw.Core.Models.TicketPriority.NiceToHave;

            var ticket = await _tickets.CreateTicketAsync(
                rt.Slug,
                title,
                description: Resolve(cta.Description),
                createdBy: string.IsNullOrWhiteSpace(cta.CreatedBy) ? "automation" : cta.CreatedBy,
                status: cta.Status,
                labelIds: labelIds,
                priority: priority,
                assignedTo: string.IsNullOrWhiteSpace(cta.AssignedTo) ? null : cta.AssignedTo,
                parentId: cta.ParentId);

            _logger.LogInformation("createTicket: created ticket #{Id} '{Title}' in project {Project}", ticket.Id, ticket.Title, rt.Slug);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "createTicket failed in project {Project}", rt.Slug); }
    }

    /// <summary>
    /// Starts a team run against the firing ticket. The heavy lifting (fan-out, edges, release)
    /// lives in <see cref="TeamRunService"/>; this arm only resolves the team and reports the
    /// outcome on the ticket, so a failed start is visible on the board instead of only in the log.
    /// </summary>
    private async Task ExecuteStartTeamRunActionAsync(ProjectRuntime rt, TriggerFiring firing, StartTeamRunActionSpec spec)
    {
        if (firing.TicketId is null)
        {
            // A run is bound to a parent ticket by definition; a ticketless firing has nothing to
            // hang one on. Skip rather than throw — the rest of the chain is still meaningful.
            _logger.LogWarning("startTeamRun: no ticket in the firing — skipping team '{Team}'", spec.Team);
            return;
        }

        try
        {
            var run = await _teamRuns.StartRunAsync(rt.Slug, spec.Team, firing.TicketId.Value);
            _logger.LogInformation(
                "startTeamRun: team '{Team}' run #{RunId} is {Status} on ticket #{TicketId} in {Project}",
                run.TeamSlug, run.Id, run.Status, firing.TicketId, rt.Slug);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "startTeamRun failed for team '{Team}' in project {Project}", spec.Team, rt.Slug);
            try
            {
                await _tickets.AddActivityAsync(
                    rt.Slug, firing.TicketId.Value,
                    $"Team run '{spec.Team}' could not be started: {exception.Message}", "automation");
            }
            catch { /* non-blocking */ }
        }
    }

    /// <summary>
    /// Fans the firing ticket out into the declared parallel branches (C5 part 1).
    /// <para>
    /// This arm deliberately starts <b>no agent process</b>. It translates the spec into an ad-hoc
    /// team definition (<see cref="ParallelRunPlan"/>) and hands it to <see cref="TeamRunService"/>,
    /// which materializes one sub-ticket per branch in the dispatch column. The branches are then
    /// started by the ordinary per-agent <c>ticketInColumn</c> automations — the normal dispatch
    /// path, which is what makes each branch queue behind <c>RunConcurrencyGate</c> and take its
    /// file leases like every other run. A second execution engine here would have bypassed both.
    /// </para>
    /// </summary>
    private async Task ExecuteParallelRunAgentsActionAsync(
        ProjectRuntime rt, TriggerFiring firing, ParallelRunAgentsActionSpec spec)
    {
        if (firing.TicketId is null)
        {
            // Branches are sub-tickets of the firing ticket; a ticketless firing has no parent to
            // hang them on. Skip rather than throw — the rest of the chain is still meaningful.
            _logger.LogWarning("parallelRunAgents: no ticket in the firing — skipping {Count} branch(es)", spec.Branches.Count);
            return;
        }

        try
        {
            var definition = ParallelRunPlan.ToDefinition(spec);
            var run = await _teamRuns.StartRunAsync(rt.Slug, definition, firing.TicketId.Value);
            _logger.LogInformation(
                "parallelRunAgents: run #{RunId} is {Status} with {Branches} branch(es) (join {Join}, max concurrency {Max}) on ticket #{TicketId} in {Project}",
                run.Id, run.Status, definition.TaskGraph.Count, definition.JoinPolicy.Mode,
                definition.MaxConcurrency, firing.TicketId, rt.Slug);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "parallelRunAgents failed in project {Project}", rt.Slug);
            try
            {
                await _tickets.AddActivityAsync(
                    rt.Slug, firing.TicketId.Value,
                    $"Parallel branches could not be started: {exception.Message}", "automation");
            }
            catch { /* non-blocking */ }
        }
    }

    // ── C5 follow-up: the workflow walk ─────────────────────────────────────

    /// <summary>
    /// Opens a walk of the workspace's workflow graph on the firing ticket. Like
    /// <c>enqueueMerge</c>, this only records intent: it writes the walk's opening receipt and
    /// returns, and <c>WorkflowWalker</c>'s per-tick reconcile enters the first state. Doing the
    /// walking here would tie every later hop to this automation firing again — which it will not,
    /// because the things that unblock a walk (a sub-ticket reporting, a verdict arriving, a
    /// fan-out closing) are not this trigger.
    /// </summary>
    private async Task ExecuteStartWorkflowActionAsync(ProjectRuntime rt, TriggerFiring firing, StartWorkflowActionSpec spec)
    {
        if (firing.TicketId is null)
        {
            // A walk is a ticket's journey; a ticketless firing has nothing to walk. Skip rather
            // than throw — the rest of the chain is still meaningful.
            _logger.LogWarning("startWorkflow: no ticket in the firing — skipping");
            return;
        }

        var ticketId = firing.TicketId.Value;
        var graph = rt.Workflow;
        if (graph is null)
        {
            await NoteAsync(rt, ticketId,
                $"No workflow graph in this workspace ({Workflow.WorkflowGraphFile.FileName}), so there is nothing to walk.");
            return;
        }

        var ticket = await _tickets.GetTicketAsync(rt.Slug, ticketId);
        if (ticket is null)
        {
            _logger.LogWarning("startWorkflow: ticket #{TicketId} not found in project {Project}", ticketId, rt.Slug);
            return;
        }

        var walk = Workflow.WorkflowWalker.Replay(ticket);
        if (walk.IsOpen)
        {
            // Idempotent per ticket, exactly like startTeamRun: a repeating trigger re-attaches to
            // the walk in flight instead of restarting it from the entry state.
            _logger.LogDebug(
                "[{Slug}] ticket #{TicketId} is already walking (at '{State}') — re-attaching",
                rt.Slug, ticketId, walk.Open?.State ?? "(between states)");
            return;
        }

        var initial = string.IsNullOrWhiteSpace(spec.At) ? graph.EntryState : spec.At.Trim();
        if (initial is null || graph.Find(initial) is null)
        {
            await NoteAsync(rt, ticketId,
                $"Workflow walk refused: '{initial}' is not a state of this workspace's graph.");
            return;
        }

        var step = new Workflow.WorkflowWalkStep(0, Workflow.WorkflowWalkEvent.Started, graph.Find(initial)!.Name)
        {
            At = DateTime.UtcNow,
        };
        await _tickets.AddCommentAsync(
            rt.Slug, ticketId, Workflow.WorkflowWalk.Render(ticketId, step), Workflow.WorkflowWalk.ReceiptAuthor);
        _logger.LogInformation(
            "startWorkflow: ticket #{TicketId} in {Project} opened a walk at '{State}'", ticketId, rt.Slug, initial);
    }

    private async Task NoteAsync(ProjectRuntime rt, int ticketId, string text)
    {
        try { await _tickets.AddActivityAsync(rt.Slug, ticketId, text, "automation"); }
        catch (Exception exception) { _logger.LogWarning(exception, "[{Slug}] could not note on ticket #{TicketId}", rt.Slug, ticketId); }
    }

    /// <summary>
    /// Evaluates a workflow gate's condition and reports the <b>label</b> the graph routes on, not
    /// just whether it matched.
    /// <para>
    /// For <c>verdictIs</c> the label is the resolved verdict outcome — which is what makes
    /// <c>verdictIs</c> the gate language rather than merely one gate among many: a graph writes
    /// <c>when: "FIX"</c> and gets the repair arm, with <c>MISSING</c>/<c>INVALID</c>/<c>STALE</c>
    /// available for the same reason the condition exposes them, so prose instead of a verdict fails
    /// loudly. Every other condition in the vocabulary routes on <c>PASS</c>/<c>FAIL</c>, through the
    /// same evaluator the automations use — there is no second condition engine here.
    /// </para>
    /// </summary>
    internal async Task<Workflow.WorkflowGateResult> EvaluateWorkflowGateAsync(
        ProjectRuntime rt, ConditionSpec gate, int subjectTicketId)
    {
        var subject = await _tickets.GetTicketAsync(rt.Slug, subjectTicketId);
        var firing = new TriggerFiring(subjectTicketId, subject?.Title, subject?.Status);

        if (gate is VerdictIsConditionSpec verdictGate && subject is not null)
        {
            var agent = string.IsNullOrWhiteSpace(verdictGate.Agent)
                ? null
                : ConditionEvaluators.ResolveAgentPlaceholder(verdictGate.Agent, subject.AssignedTo);
            if (verdictGate.Agent is not null && verdictGate.Agent.Contains("{assignee}") && agent is null)
            {
                // Placeholder with nothing to resolve to: fail closed, the same as the condition does.
                return new Workflow.WorkflowGateResult(
                    "MISSING", false, $"the gate names agent '{verdictGate.Agent}' but the ticket has no assignee");
            }

            var resolution = VerdictScanner.Resolve(
                VerdictCommentsOf(subject),
                agent,
                verdictGate.RequireFreshArtifact
                    ? verdict => (VerdictReader.IsFresh(verdict, rt.Workspace, out var reason), reason)
                    : null);

            var matched = ConditionEvaluators.VerdictIs(verdictGate, resolution.Outcome);
            if (verdictGate.Negate) matched = !matched;
            return new Workflow.WorkflowGateResult(
                resolution.Outcome.ToString().ToUpperInvariant(), matched, resolution.Diagnostic);
        }

        var result = await EvaluateSingleConditionAsync(rt, gate, firing);
        if (gate.Negate) result = !result;
        return new Workflow.WorkflowGateResult(
            result ? Workflow.WorkflowWalk.PassOutcome : Workflow.WorkflowWalk.FailOutcome, result);
    }

    // ── R6: merge queue + integration gate ──────────────────────────────────

    /// <summary>
    /// Enqueues the firing ticket's R5 worktree branch onto the project's durable merge queue
    /// (<see cref="MergeQueueStore"/>). This method only records intent — it never rebases or
    /// merges inline; <see cref="Services.MergeQueueProcessor"/> drains the queue one candidate at a
    /// time. A ticket with no recorded worktree (never dispatched with <c>isolation: "worktree"</c>,
    /// or the worktree was already cleaned up) has nothing to merge and bounces immediately rather
    /// than enqueuing a candidate that can never rebase.
    /// </summary>
    private async Task ExecuteEnqueueMergeActionAsync(ProjectRuntime rt, TriggerFiring firing, EnqueueMergeActionSpec spec, CancellationToken ct)
    {
        if (_mergeQueue is null)
        {
            _logger.LogDebug("enqueueMerge: no merge queue store wired — skipping for ticket #{Id}", firing.TicketId);
            return;
        }

        var ticketId = firing.TicketId!.Value;
        try
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, ticketId);
            if (ticket is null)
            {
                _logger.LogWarning("enqueueMerge: ticket #{Id} not found in project {Project}", ticketId, rt.Slug);
                return;
            }

            if (string.IsNullOrWhiteSpace(ticket.WorktreeBranch) || string.IsNullOrWhiteSpace(ticket.WorktreePath))
            {
                var receipt = MergeReceipts.Bounced(
                    ticketId, ticket.WorktreeBranch, "no-worktree",
                    "Ticket has no recorded worktree branch — nothing to merge. Dispatch it with " +
                    "isolation: \"worktree\" before enqueueing a merge.");
                try { await _tickets.AddCommentAsync(rt.Slug, ticketId, receipt, "automation"); }
                catch (Exception ex) { _logger.LogWarning(ex, "enqueueMerge: failed to write merge-bounced receipt for ticket #{Id}", ticketId); }
                try { await _tickets.MoveTicketAsync(rt.Slug, ticketId, "Blocked", "automation"); }
                catch (Exception ex) { _logger.LogWarning(ex, "enqueueMerge: failed to move ticket #{Id} to Blocked", ticketId); }
                return;
            }

            var project = await _projects.GetProjectAsync(rt.Slug);
            // Per-automation override wins; absent falls back to the project-level setting; both
            // absent means the integration step is skipped (recorded on the eventual receipt), not
            // silently treated as green. Snapshotted now so a later edit to either setting cannot
            // change the gate under an already-queued candidate.
            var integrationCommand = string.IsNullOrWhiteSpace(spec.IntegrationCommand)
                ? project?.IntegrationCommand
                : spec.IntegrationCommand;

            // R3/R6 trust anchor: read fresh on every enqueue, never cached — see MergeApprovalGate.
            var approved = _mergeApproval.IsApproved(rt.Slug);
            var result = await _mergeQueue.EnqueueAsync(
                rt.Slug, ticketId, ticket.WorktreeBranch, integrationCommand, approved, DateTime.UtcNow, ct);

            // Only the FIRST time an entry lands in Held is worth a receipt — a repeated firing of
            // this action against a ticket that is already held (idempotent re-enqueue) must not
            // spam the same receipt on every poll.
            if (result.IsNew && result.Entry.State == MergeQueueState.Held)
            {
                var receipt = MergeReceipts.Held(ticketId, ticket.WorktreeBranch);
                try { await _tickets.AddCommentAsync(rt.Slug, ticketId, receipt, "automation"); }
                catch (Exception ex) { _logger.LogWarning(ex, "enqueueMerge: failed to write merge-held receipt for ticket #{Id}", ticketId); }
            }

            _logger.LogInformation(
                "enqueueMerge: ticket #{Id} branch {Branch} is {State} in project {Project}",
                ticketId, ticket.WorktreeBranch, result.Entry.State, rt.Slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "enqueueMerge failed for ticket #{Id} in project {Project}", ticketId, rt.Slug);
        }
    }

    private async Task ExecuteCommitAgentMemoryActionAsync(ProjectRuntime rt, CommitAgentMemoryActionSpec cm, TriggerFiring? firing = null)
    {
        try
        {
            var agent = cm.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    _logger.LogInformation("commitAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return;
                }
                var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    _logger.LogInformation("commitAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping", firing.TicketId);
                    return;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            var workspace = rt.Workspace!;
            // Memory lives either in the new per-topic layout (.agents/{agent}/memory/) or, until an
            // agent has consolidated, in the legacy flat file (.agents/{agent}/memory.md). Commit
            // whichever exist — both, during the migration window.
            var memoryDirAbs = Path.Combine(workspace, ".agents", agent, "memory");
            var legacyAbs = Path.Combine(workspace, ".agents", agent, "memory.md");
            var hasDir = Directory.Exists(memoryDirAbs);
            var hasLegacy = File.Exists(legacyAbs);
            if (!hasDir && !hasLegacy)
            {
                _logger.LogInformation("commitAgentMemory: no memory found for {Agent} under {Path}", agent, Path.GetDirectoryName(legacyAbs));
                return;
            }

            // Prefer a nested .agents/.git repo if present (decouples agent config from main project repo).
            // Otherwise fall back to the main workspace repo.
            var agentsDir = Path.Combine(workspace, ".agents");
            string gitCwd;
            string relBase;
            if (Directory.Exists(Path.Combine(agentsDir, ".git")))
            {
                gitCwd = agentsDir;
                relBase = $"{agent}";
            }
            else if (Directory.Exists(Path.Combine(workspace, ".git")))
            {
                gitCwd = workspace;
                relBase = $".agents/{agent}";
            }
            else
            {
                _logger.LogDebug("commitAgentMemory: no git repo at {Path} or {Agents} — skipping", workspace, agentsDir);
                return;
            }

            // Pathspecs cover the new memory/ dir (recursively) and the legacy flat file. Git tolerates
            // pathspecs for paths that don't exist on disk (e.g. only the dir is present), so list both.
            var pathArgs = $"\"{relBase}/memory\" \"{relBase}/memory.md\"";

            var gitLock = _gitLocks.GetOrAdd(gitCwd, _ => new SemaphoreSlim(1, 1));
            await gitLock.WaitAsync();
            try
            {
                var diff = await RunGitAsync(gitCwd, $"diff --quiet --exit-code -- {pathArgs}");
                // diff --quiet returns 1 when there are tracked-file changes; untracked new topic
                // files are invisible to it, so also check `status --porcelain` before bailing.
                var status = await RunGitAsync(gitCwd, $"status --porcelain -- {pathArgs}");
                if (diff.exitCode == 0 && string.IsNullOrWhiteSpace(status.stdout))
                {
                    _logger.LogDebug("commitAgentMemory: {Agent} memory is clean, nothing to commit", agent);
                    return;
                }

                var add = await RunGitAsync(gitCwd, $"add -- {pathArgs}");
                if (add.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git add failed for {Agent}: {Err}", agent, add.stderr);
                    return;
                }

                var ticketSuffix = firing?.TicketId is int tid ? $" (#{tid})" : "";
                var msg = $"chore(memory): {agent}{ticketSuffix}";
                // Dedicated identity so gitCommit-triggered automations (documentalist) can filter
                // memory commits via ignoreAuthors instead of relying on the ambient git identity.
                var commit = await RunGitAsync(gitCwd, $"-c user.name=\"GigaClaw Memory\" -c user.email=\"memory@gigaclaw.local\" commit --no-verify -m \"{msg}\" -- {pathArgs}");
                if (commit.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git commit failed for {Agent}: {Err}", agent, commit.stderr);
                    return;
                }

                _logger.LogInformation("commitAgentMemory: committed {Agent} memory", agent);
            }
            finally { gitLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "commitAgentMemory: failed to commit memory for {Agent}", cm.Agent);
        }
    }

    /// <summary>Upper bound on the raw stdout published as <c>{powershell.stdout}</c>, mirroring
    /// <see cref="MaxCapturedBodyChars"/> for httpRequest — a runaway script can't paste megabytes
    /// into a ticket comment.</summary>
    private const int MaxCapturedStdoutChars = 4096;

    // Returns true when AbortOnFailure is set and the process exited with a non-zero code.
    private async Task<bool> ExecutePowerShellAsync(ExecutePowerShellActionSpec spec, string workspacePath, string slug, TriggerFiring firing, ActionState? state, CancellationToken ct)
    {
        void Publish(string key, string value)
        {
            if (state is not null) state.ChainValues[key] = value;
        }

        // Set only for the inline-Script + Arguments case below; deleted in the finally so a
        // long-lived automation config never leaks one temp file per firing.
        string? tempScriptPath = null;
        try
        {
            string Render(string s) => ActionTemplate.Render(s, ActionTemplate.Values(
                state,
                ("ticketId", firing.TicketId?.ToString() ?? ""),
                ("ticketTitle", firing.TicketTitle ?? ""),
                ("slug", slug ?? "")));

            string scriptArg;
            if (!string.IsNullOrWhiteSpace(spec.ScriptFile))
            {
                var rendered = Render(spec.ScriptFile);
                var path = Path.IsPathRooted(rendered)
                    ? rendered
                    : Path.Combine(workspacePath, rendered);
                scriptArg = $"-File \"{path}\"";
            }
            else if (spec.Arguments.Count > 0)
            {
                // pwsh's -EncodedCommand does not accept trailing positional arguments — it errors
                // out with a "-File" usage message instead of exposing them as $args, unlike
                // -File <path> <args...>. Route inline Script + Arguments through a temp .ps1 file
                // so both ways of invoking a script behave identically.
                tempScriptPath = Path.Combine(Path.GetTempPath(), $"gigaclaw-ps-{Guid.NewGuid():N}.ps1");
                await File.WriteAllTextAsync(tempScriptPath, Render(spec.Script), ct);
                scriptArg = $"-File \"{tempScriptPath}\"";
            }
            else
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(Render(spec.Script));
                scriptArg = $"-EncodedCommand {Convert.ToBase64String(bytes)}";
            }

            var extraArgs = spec.Arguments.Count > 0
                ? " " + string.Join(" ", spec.Arguments.Select(a => $"\"{Render(a)}\""))
                : "";

            var pwshBin = ShellResolver.ResolvePowerShell();
            var res = await ProcessRunner.RunAsync(
                pwshBin,
                $"-NonInteractive -NoProfile {scriptArg}{extraArgs}",
                workspacePath,
                TimeSpan.FromSeconds(spec.TimeoutSeconds),
                spec.Env,
                ct);

            // Published unconditionally (success, non-zero exit, or timeout) — same "publish first,
            // ask questions later" shape as httpRequest's http.status/http.body — so a later
            // addComment can report what happened either way (e.g. a best-effort archive step).
            var stdout = res.Stdout.Trim();
            Publish("powershell.stdout", stdout.Length <= MaxCapturedStdoutChars
                ? stdout
                : stdout[..MaxCapturedStdoutChars] + "…");
            Publish("powershell.exitCode", res.ExitCode?.ToString() ?? "");

            if (res.TimedOut)
            {
                _logger.LogWarning("executePowerShell timed out after {Timeout}s; process tree killed", spec.TimeoutSeconds);
                return spec.AbortOnFailure;
            }

            _logger.LogInformation("executePowerShell exited {Code}. stdout={Stdout} stderr={Stderr}",
                res.ExitCode, res.Stdout.Trim(), res.Stderr.Trim());

            if (res.ExitCode != 0)
            {
                _logger.LogWarning("executePowerShell non-zero exit ({Code}); abortOnFailure={Abort}", res.ExitCode, spec.AbortOnFailure);
                return spec.AbortOnFailure;
            }
        }
        catch (OperationCanceledException)
        {
            // Engine shutdown / chain cancellation — the process tree was already killed.
            _logger.LogWarning("executePowerShell cancelled");
            if (spec.AbortOnFailure) return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "executePowerShell failed");
            Publish("powershell.stdout", "");
            Publish("powershell.exitCode", "");
            if (spec.AbortOnFailure) return true;
        }
        finally
        {
            if (tempScriptPath is not null)
            {
                try { File.Delete(tempScriptPath); } catch { /* best-effort cleanup */ }
            }
        }
        return false;
    }

    // ── httpRequest ─────────────────────────────────────────────────────────

    /// <summary>Upper bound on the raw body published as <c>{http.body}</c>, so a large response
    /// can't be pasted wholesale into a ticket comment.</summary>
    private const int MaxCapturedBodyChars = 8192;

    /// <summary>
    /// Performs the outbound request and publishes <c>http.status</c>, <c>http.body</c> and the
    /// flattened <c>http.body.&lt;field&gt;</c> values into the chain. When <see cref="HttpRequestActionSpec.BodyTemplate"/>
    /// references <c>{draft.*}</c>, the firing ticket's description is fetched and parsed as
    /// <see cref="DraftFrontmatter"/> first — a parse failure fails the action the same way a
    /// failed request does, without ever sending it. Returns true when the caller should abort
    /// the rest of the chain (failure + AbortOnFailure).
    /// </summary>
    private async Task<bool> ExecuteHttpRequestAsync(
        HttpRequestActionSpec spec,
        string slug,
        TriggerFiring firing,
        ActionState? state,
        string actor,
        CancellationToken ct)
    {
        // Substitution always targets locals — the spec objects are the shared, mutable instances
        // held by the chain snapshot and by the on-disk config, and must never be written to.
        string Render(string? s) => ActionTemplate.Render(s, ActionTemplate.Values(
            state,
            ("ticketId", firing.TicketId?.ToString() ?? ""),
            ("ticketTitle", firing.TicketTitle ?? ""),
            ("slug", slug ?? ""),
            ("projectSlug", slug ?? "")));

        void Publish(string key, string value)
        {
            if (state is not null) state.ChainValues[key] = value;
        }

        // Deterministic values so a template referencing {http.status} never renders the raw
        // placeholder when the request never produced a response.
        Publish("http.status", "0");
        Publish("http.body", "");

        // Posts the failure receipt (comment + status move) this spec was configured with, then
        // returns whether the caller should abort the rest of the chain. Shared by every failure
        // exit below — non-2xx, transport/timeout, bad URL, and frontmatter parse failure — so
        // FailureComment/FailureStatus behave identically regardless of which one fired.
        async Task<bool> FailAsync(string httpError)
        {
            Publish("http.error", httpError);
            if (firing.TicketId is int failedTicketId)
            {
                if (!string.IsNullOrWhiteSpace(spec.FailureComment))
                {
                    var content = ActionTemplate.Render(spec.FailureComment, state, firing);
                    try { await _tickets.AddCommentAsync(slug!, failedTicketId, content, "automation"); }
                    catch (Exception ex) { _logger.LogWarning(ex, "httpRequest: failed to post FailureComment for ticket #{Id}", failedTicketId); }
                }
                if (!string.IsNullOrWhiteSpace(spec.FailureStatus))
                {
                    try { await _tickets.MoveTicketAsync(slug!, failedTicketId, spec.FailureStatus, "automation"); }
                    catch (Exception ex) { _logger.LogWarning(ex, "httpRequest: failed to apply FailureStatus for ticket #{Id}", failedTicketId); }
                }
            }
            return spec.AbortOnFailure;
        }

        var url = Render(spec.Url).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("httpRequest: '{Url}' is not an absolute http(s) URL — skipping", url);
            return await FailAsync($"invalid URL '{url}'");
        }

        // U17/R3 host-side preflight. The trust anchor is the owner's app-level settings.json —
        // never a ticket label, which agents holding board-write can set themselves. Without a
        // trusted approval for this host, the action is a dry run: logged and receipted, but
        // nothing leaves the process. The approved-host list is read per execution, so an owner
        // edit takes effect on the next firing without an engine restart.
        var approval = _outboundGate.Evaluate(url);
        if (!approval.MaySend)
        {
            var reason = approval.Reason ?? "no trusted outbound approval";
            Publish("http.dryRun", "true");
            Publish("http.error", reason);
            await WriteOutboundDenialReceiptAsync(slug, firing, actor, url, uri.Host, reason);
            // Not sent means no response: actions downstream that assume a successful send must
            // not run when the spec opted into abort-on-failure. The receipt above — not the
            // spec's FailureComment/FailureStatus — is the record, because a dry run is the
            // configured behavior of an unapproved host, not a dispatch failure.
            return spec.AbortOnFailure;
        }

        var timeout = TimeSpan.FromSeconds(spec.TimeoutSeconds > 0 ? spec.TimeoutSeconds : 30);

        try
        {
            // {draft.*} is only recognized in BodyTemplate — a frontmatter parse failure must
            // block dispatch (never POST a malformed draft) rather than silently leaving the
            // placeholders un-rendered.
            var bodyTemplate = spec.BodyTemplate ?? "";
            string body;
            if (bodyTemplate.Contains("{draft.", StringComparison.Ordinal))
            {
                var ticket = firing.TicketId is int draftTicketId
                    ? await _tickets.GetTicketAsync(slug, draftTicketId)
                    : null;
                if (!DraftFrontmatter.TryParse(ticket?.Description, out var draft, out var parseError))
                {
                    _logger.LogWarning(
                        "httpRequest: draft frontmatter parse failed for ticket #{Id}: {Error} — request not sent",
                        firing.TicketId, parseError);
                    return await FailAsync($"frontmatter: {parseError}");
                }

                var values = ActionTemplate.Values(state,
                    ("ticketId", firing.TicketId?.ToString() ?? ""),
                    ("ticketTitle", firing.TicketTitle ?? ""),
                    ("slug", slug ?? ""),
                    ("projectSlug", slug ?? ""));
                foreach (var (key, value) in draft!.ToJsonEscapedValues())
                    values[key] = value;
                body = ActionTemplate.Render(bodyTemplate, values);
            }
            else
            {
                body = Render(bodyTemplate);
            }

            using var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(spec.Method) ? "POST" : spec.Method.Trim().ToUpperInvariant()),
                uri);

            string? contentType = null;
            var hasAuthorization = false;
            foreach (var (name, rawValue) in spec.Headers)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var value = Render(rawValue);
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = value;
                    continue;
                }
                if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                    hasAuthorization = true;
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    _logger.LogWarning("httpRequest: header '{Header}' was rejected — skipping it", name);
            }

            if (!string.IsNullOrWhiteSpace(spec.SecretRef))
            {
                // Only the variable NAME is ever logged; the token itself is never written anywhere.
                var token = Environment.GetEnvironmentVariable(spec.SecretRef);
                if (string.IsNullOrEmpty(token))
                    _logger.LogWarning(
                        "httpRequest: secretRef '{Name}' is not set on the server — sending the request without an Authorization header",
                        spec.SecretRef);
                else if (hasAuthorization)
                    _logger.LogDebug("httpRequest: explicit Authorization header present — ignoring secretRef '{Name}'", spec.SecretRef);
                else
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(
                    body, System.Text.Encoding.UTF8, contentType ?? "application/json");
            }

            var client = _httpClientFactory.CreateClient(HttpRequestActionSpec.HttpClientName);
            client.Timeout = timeout;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            var status = (int)response.StatusCode;
            Publish("http.status", status.ToString());

            var raw = (await response.Content.ReadAsStringAsync(timeoutCts.Token)).Trim();
            CaptureResponseBody(raw, Publish);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "httpRequest {Method} {Url} returned {Status}; abortOnFailure={Abort}",
                    request.Method, uri, status, spec.AbortOnFailure);
                return await FailAsync($"HTTP {status}");
            }

            _logger.LogInformation("httpRequest {Method} {Url} returned {Status}", request.Method, uri, status);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Engine shutdown, not a dispatch failure — leave the ticket alone.
            _logger.LogWarning("httpRequest to {Url} cancelled (engine shutdown)", uri);
            return spec.AbortOnFailure;
        }
        catch (OperationCanceledException)
        {
            // Both HttpClient.Timeout and the linked CTS surface here.
            _logger.LogWarning("httpRequest to {Url} timed out after {Timeout}s; abortOnFailure={Abort}",
                uri, timeout.TotalSeconds, spec.AbortOnFailure);
            return await FailAsync($"timed out after {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "httpRequest to {Url} failed; abortOnFailure={Abort}", uri, spec.AbortOnFailure);
            return await FailAsync(ex.Message);
        }
    }

    /// <summary>
    /// Writes the queryable denial receipt for an outbound dry run: a structured
    /// <c>outbound-denial/v1</c> ticket comment naming agent, action, target, and rule —
    /// the same "denials produce receipts just like warnings" contract as the R2
    /// <c>policy-violation/v1</c> run events. Firings without a ticket still get the log line.
    /// </summary>
    private async Task WriteOutboundDenialReceiptAsync(
        string slug, TriggerFiring firing, string actor, string url, string host, string reason)
    {
        _logger.LogWarning(
            "OUTBOUND DRY-RUN agent={Agent} action=httpRequest target={Target} rule=outbound-approval reason={Reason}",
            actor, url, reason);

        if (firing.TicketId is not int ticketId) return;

        var receipt = JsonSerializer.Serialize(new
        {
            schema = "outbound-denial/v1",
            agent = actor,
            action = "httpRequest",
            target = url,
            host,
            rule = "outbound-approval",
            reason,
            enforcementMode = "dry-run",
        });

        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "httpRequest: failed to write outbound-denial receipt for ticket #{Id}", ticketId);
        }
    }

    // ── R4: file-ownership leases ───────────────────────────────────────────

    /// <summary>
    /// The dispatch-time lease gate: resolves the ticket's declared file scope (handoff
    /// <c>ownedFiles</c>, falling back to the agent's own <c>allowedWriteGlobs</c> — see
    /// <see cref="FileLeaseScopeResolver"/>) and attempts to lease it for <paramref name="runId"/>.
    /// <see cref="FileLeaseGateOutcome.NotApplicable"/> covers every reason leasing does not apply
    /// to this dispatch: no store wired (pre-R4 behavior), no ticket, no declared scope, or a
    /// lease-store fault. That last case is a deliberate fail-<b>open</b> choice, unlike
    /// <see cref="ContractPolicy"/>'s fail-closed default for an unreadable manifest: a missing or
    /// malformed contracts.json is an authorization gap that must block every tool call, but a
    /// transient fault in this store (a locked file, a full disk) is an availability problem for a
    /// serialization aid — halting every dispatch in the project because the lease table hiccuped
    /// would be worse than the race it exists to prevent.
    /// <para>
    /// On a real conflict, block and warn mode diverge exactly the way R2/R3 diverge for every
    /// other policy violation: <see cref="FileLeaseGateOutcome.Blocked"/> (the agent's contract is
    /// in block mode) is real enforcement — the dispatch fails closed, the same as R3 denying an
    /// out-of-glob write. <see cref="FileLeaseGateOutcome.WarnedAndProceeded"/> (warn mode) mirrors
    /// R2's shadow mode: the conflict is recorded as a receipt, but the tool call — here, the
    /// dispatch itself — is not stopped. A warn-mode dispatch that proceeds through a conflict does
    /// not register a lease of its own (the scope it would have claimed is already held), so it is
    /// not tracked for serialization against a third run either; that is the acknowledged cost of
    /// shadow mode, and the same cost R2 accepts for an out-of-glob write. It is also the point of
    /// running R4 in warn mode at all: like R2's SP-1 inventory, it lets real conflicts happen and
    /// be recorded so an owner has evidence before flipping a given agent to block.
    /// </para>
    /// </summary>
    internal async Task<FileLeaseGateDecision> TryAcquireDispatchLeaseAsync(
        ProjectRuntime rt, int? ticketId, string agentName, string runId, CancellationToken ct)
    {
        if (_leases is null || ticketId is null || string.IsNullOrWhiteSpace(rt.Workspace))
            return FileLeaseGateDecision.NotApplicable;

        IReadOnlyList<string> scope;
        // Oldest-first, and reused twice: as the handoff source the leased scope is resolved from,
        // and as the receipt history the write-once denial check reads (see WriteFileLeaseReceiptAsync).
        var comments = new List<string>();
        try
        {
            Models.Ticket? ticket = null;
            try { ticket = await _tickets.GetTicketAsync(rt.Slug, ticketId.Value); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "fileLease: could not read ticket #{Id} for scope resolution", ticketId);
            }

            if (ticket is not null)
                comments.AddRange(ticket.Comments.OrderBy(c => c.CreatedAt).Select(c => c.Content));
            scope = await FileLeaseScopeResolver.ResolveAsync(rt.Workspace!, comments, agentName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: scope resolution failed for agent {Agent} ticket #{Id} — dispatching unleased", agentName, ticketId);
            return FileLeaseGateDecision.NotApplicable;
        }

        if (scope.Count == 0)
            return FileLeaseGateDecision.NotApplicable;

        PolicyEnforcementMode enforcement;
        FileLeaseAcquireResult result;
        try
        {
            var policy = await ContractPolicyLoader.LoadAsync(rt.Workspace!, agentName, ct);
            enforcement = policy.Enforcement;
            result = await _leases.AcquireAsync(
                rt.Slug, ticketId.Value, runId, agentName, scope, DateTime.UtcNow, FileLeaseStore.DefaultTtl, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: acquire failed for agent {Agent} ticket #{Id} — dispatching unleased", agentName, ticketId);
            return FileLeaseGateDecision.NotApplicable;
        }

        if (result.IsAcquired)
            return FileLeaseGateDecision.Granted;

        var outcome = enforcement == PolicyEnforcementMode.Block
            ? FileLeaseGateOutcome.Blocked
            : FileLeaseGateOutcome.WarnedAndProceeded;
        await WriteFileLeaseReceiptAsync(
            rt.Slug, ticketId.Value, agentName, scope, result.ConflictingLease!, enforcement, comments);
        _logger.LogWarning(
            "fileLease: {Outcome} agent={Agent} ticket=#{Ticket} run={Run} conflictsWith run={ConflictRun} agent={ConflictAgent}",
            outcome, agentName, ticketId, runId, result.ConflictingLease!.RunId, result.ConflictingLease.Agent);
        return new FileLeaseGateDecision(outcome);
    }

    /// <summary>Releases any active lease held by <paramref name="runId"/>. A no-op when no store is
    /// wired or the run never held one (e.g. its declared scope was empty).</summary>
    internal Task ReleaseDispatchLeaseAsync(string slug, string runId) =>
        _leases?.ReleaseAsync(slug, runId, DateTime.UtcNow) ?? Task.CompletedTask;

    /// <summary>
    /// Writes the queryable receipt for a file-lease conflict: a structured
    /// <c>file-lease-denial/v1</c> ticket comment naming the agent, its scope, and the conflicting
    /// lease — the same "denials/serializations produce receipts" idiom as R2's
    /// <c>policy-violation/v1</c> and R3's <c>outbound-denial/v1</c>
    /// (<see cref="WriteOutboundDenialReceiptAsync"/>).
    /// <para>
    /// <b>Once per conflict, not once per poll (SP-3 F2).</b> A blocked dispatch returns before
    /// <c>FinalizeAsync</c>, so its trigger firing is never committed and a repeating
    /// <c>ticketInColumn</c> trigger retries it every tick — deliberately, so the lane resumes the
    /// moment the lease frees, and that retry behavior is unchanged here. What changes is the noise:
    /// if the newest <c>file-lease-denial/v1</c> already on the ticket is byte-identical to the one
    /// this refusal would write, nothing is appended. The receipt is its own dedup key — same
    /// blocked agent, same scope, same conflicting lease means the same JSON, whereas a different
    /// lease, holder, ticket or scope produces different JSON and therefore a new receipt. That is
    /// the same first-refusal-only discipline R6 applies to <c>merge-held/v1</c>, and because the
    /// key is the durable comment rather than in-process memory it holds across a restart too.
    /// </para>
    /// </summary>
    private async Task WriteFileLeaseReceiptAsync(
        string slug,
        int ticketId,
        string agent,
        IReadOnlyList<string> scope,
        FileLease conflict,
        PolicyEnforcementMode enforcement,
        IReadOnlyList<string> priorCommentsOldestFirst)
    {
        var receipt = JsonSerializer.Serialize(new
        {
            schema = "file-lease-denial/v1",
            agent,
            action = "runAgent",
            scope,
            rule = "file-ownership-lease",
            conflictingLeaseId = conflict.LeaseId,
            conflictingRunId = conflict.RunId,
            conflictingAgent = conflict.Agent,
            conflictingTicketId = conflict.TicketId,
            reason = $"Scope overlaps an active lease held by '{conflict.Agent}' (run {conflict.RunId}) on ticket #{conflict.TicketId}.",
            enforcementMode = enforcement == PolicyEnforcementMode.Block ? "block" : "warn",
        });

        var newest = priorCommentsOldestFirst.LastOrDefault(
            c => c.Contains("file-lease-denial/v1", StringComparison.Ordinal));
        if (string.Equals(newest, receipt, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "fileLease: ticket #{Id} already carries this exact denial receipt — not appending another", ticketId);
            return;
        }

        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: failed to write file-lease-denial receipt for ticket #{Id}", ticketId);
        }
    }

    // ── R5: worktree-per-ticket execution ───────────────────────────────────

    /// <summary>
    /// Creates or reuses the ticket's git worktree (<see cref="WorktreeManager.EnsureAsync"/>) and
    /// returns the path the agent should execute in, or null when isolation could not be honored.
    /// Called strictly after <see cref="TryAcquireDispatchLeaseAsync"/> has already granted (or
    /// deemed not-applicable) the file lease for <paramref name="runId"/> — on any failure here
    /// (no ticket in the firing, workspace is not a git repo, a git failure) that lease is released
    /// and a <c>worktree-isolation-failure/v1</c> receipt is written, so the dispatch fails closed
    /// exactly like a block-mode lease conflict rather than silently falling back to in-place
    /// execution (the one behavior the R5 constraints explicitly rule out).
    /// </summary>
    private async Task<string?> EnsureWorktreeIsolationAsync(
        ProjectRuntime rt, int? ticketId, string agentName, string runId, CancellationToken ct)
    {
        if (ticketId is null)
        {
            _logger.LogWarning(
                "worktree isolation requested for agent {Agent} but the firing has no ticket — failing the dispatch closed",
                agentName);
            await ReleaseDispatchLeaseAsync(rt.Slug, runId);
            return null;
        }

        var result = await WorktreeManager.EnsureAsync(rt.Workspace!, ticketId.Value, ct);
        if (!result.IsReady)
        {
            _logger.LogWarning(
                "worktree isolation failed for agent {Agent} ticket #{Id}: {Error}",
                agentName, ticketId, result.Error);
            await ReleaseDispatchLeaseAsync(rt.Slug, runId);
            await WriteWorktreeIsolationFailureReceiptAsync(rt.Slug, ticketId.Value, agentName, result);
            return null;
        }

        try
        {
            await _tickets.SetWorktreeStateAsync(rt.Slug, ticketId.Value, result.Branch!, result.Path!, "active");
        }
        catch (Exception ex)
        {
            // The worktree itself is ready — a persistence hiccup here must not fail a dispatch
            // that is otherwise good to go; the ticket simply won't show the branch/path until the
            // next successful write (e.g. cleanup at Done).
            _logger.LogWarning(ex, "worktree isolation: failed to persist worktree state on ticket #{Id}", ticketId);
        }

        return result.Path;
    }

    private async Task WriteWorktreeIsolationFailureReceiptAsync(
        string slug, int ticketId, string agent, WorktreeResult result)
    {
        var receipt = JsonSerializer.Serialize(new
        {
            schema = "worktree-isolation-failure/v1",
            agent,
            action = "runAgent",
            rule = "worktree-isolation",
            outcome = result.Outcome.ToString(),
            reason = result.Error ?? "worktree isolation failed",
        });
        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "worktree isolation: failed to write failure receipt for ticket #{Id}", ticketId);
        }
    }

    /// <summary>
    /// Publishes the response body into the chain: always the raw trimmed text as
    /// <c>http.body</c>, plus one <c>http.body.&lt;field&gt;</c> per first-level field when the
    /// response is a JSON object. Malformed JSON is not an error — only the raw value is stored.
    /// </summary>
    private static void CaptureResponseBody(string raw, Action<string, string> publish)
    {
        publish("http.body", raw.Length <= MaxCapturedBodyChars ? raw : raw[..MaxCapturedBodyChars] + "…");
        if (raw.Length == 0 || raw[0] != '{') return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                publish($"http.body.{prop.Name}", prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null   => "",
                    // Objects and arrays keep their JSON text so nothing is silently lost.
                    _                    => prop.Value.GetRawText(),
                });
            }
        }
        catch (JsonException)
        {
            // Body claimed to be JSON but isn't — the raw capture above is all we can offer.
        }
    }

    // ── Git helpers ─────────────────────────────────────────────────────────

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string cwd, string args)
    {
        // 2-minute cap: a git blocked on a credential prompt or a stale index.lock must not
        // hold the per-repo git lock forever.
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromMinutes(2));
        return (res.ExitCode ?? -1, res.Stdout, res.TimedOut ? "git timed out after 2 minutes" : res.Stderr);
    }

    private static string BuildEventsSummary(AgentRun? run)
    {
        if (run is null) return "";
        var lines = new List<string>();
        foreach (var ev in run.SnapshotBuffer())
        {
            if (ev.Kind is "assistant" or "tool_use" or "result")
            {
                var text = ev.Kind == "tool_use"
                    ? $"[tool_use] {ev.Text}: {TruncateDetail(ev.Detail, 120)}"
                    : $"[{ev.Kind}] {TruncateLine(ev.Text, 200)}";
                lines.Add(text);
            }
            if (lines.Count >= 80) break;
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    private static string TruncateLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string TruncateDetail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "{}";
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>Outcome of <see cref="ActionExecutor.TryAcquireDispatchLeaseAsync"/> (R4).</summary>
internal enum FileLeaseGateOutcome
{
    /// <summary>Leasing does not apply to this dispatch (no store wired, no ticket, no declared
    /// scope, or a lease-store fault) — dispatch proceeds exactly as it did pre-R4.</summary>
    NotApplicable,
    /// <summary>The lease was acquired; dispatch proceeds.</summary>
    Granted,
    /// <summary>A conflicting lease is active and the agent's contract is in warn mode: a
    /// <c>file-lease-denial/v1</c> receipt is written, but — mirroring R2's shadow mode, which
    /// records an out-of-glob write without stopping it — the dispatch is not skipped. It proceeds
    /// without holding a lease of its own.</summary>
    WarnedAndProceeded,
    /// <summary>A conflicting lease is active and the agent's contract is in block mode: this
    /// dispatch fails closed, the same way R3 denies an out-of-glob write.</summary>
    Blocked,
}

internal readonly record struct FileLeaseGateDecision(FileLeaseGateOutcome Outcome)
{
    public static readonly FileLeaseGateDecision NotApplicable = new(FileLeaseGateOutcome.NotApplicable);
    public static readonly FileLeaseGateDecision Granted = new(FileLeaseGateOutcome.Granted);
    public static readonly FileLeaseGateDecision WarnedAndProceeded = new(FileLeaseGateOutcome.WarnedAndProceeded);

    /// <summary>True only for <see cref="FileLeaseGateOutcome.Blocked"/> — block mode is real
    /// enforcement and fails closed; warn mode logs a receipt but does not stop the dispatch, the
    /// same warn/block split R2/R3 apply to every other policy violation.</summary>
    public bool ShouldSkip => Outcome == FileLeaseGateOutcome.Blocked;
}

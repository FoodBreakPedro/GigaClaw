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

internal sealed partial class ActionExecutor
{
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
}

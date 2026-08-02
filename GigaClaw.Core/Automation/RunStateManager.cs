using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Decides whether a RunAgent dispatch should be skipped by checking budget,
/// minimum description length, and all concurrency/mutual-exclusion constraints.
/// Returns true when the dispatch should be skipped.
/// </summary>
internal sealed class RunStateManager
{
    private readonly AgentRunRegistry _runs;
    private readonly CostTracker _cost;
    private readonly TicketService _tickets;
    private readonly ILogger _logger;

    public RunStateManager(AgentRunRegistry runs, CostTracker cost, TicketService tickets, ILogger logger)
    {
        _runs = runs;
        _cost = cost;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the automation should NOT be dispatched (budget exceeded,
    /// agent already running, group busy, or mutual-exclusion violated).
    /// </summary>
    public async Task<bool> ShouldSkipAsync(
        ProjectRuntime rt,
        RunAgentActionSpec spec,
        TriggerFiring firing,
        string agentName,
        string group)
    {
        if (rt.Config?.DailyBudgetUsd is decimal cap && group != "ceo"
            && _cost.IsBudgetExceeded(rt.Workspace!, cap))
        {
            _logger.LogInformation("Budget exceeded for {Slug} — skipping {Agent}", rt.Slug, agentName);
            return true;
        }

        // Both per-ticket gates below read the firing ticket; read it once for the pair rather
        // than paying a second round-trip when a project configures both.
        if (firing.TicketId is int ticketId
            && (rt.Config?.MaxTicketCostUsd is not null || rt.Config?.MinDescriptionLength is not null))
        {
            var t = await _tickets.GetTicketAsync(rt.Slug, ticketId);
            if (t is not null)
            {
                // Plan 2.1: the per-ticket ceiling. Measured on the ticket's own durable
                // AgentCostUsd column, so a restart cannot reset it and no engine-side counter can
                // drift from it. No agent is exempt — see TicketCostCap for why groomer least of all.
                // Null is the only "off": a configured 0 means "this ticket gets no further spend",
                // which is a real thing an owner may want to say and must not read as "unlimited".
                if (rt.Config?.MaxTicketCostUsd is decimal ticketCap && ticketCap >= 0m
                    && (decimal)t.AgentCostUsd >= ticketCap)
                {
                    await ReportCostCapBreachAsync(rt, t, ticketCap, agentName);
                    return true;
                }

                if (rt.Config?.MinDescriptionLength is int minLen && t.Description.Length < minLen)
                {
                    _logger.LogInformation("Ticket #{Id} description too short ({Len}<{Min}) — skipping {Agent}",
                        ticketId, t.Description.Length, minLen, agentName);
                    return true;
                }
            }
        }

        if (firing.TicketId is not null
            && _runs.ActiveForTicket(rt.Slug, firing.TicketId.Value).Any(r => r.AgentName == agentName))
            return true;

        if (_runs.HasActiveInGroup(rt.Slug, group)) return true;

        if (spec.MutuallyExclusiveWith.Count > 0)
        {
            var ticketIdStr = firing.TicketId?.ToString() ?? "none";
            var resolved = spec.MutuallyExclusiveWith
                .Select(g => g.Replace("{assignee}", agentName).Replace("{ticketId}", ticketIdStr))
                .ToList();
            if (_runs.HasActiveAny(rt.Slug, resolved))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Records a per-ticket cost-cap breach: one receipt comment and one hand-off to the triage
    /// agent, both at most once per episode. The skip itself has already been decided by the caller —
    /// this method only decides whether the breach is worth *saying* again, which is the whole
    /// anti-spam story: a <c>ticketInColumn</c> trigger re-evaluates the same parked ticket every
    /// 30 seconds, so an unguarded comment would bury the ticket within the hour.
    /// </summary>
    private async Task ReportCostCapBreachAsync(
        ProjectRuntime rt, Models.Ticket ticket, decimal cap, string agentName)
    {
        _logger.LogWarning(
            "Per-ticket cost cap reached for {Slug} #{Id} ({Spent} >= {Cap} USD) — skipping {Agent}",
            rt.Slug, ticket.Id, ticket.AgentCostUsd, cap, agentName);

        if (TicketCostCap.AlreadyReported(ticket.Comments.Select(c => c.Content), ticket.Id, cap))
            return;

        try
        {
            await _tickets.AddCommentAsync(
                rt.Slug, ticket.Id,
                TicketCostCap.RenderReceipt(ticket.Id, cap, (decimal)ticket.AgentCostUsd, agentName),
                TicketCostCap.ReceiptAuthor);
        }
        catch (Exception ex)
        {
            // The receipt is the episode marker. Without it there is nothing to suppress the next
            // tick, so re-assigning now would mean re-assigning on every tick — leave both undone
            // and let the next evaluation retry the pair together.
            _logger.LogWarning(ex, "Cost-cap receipt failed for ticket #{Id} in project {Project}", ticket.Id, rt.Slug);
            return;
        }

        if (string.Equals(ticket.AssignedTo, TicketCostCap.TriageAgent, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // Same service pathway as the assignTicket action (ActionExecutor), which validates the
            // slug against the project's members and writes the activity entry.
            await _tickets.UpdateTicketAsync(
                rt.Slug, ticket.Id, assignedTo: TicketCostCap.TriageAgent, author: "automation");
        }
        catch (Exception ex)
        {
            // A project without the seeded triage member still gets the receipt and the hard stop;
            // only the routing is lost, so this never fails the skip decision.
            _logger.LogWarning(ex, "Cost-cap hand-off to '{Agent}' failed for ticket #{Id} in project {Project}",
                TicketCostCap.TriageAgent, ticket.Id, rt.Slug);
        }
    }
}

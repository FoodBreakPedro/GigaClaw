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

    /// <summary>
    /// U6 follow-up: opens (or re-finds) a pull request for the firing ticket's R5 worktree branch
    /// through <see cref="Github.GitHubPullRequestService.OpenForTicketAsync"/>. Records intent only,
    /// exactly like <see cref="ExecuteEnqueueMergeActionAsync"/> — the push and the PR create/lookup
    /// happen here, once; CI and review are driven by their own triggers, not by this action re-firing.
    /// <para>
    /// Fails closed rather than throwing for every outcome the service reports: an unwired executor
    /// (<see cref="_pullRequests"/> null, the "unwired = pre-feature behavior" shape every optional
    /// dependency here uses) logs and returns; a project with no GitHub config, no token, or a ticket
    /// never dispatched under <c>isolation: "worktree"</c> gets a plain ticket note explaining why,
    /// since the service itself writes nothing for that case. A policy-gate refusal (host not
    /// approved) already wrote its own <c>outbound-denial/v1</c> receipt inside the service, so this
    /// method does not duplicate it — it only fills the one silent gap.
    /// </para>
    /// </summary>
    private async Task ExecuteOpenPullRequestActionAsync(
        ProjectRuntime rt, TriggerFiring firing, OpenPullRequestActionSpec spec, CancellationToken ct)
    {
        if (_pullRequests is null)
        {
            _logger.LogDebug("openPullRequest: no GitHubPullRequestService wired — skipping for ticket #{Id}", firing.TicketId);
            return;
        }

        var ticketId = firing.TicketId!.Value;
        try
        {
            var result = await _pullRequests.OpenForTicketAsync(rt.Slug, ticketId, ct);

            // Opened/AlreadyOpen: the service already wrote its own github-pull-request/v1 receipt.
            // DryRun (a policy-gate refusal): the service already wrote its own outbound-denial/v1
            // receipt (action "gitPush" or "githubRequest"). The only case left silent by the service
            // is a plain Skip — no GitHub config, no token, or no worktree branch recorded — and that
            // is the one this action records so it fails closed instead of vanishing.
            if (!result.Published && !result.DryRun)
            {
                var reason = string.IsNullOrWhiteSpace(result.Error)
                    ? "Pull request not opened."
                    : $"Pull request not opened: {result.Error}";
                await NoteAsync(rt, ticketId, reason);
            }

            _logger.LogInformation(
                "openPullRequest: ticket #{Id} in project {Project} — opened={Opened} alreadyOpen={AlreadyOpen} pushed={Pushed}",
                ticketId, rt.Slug, result.Opened, result.AlreadyOpen, result.Pushed);
        }
        catch (Exception ex)
        {
            // The service is documented to return rather than throw for every "not configured" case
            // — a throw here would be a bug in that contract, not an expected outcome. Fail closed
            // with a note anyway, so an unexpected exception cannot take the rest of the chain down.
            _logger.LogWarning(ex, "openPullRequest failed for ticket #{Id} in project {Project}", ticketId, rt.Slug);
            await NoteAsync(rt, ticketId, $"Pull request not opened: {ex.Message}");
        }
    }
}

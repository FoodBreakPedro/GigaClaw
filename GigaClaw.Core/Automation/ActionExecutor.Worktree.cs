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
}

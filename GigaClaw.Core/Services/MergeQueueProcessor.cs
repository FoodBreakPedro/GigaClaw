using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Services;

/// <summary>
/// Drains R6's durable merge queue (doc/roadmap/lane-codex-runtime.md, Task R6), one candidate at a
/// time per project. Modeled on <see cref="FileLeaseReaper"/>'s poll-loop shape: a
/// <see cref="BackgroundService"/> that sweeps every project each tick, so queue state lives
/// entirely in <see cref="MergeQueueStore"/>'s SQLite table rather than in this process's memory —
/// a restart resumes exactly where the queue left off (see <see cref="MergeQueueStore.ClaimNextAsync"/>'s
/// crash-recovery sweep) instead of losing track of what was in flight.
/// <para>
/// <b>Serialization.</b> <see cref="ProcessProjectAsync"/> claims and fully processes AT MOST ONE
/// entry per call, and <see cref="ProcessAllAsync"/> awaits each project in turn rather than
/// fanning them out — so at any instant this processor is working on at most one merge, project-
/// wide. The queue is the serializer (design requirement 6): a second candidate for the same
/// project never starts until the first reaches <c>Merged</c> or <c>Bounced</c>.
/// </para>
/// </summary>
public sealed class MergeQueueProcessor : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly MergeQueueStore _queue;
    private readonly MergeApprovalGate _approval;
    private readonly ILogger<MergeQueueProcessor> _logger;

    // Faster than FileLeaseReaper's 60s: a merge candidate sitting in the queue is a ticket
    // waiting to land, not a crash-safety backstop, so a shorter cadence keeps the pipeline moving.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IntegrationTimeout = TimeSpan.FromMinutes(15);

    public MergeQueueProcessor(
        ProjectService projects,
        TicketService tickets,
        MergeQueueStore queue,
        AppSettingsService appSettings,
        ILogger<MergeQueueProcessor> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _queue = queue;
        // U17/R3/R6 precedent: the trust anchor is read fresh from the owner's settings.json on
        // every call (see AppSettingsService.GetApprovedMergeProjects), never cached at startup, so
        // an owner flipping approval takes effect on the very next poll.
        _approval = new MergeApprovalGate(appSettings.GetApprovedMergeProjects);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MergeQueueProcessor started (poll every {Seconds}s)", PollInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAllAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "MergeQueueProcessor tick failed"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Processes at most one candidate for every project, in turn. A single project's
    /// failure (e.g. a locked file) is logged and skipped rather than aborting the sweep.</summary>
    internal async Task ProcessAllAsync(CancellationToken ct)
    {
        foreach (var project in await _projects.ListProjectsAsync())
        {
            try { await ProcessProjectAsync(project.Slug, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MergeQueueProcessor: failed to process project {Slug}", project.Slug); }
        }
    }

    /// <summary>
    /// Claims and fully processes at most one queue entry for <paramref name="slug"/>: rebase onto
    /// workspace HEAD, run the configured integration command, then fast-forward-or-merge-commit
    /// into the workspace. Returns the claimed entry (whatever its final state) or null when the
    /// project's queue had nothing claimable — an empty queue and a queue that is entirely
    /// <c>Held</c> for an unapproved project both return null, which is exactly "nothing merges".
    /// </summary>
    internal async Task<MergeQueueEntry?> ProcessProjectAsync(string slug, CancellationToken ct)
    {
        var approved = _approval.IsApproved(slug);
        var claimed = await _queue.ClaimNextAsync(slug, approved, DateTime.UtcNow, ct);
        if (claimed is null) return null;

        var project = await _projects.GetProjectAsync(slug);
        if (project is null)
        {
            return await BounceAsync(slug, claimed, "project-missing", "The project no longer exists.");
        }
        var workspace = _projects.ResolveWorkspacePath(project);

        var ticket = await _tickets.GetTicketAsync(slug, claimed.TicketId);
        var worktreePath = ticket?.WorktreePath;
        if (ticket is null || string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return await BounceAsync(slug, claimed, "no-worktree",
                $"Ticket #{claimed.TicketId} has no worktree checkout at the recorded path — nothing to rebase or merge.");
        }

        var rebase = await MergeEngine.RebaseOntoWorkspaceHeadAsync(workspace, worktreePath, ct);
        if (rebase.Outcome == MergeRebaseOutcome.Conflict)
        {
            return await BounceAsync(
                slug, claimed, "conflict",
                $"Rebasing {claimed.Branch} onto the workspace's current HEAD conflicted in: " +
                string.Join(", ", rebase.ConflictingFiles!),
                conflictingFiles: rebase.ConflictingFiles);
        }
        if (rebase.Outcome == MergeRebaseOutcome.GitFailure)
        {
            return await BounceAsync(slug, claimed, "git-failure", rebase.Error ?? "git rebase failed for an unknown reason.");
        }

        var integration = await MergeEngine.RunIntegrationCommandAsync(
            worktreePath, claimed.IntegrationCommand, IntegrationTimeout, ct);
        if (integration.Ran && !integration.Success)
        {
            return await BounceAsync(
                slug, claimed, "integration-red",
                "The configured integration command failed in the rebased worktree.",
                outputExcerpt: integration.OutputExcerpt);
        }

        var (merged, error) = await MergeEngine.MergeIntoWorkspaceAsync(workspace, claimed.Branch, claimed.TicketId, ct);
        if (!merged)
        {
            return await BounceAsync(slug, claimed, "merge-failed", error ?? "git merge failed for an unknown reason.");
        }

        var mergedAt = DateTime.UtcNow;
        await _queue.CompleteAsync(slug, claimed.Id, MergeQueueState.Merged, reason: null, mergedAt, ct);
        // The branch is now an ancestor of the workspace's HEAD — R5's Done-triggered cleanup
        // (ActionExecutor.TryCleanupWorktreeAsync / WorktreeManager.TryCleanupAsync) already checks
        // exactly that ancestry before removing a worktree, so landing the merge here is what makes
        // this ticket's worktree eligible for that cleanup; R6 does not need to delete it itself.
        var receipt = MergeReceipts.Completed(claimed.TicketId, claimed.Branch, integrationRan: integration.Ran);
        try { await _tickets.AddCommentAsync(slug, claimed.TicketId, receipt, "automation"); }
        catch (Exception ex) { _logger.LogWarning(ex, "MergeQueueProcessor: failed to write merge-completed receipt for ticket #{Id}", claimed.TicketId); }

        _logger.LogInformation(
            "MergeQueueProcessor: merged {Branch} for ticket #{Id} in project {Slug}", claimed.Branch, claimed.TicketId, slug);
        return claimed with { State = MergeQueueState.Merged, Reason = null, UpdatedAtUtc = mergedAt };
    }

    private async Task<MergeQueueEntry> BounceAsync(
        string slug, MergeQueueEntry claimed, string cause, string reason,
        IReadOnlyList<string>? conflictingFiles = null, string? outputExcerpt = null)
    {
        var bouncedAt = DateTime.UtcNow;
        await _queue.CompleteAsync(slug, claimed.Id, MergeQueueState.Bounced, reason, bouncedAt, CancellationToken.None);

        var receipt = MergeReceipts.Bounced(claimed.TicketId, claimed.Branch, cause, reason, conflictingFiles, outputExcerpt);
        try { await _tickets.AddCommentAsync(slug, claimed.TicketId, receipt, "automation"); }
        catch (Exception ex) { _logger.LogWarning(ex, "MergeQueueProcessor: failed to write merge-bounced receipt for ticket #{Id}", claimed.TicketId); }

        try { await _tickets.MoveTicketAsync(slug, claimed.TicketId, "Blocked", "automation"); }
        catch (Exception ex) { _logger.LogWarning(ex, "MergeQueueProcessor: failed to move ticket #{Id} to Blocked", claimed.TicketId); }

        _logger.LogWarning(
            "MergeQueueProcessor: bounced ticket #{Id} branch {Branch} in project {Slug}: {Cause} — {Reason}",
            claimed.TicketId, claimed.Branch, slug, cause, reason);

        return claimed with { State = MergeQueueState.Bounced, Reason = reason, UpdatedAtUtc = bouncedAt };
    }
}

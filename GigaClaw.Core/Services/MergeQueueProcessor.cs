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
/// <para>
/// <b>The R4 interlock (SP-3 F1).</b> Before it touches either checkout, a claimed candidate is
/// checked against the project's live file leases: if any run other than the branch's own author
/// holds a lease covering a file this merge would rewrite, the entry goes back to <c>Held</c> with a
/// <c>merge-held/v1</c> receipt and is retried on later polls. A lease <b>holds</b> a merge; it
/// never bounces it and is never stolen to make room. Because the queue is FIFO and serialized, a
/// held head-of-line candidate does delay the candidates behind it — that is the queue working as
/// designed (one merge at a time, in order), not a separate stall to work around.
/// </para>
/// </summary>
public sealed class MergeQueueProcessor : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly MergeQueueStore _queue;
    private readonly FileLeaseStore _leases;
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
        FileLeaseStore leases,
        AppSettingsService appSettings,
        ILogger<MergeQueueProcessor> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _queue = queue;
        _leases = leases;
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
    /// A candidate held behind a live file lease is returned in state <c>Held</c> — it was claimed
    /// and examined, and it will be claimed again on the next pass.
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

        // SP-3 F1: the lease interlock runs FIRST, before the rebase — a held merge must leave both
        // the worktree and the workspace exactly as it found them, and a rebase already rewrites the
        // candidate's checkout.
        var interlocked = await CheckFileLeaseInterlockAsync(slug, claimed, workspace, ct);
        if (interlocked is not null) return interlocked;

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

    /// <summary>
    /// SP-3 F1. Returns the held entry when a live lease covers what this merge would rewrite (or
    /// when the diff is unknowable, which is the same answer for a gate that must fail closed), or
    /// null when the merge is clear to proceed.
    /// <para>
    /// A fault reading the lease table is <b>not</b> treated as "no leases": unlike
    /// <c>ActionExecutor.TryAcquireDispatchLeaseAsync</c>, which fails open because halting every
    /// dispatch in a project over a hiccup in a serialization aid is worse than the race it
    /// prevents, the cost here is one delayed merge on a queue that retries by construction — so
    /// this side of the interlock fails closed, and both directions of the asymmetry are deliberate.
    /// </para>
    /// </summary>
    private async Task<MergeQueueEntry?> CheckFileLeaseInterlockAsync(
        string slug, MergeQueueEntry claimed, string workspace, CancellationToken ct)
    {
        var changed = await MergeEngine.ChangedFilesAgainstWorkspaceHeadAsync(workspace, claimed.Branch, ct);
        if (!changed.Computed)
        {
            return await HoldAsync(
                slug, claimed,
                reason: "file-lease-interlock: the branch diff could not be computed",
                receipt: MergeReceipts.HeldForUnknownDiff(claimed.TicketId, claimed.Branch, changed.Error));
        }

        IReadOnlyList<FileLease> active;
        try
        {
            active = await _leases.ListActiveAsync(slug, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MergeQueueProcessor: could not read file leases for project {Slug} — holding", slug);
            return await HoldAsync(
                slug, claimed,
                reason: "file-lease-interlock: the lease table could not be read",
                receipt: MergeReceipts.HeldForUnknownDiff(claimed.TicketId, claimed.Branch, ex.Message));
        }

        var blocking = MergeLeaseInterlock.FindBlocking(changed.Files, active, claimed.TicketId, DateTime.UtcNow);
        if (blocking is null) return null;

        return await HoldAsync(
            slug, claimed,
            // Keyed on the lease, not on the file list: the same lease still holding on the next
            // pass is the same hold and writes no second receipt, while a different lease taking
            // over the block is new information and gets one of its own.
            reason: $"file-lease-interlock: lease {blocking.Lease.LeaseId} held by '{blocking.Lease.Agent}' " +
                    $"(run {blocking.Lease.RunId}) on ticket #{blocking.Lease.TicketId}",
            receipt: MergeReceipts.HeldForFileLease(
                claimed.TicketId, claimed.Branch, blocking.Lease, blocking.Files));
    }

    /// <summary>
    /// Puts a claimed entry back to <c>Held</c> and writes its receipt <b>once per hold reason</b> —
    /// the same first-hold-only discipline <c>enqueueMerge</c> applies to the approval hold
    /// (ActionExecutor.ExecuteEnqueueMergeActionAsync). The previous reason is read off the claimed
    /// entry, which came from SQLite, so this survives a restart without any in-memory bookkeeping:
    /// a process that comes back to a still-blocked merge re-holds it silently.
    /// </summary>
    private async Task<MergeQueueEntry> HoldAsync(
        string slug, MergeQueueEntry claimed, string reason, string receipt)
    {
        var repeat = string.Equals(claimed.Reason, reason, StringComparison.Ordinal);
        var heldAt = DateTime.UtcNow;
        await _queue.HoldAsync(slug, claimed.Id, reason, heldAt, CancellationToken.None);

        if (!repeat)
        {
            try { await _tickets.AddCommentAsync(slug, claimed.TicketId, receipt, "automation"); }
            catch (Exception ex) { _logger.LogWarning(ex, "MergeQueueProcessor: failed to write merge-held receipt for ticket #{Id}", claimed.TicketId); }
        }

        _logger.LogInformation(
            "MergeQueueProcessor: held ticket #{Id} branch {Branch} in project {Slug}: {Reason}",
            claimed.TicketId, claimed.Branch, slug, reason);

        return claimed with { State = MergeQueueState.Held, Reason = reason, UpdatedAtUtc = heldAt };
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

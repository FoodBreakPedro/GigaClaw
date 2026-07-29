using System.Collections.Concurrent;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

/*
 * R5 — Worktree-per-ticket execution (doc/roadmap/lane-codex-runtime.md, Task R5).
 *
 * Productizes the opt-in recipe in doc/worktree-workflow.md: instead of an agent prompt telling
 * the CLI to `cd` into a worktree it creates itself via tools/worktree-ensure.ps1, a
 * `runAgent` action with `isolation: "worktree"` makes the ORCHESTRATOR create/reuse the worktree
 * and hand the agent's subprocess that directory as its working directory
 * (see ClaudeRunContext.ExecutionPath / ProcessLifecycleManager). `.agents/` itself is still read
 * from the primary workspace (ClaudeRunContext.WorkspacePath), exactly as the opt-in recipe's
 * caveat "`.agents/` is not copied into worktrees" describes — only the process cwd moves.
 *
 * Path convention: `<workspace>.worktrees/ticket-<id>`, a SIBLING of the workspace directory —
 * mirroring doc/worktree-workflow.md's `<repo>.worktrees/ticket-<N>` convention exactly, and
 * deliberately NOT nested inside the workspace. Nesting it would (a) show up as an untracked
 * directory in `git status` of the main workspace repo — polluting commitAgentMemory's dirty-check
 * and any other status-sensitive automation — and (b) risk collision with PackInstaller's
 * `.agents.staging-*` sweep (PackInstaller.SweepStagingDirectories) or ProjectTemplate's `.agents/`
 * globbing, both of which only look inside the workspace root. A sibling directory is invisible to
 * all three.
 *
 * Branch convention: `ticket/<id>`, matching doc/worktree-workflow.md exactly so a project that
 * already adopted the opt-in recipe by hand sees the same branch name from the productized path.
 */

/// <summary>Outcome of <see cref="WorktreeManager.EnsureAsync"/>.</summary>
public enum WorktreeOutcome
{
    /// <summary>The worktree exists (freshly created or reused) and is ready to execute in.</summary>
    Ready,
    /// <summary>The workspace is not a git repository — worktree isolation cannot be honored. The
    /// caller must fail the dispatch closed rather than fall back to in-place execution.</summary>
    NotAGitRepo,
    /// <summary>A git operation failed, or the deterministic path is occupied by something git does
    /// not recognize as this ticket's worktree.</summary>
    GitFailure,
}

/// <summary>Result of <see cref="WorktreeManager.EnsureAsync"/>. <see cref="Path"/>/<see cref="Branch"/>
/// are populated only when <see cref="IsReady"/> is true.</summary>
public sealed record WorktreeResult(WorktreeOutcome Outcome, string? Path, string? Branch, string? Error)
{
    public bool IsReady => Outcome == WorktreeOutcome.Ready;

    internal static WorktreeResult Ready(string path, string branch) => new(WorktreeOutcome.Ready, path, branch, null);
    internal static WorktreeResult NotAGitRepo(string error) => new(WorktreeOutcome.NotAGitRepo, null, null, error);
    internal static WorktreeResult Failure(string error) => new(WorktreeOutcome.GitFailure, null, null, error);
}

/// <summary>Outcome of <see cref="WorktreeManager.TryCleanupAsync"/>.</summary>
public enum WorktreeCleanupOutcome
{
    /// <summary>The worktree was clean and its branch fully merged — it was removed.</summary>
    Cleaned,
    /// <summary>Nothing was there to clean up (already removed by a prior pass).</summary>
    AlreadyGone,
    /// <summary>The worktree has uncommitted changes — never deleted; flagged to the owner instead.</summary>
    Dirty,
    /// <summary>The worktree is clean but its branch is not (yet) an ancestor of the workspace's
    /// current HEAD — merging is R6's job, not R5's; flagged rather than deleted.</summary>
    NotMerged,
    /// <summary>A git operation failed while checking or removing the worktree.</summary>
    Error,
}

/// <summary>Result of <see cref="WorktreeManager.TryCleanupAsync"/>.</summary>
public sealed record WorktreeCleanupResult(WorktreeCleanupOutcome Outcome, string? Reason)
{
    internal static readonly WorktreeCleanupResult Cleaned = new(WorktreeCleanupOutcome.Cleaned, null);
    internal static readonly WorktreeCleanupResult AlreadyGone = new(WorktreeCleanupOutcome.AlreadyGone, null);
    internal static WorktreeCleanupResult Dirty(string reason) => new(WorktreeCleanupOutcome.Dirty, reason);
    internal static WorktreeCleanupResult NotMerged(string reason) => new(WorktreeCleanupOutcome.NotMerged, reason);
    internal static WorktreeCleanupResult Error(string reason) => new(WorktreeCleanupOutcome.Error, reason);
}

/// <summary>
/// Creates, reuses, and cleans up per-ticket git worktrees for R5 <c>isolation: "worktree"</c>
/// dispatches. Every git operation shells out via <see cref="ProcessRunner"/> (the same helper
/// ActionExecutor's own git helpers and executePowerShell use) — no libgit2/managed git dependency.
/// Stateless aside from a per-workspace serialization lock, so it is a plain static class rather
/// than a DI-registered service.
/// </summary>
public static class WorktreeManager
{
    // 2-minute cap mirrors ActionExecutor.RunGitAsync: a git blocked on a credential prompt or a
    // stale index.lock must not hang a dispatch forever.
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    // Serializes worktree create/reuse per workspace so two concurrent dispatches against the same
    // ticket (or two different tickets in the same repo) don't race `git worktree add`/`prune`
    // against each other. Independent of ActionExecutor's own _gitLocks (different purpose, same
    // shape) so this type has no dependency on ActionExecutor.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string BranchFor(int ticketId) => $"ticket/{ticketId}";

    /// <summary>
    /// The deterministic checkout path for a ticket's worktree: a SIBLING of the workspace
    /// directory, never nested inside it — see the file-level remarks for why.
    /// </summary>
    public static string PathFor(string workspacePath, int ticketId)
    {
        var full = System.IO.Path.GetFullPath(workspacePath)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(full) ?? full;
        var repoName = System.IO.Path.GetFileName(full);
        return System.IO.Path.Combine(parent, $"{repoName}.worktrees", $"ticket-{ticketId}");
    }

    /// <summary>
    /// Creates the ticket's worktree if it does not already exist, or returns the existing one
    /// (idempotent — a re-dispatch against the same ticket reuses it). Fails closed with
    /// <see cref="WorktreeOutcome.NotAGitRepo"/> when <paramref name="workspacePath"/> is not a git
    /// repository: worktree isolation must never silently fall back to in-place execution.
    /// </summary>
    public static async Task<WorktreeResult> EnsureAsync(string workspacePath, int ticketId, CancellationToken ct)
    {
        var gitLock = _locks.GetOrAdd(workspacePath, static _ => new SemaphoreSlim(1, 1));
        await gitLock.WaitAsync(ct);
        try
        {
            var isRepo = await RunGitAsync(workspacePath, "rev-parse --is-inside-work-tree", ct);
            if (isRepo.exitCode != 0 || !isRepo.stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return WorktreeResult.NotAGitRepo(
                    $"Workspace '{workspacePath}' is not a git repository — worktree isolation requires git.");

            var branch = BranchFor(ticketId);
            var path = PathFor(workspacePath, ticketId);

            // Prune stale registrations (a worktree removed by hand, outside git, would otherwise
            // leave a dangling entry) before checking whether this ticket's branch already has a
            // registered worktree. Matched by BRANCH, not by string-comparing our computed `path`
            // against git's reported one: git may report an OS-canonicalized path (e.g. macOS
            // resolving a /tmp symlink to /private/tmp) that differs lexically from `path` even
            // though it is the same directory — branch identity has no such ambiguity. The path we
            // return is always our own deterministic `path`, never git's spelling of it, so a
            // ticket's persisted WorktreePath is stable across calls regardless of that quirk.
            await RunGitAsync(workspacePath, "worktree prune", ct);
            var list = await RunGitAsync(workspacePath, "worktree list --porcelain", ct);
            if (list.exitCode == 0 && HasWorktreeForBranch(list.stdout, $"refs/heads/{branch}"))
                return WorktreeResult.Ready(path, branch);

            if (Directory.Exists(path))
            {
                // Something occupies the deterministic path but git does not recognize it as a
                // worktree. Refuse rather than reuse-or-delete a directory we don't understand.
                return WorktreeResult.Failure(
                    $"'{path}' exists but is not a registered git worktree — refusing to touch it.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            var branchExists = (await RunGitAsync(
                workspacePath, $"rev-parse --verify --quiet refs/heads/{branch}", ct)).exitCode == 0;
            // Branch already exists (e.g. a prior worktree was cleaned up but the branch survived,
            // or the project adopted the opt-in recipe by hand first): check it out into the new
            // worktree rather than re-creating it. Otherwise start a fresh branch from HEAD — the
            // same "no explicit start point" default doc/worktree-workflow.md's script uses when it
            // says "from local main": the workspace's checked-out branch (normally main) is HEAD.
            var addArgs = branchExists
                ? $"worktree add \"{path}\" {branch}"
                : $"worktree add -b {branch} \"{path}\"";
            var add = await RunGitAsync(workspacePath, addArgs, ct);
            if (add.exitCode != 0)
                return WorktreeResult.Failure($"git worktree add failed: {add.stderr.Trim()}");

            return WorktreeResult.Ready(path, branch);
        }
        finally
        {
            gitLock.Release();
        }
    }

    /// <summary>
    /// Attempts to remove a ticket's worktree once it has reached Done. Only ever removes when the
    /// checkout is clean AND the branch is already an ancestor of the workspace's current HEAD
    /// (i.e. actually merged — R6 owns the merge itself, this only recognizes it happened). Every
    /// other case — dirty checkout, unmerged branch, worktree already missing on disk, a git
    /// failure — is reported rather than acted on: dirty/failed worktrees are never silently
    /// deleted, they are flagged to the owner (see ActionExecutor.TryCleanupWorktreeAsync).
    /// </summary>
    public static async Task<WorktreeCleanupResult> TryCleanupAsync(
        string workspacePath, string branch, string worktreePath, CancellationToken ct)
    {
        if (!Directory.Exists(worktreePath))
            return WorktreeCleanupResult.AlreadyGone;

        var status = await RunGitAsync(worktreePath, "status --porcelain", ct);
        if (status.exitCode != 0)
            return WorktreeCleanupResult.Error($"git status failed in worktree: {status.stderr.Trim()}");
        if (!string.IsNullOrWhiteSpace(status.stdout))
            return WorktreeCleanupResult.Dirty("the worktree has uncommitted changes");

        var mergeCheck = await RunGitAsync(workspacePath, $"merge-base --is-ancestor {branch} HEAD", ct);
        if (mergeCheck.exitCode != 0)
            return WorktreeCleanupResult.NotMerged(
                $"branch '{branch}' is not (yet) merged into the workspace's current HEAD");

        var gitLock = _locks.GetOrAdd(workspacePath, static _ => new SemaphoreSlim(1, 1));
        await gitLock.WaitAsync(ct);
        try
        {
            var remove = await RunGitAsync(workspacePath, $"worktree remove \"{worktreePath}\"", ct);
            if (remove.exitCode != 0)
                return WorktreeCleanupResult.Error($"git worktree remove failed: {remove.stderr.Trim()}");

            // Best-effort: the worktree is already gone (the artifact that matters) once we reach
            // here, so a branch-delete failure (e.g. it was never fast-forwarded and -d refuses) is
            // logged by the caller, not escalated back to a cleanup failure.
            await RunGitAsync(workspacePath, $"branch -d {branch}", ct);
            return WorktreeCleanupResult.Cleaned;
        }
        finally
        {
            gitLock.Release();
        }
    }

    /// <summary>True when `git worktree list --porcelain` output (blocks separated by a blank
    /// line) contains a block whose `branch refs/heads/&lt;name&gt;` line matches
    /// <paramref name="branchRef"/> — i.e. some worktree already checks out that branch.</summary>
    private static bool HasWorktreeForBranch(string porcelain, string branchRef)
    {
        foreach (var rawLine in porcelain.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("branch ", StringComparison.Ordinal)) continue;
            var branch = line["branch ".Length..].Trim();
            if (string.Equals(branch, branchRef, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(
        string cwd, string args, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, GitTimeout, ct: ct);
        return (res.ExitCode ?? -1, res.Stdout, res.TimedOut ? "git timed out after 2 minutes" : res.Stderr);
    }
}

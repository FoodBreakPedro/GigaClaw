using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

/*
 * R6 — Merge mechanics (doc/roadmap/lane-codex-runtime.md, Task R6).
 *
 * Pure git plumbing for one merge-queue candidate, called only by MergeQueueProcessor. Every git
 * operation shells out via ProcessRunner — the same helper WorktreeManager (R5) and ActionExecutor's
 * own git helpers use — no libgit2/managed git dependency. Stateless, so a plain static class.
 *
 * A candidate's worktree and the workspace it merges into are the SAME git repository (a worktree
 * shares objects/refs with its parent), so rebasing "onto workspace HEAD" needs no fetch — the
 * worktree's `git rebase <sha>` can see the workspace's HEAD commit directly.
 */

public enum MergeRebaseOutcome { Success, Conflict, GitFailure }

/// <summary>Result of <see cref="MergeEngine.RebaseOntoWorkspaceHeadAsync"/>. <see cref="ConflictingFiles"/>
/// is populated only on <see cref="MergeRebaseOutcome.Conflict"/>.</summary>
public sealed record MergeRebaseResult(MergeRebaseOutcome Outcome, IReadOnlyList<string>? ConflictingFiles, string? Error)
{
    internal static MergeRebaseResult Ok() => new(MergeRebaseOutcome.Success, null, null);
    internal static MergeRebaseResult Conflict(IReadOnlyList<string> files) => new(MergeRebaseOutcome.Conflict, files, null);
    internal static MergeRebaseResult Failure(string error) => new(MergeRebaseOutcome.GitFailure, null, error);
}

/// <summary>Result of <see cref="MergeEngine.RunIntegrationCommandAsync"/>. <see cref="Ran"/> is
/// false when no command was configured — that is a skip, not a pass, and the caller records it as
/// such rather than treating it as green by default.</summary>
public sealed record IntegrationRunResult(bool Ran, bool Success, string? OutputExcerpt);

/// <summary>
/// Result of <see cref="MergeEngine.ChangedFilesAgainstWorkspaceHeadAsync"/> — the workspace-relative
/// paths a candidate branch would rewrite when it lands. <see cref="Computed"/> is false when git
/// could not answer (a missing branch, an unreadable repo, a timeout); callers must treat that as
/// "unknown, therefore possibly everything" and fail closed rather than as "nothing changed".
/// </summary>
public sealed record MergeChangedFilesResult(bool Computed, IReadOnlyList<string> Files, string? Error)
{
    internal static MergeChangedFilesResult Ok(IReadOnlyList<string> files) => new(true, files, null);
    internal static MergeChangedFilesResult Unknown(string error) => new(false, Array.Empty<string>(), error);
}

public static class MergeEngine
{
    // 2-minute cap mirrors WorktreeManager/ActionExecutor's own git helpers: a git blocked on a
    // credential prompt or a stale index.lock must not hang the queue processor forever.
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound on the integration command's captured stdout+stderr, mirroring
    /// ActionExecutor's MaxCapturedBodyChars/MaxCapturedStdoutChars — a runaway test suite can't
    /// paste megabytes into a ticket comment.</summary>
    private const int MaxExcerptChars = 4000;

    /// <summary>
    /// Rebases the candidate's worktree branch onto the workspace's current HEAD. On conflict, the
    /// conflicting files are read BEFORE the rebase is aborted — <c>rebase --abort</c> clears the
    /// conflict markers, so the caller must capture <c>git diff --diff-filter=U</c> first. Either
    /// way, the worktree is left rebase-free: a conflict never leaves a half-rebased checkout for
    /// the next poll to trip over.
    /// </summary>
    public static async Task<MergeRebaseResult> RebaseOntoWorkspaceHeadAsync(
        string workspacePath, string worktreePath, CancellationToken ct)
    {
        var headRes = await RunGitAsync(workspacePath, "rev-parse HEAD", ct);
        if (headRes.exitCode != 0)
            return MergeRebaseResult.Failure($"could not resolve workspace HEAD: {headRes.stderr.Trim()}");
        var head = headRes.stdout.Trim();

        var rebase = await RunGitAsync(worktreePath, $"rebase {head}", ct);
        if (rebase.exitCode == 0)
            return MergeRebaseResult.Ok();

        // Capture conflicts BEFORE aborting — `rebase --abort` clears the unmerged index entries
        // this reads.
        var conflicts = await RunGitAsync(worktreePath, "diff --name-only --diff-filter=U", ct);
        var files = conflicts.exitCode == 0
            ? conflicts.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        // Best-effort: leave the worktree rebase-free either way. If the abort itself fails there
        // is nothing more this call can do; a stuck rebase will surface as a GitFailure on the next
        // poll's rebase attempt rather than being silently retried against a half-rebased tree.
        await RunGitAsync(worktreePath, "rebase --abort", ct);

        return files.Count > 0
            ? MergeRebaseResult.Conflict(files)
            : MergeRebaseResult.Failure($"git rebase failed: {rebase.stderr.Trim()}");
    }

    /// <summary>
    /// The workspace-relative paths landing <paramref name="branch"/> would rewrite: the three-dot
    /// diff <c>HEAD...branch</c>, i.e. everything the branch changed since it forked from the
    /// integration target, with the target's own subsequent commits excluded. Read in the workspace
    /// (a worktree shares objects and refs with its parent, so no fetch is needed) and computed
    /// <b>before</b> the rebase, so an interlock can refuse to touch the checkout at all.
    /// <para>
    /// Any git failure yields <see cref="MergeChangedFilesResult.Computed"/> false rather than an
    /// empty file list — "git could not tell us" and "this branch changes nothing" are opposite
    /// answers for a gate, and conflating them would let an unanswerable diff sail through.
    /// </para>
    /// </summary>
    public static async Task<MergeChangedFilesResult> ChangedFilesAgainstWorkspaceHeadAsync(
        string workspacePath, string branch, CancellationToken ct)
    {
        var res = await RunGitAsync(workspacePath, $"diff --name-only HEAD...{branch}", ct);
        if (res.exitCode != 0)
            return MergeChangedFilesResult.Unknown($"git diff HEAD...{branch} failed: {res.stderr.Trim()}");

        var files = res.stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return MergeChangedFilesResult.Ok(files);
    }

    /// <summary>
    /// Runs the configured integration command in the (now rebased) worktree. A null/blank command
    /// is a skip (<see cref="IntegrationRunResult.Ran"/> false, <see cref="IntegrationRunResult.Success"/>
    /// true) — absent means "nothing to check", never "checked and green".
    /// </summary>
    public static async Task<IntegrationRunResult> RunIntegrationCommandAsync(
        string worktreePath, string? command, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new IntegrationRunResult(Ran: false, Success: true, OutputExcerpt: null);

        var (fileName, arguments) = BuildShellInvocation(command);
        var res = await ProcessRunner.RunAsync(fileName, arguments, worktreePath, timeout, ct: ct);
        var combined = (res.Stdout + "\n" + res.Stderr).Trim();
        var excerpt = combined.Length <= MaxExcerptChars ? combined : combined[..MaxExcerptChars] + "…";
        var success = !res.TimedOut && res.ExitCode == 0;
        return new IntegrationRunResult(Ran: true, Success: success, OutputExcerpt: excerpt);
    }

    /// <summary>
    /// Lands the candidate branch into the workspace's checked-out branch. Fast-forward is always
    /// attempted first — after a successful rebase onto workspace HEAD, the branch tip is a
    /// descendant of HEAD, so this is the common case and yields linear history. The merge-commit
    /// fallback exists only for the rare race where the workspace HEAD moved between the rebase and
    /// this call (e.g. a concurrent manual commit); it is not expected to fire in the normal one-
    /// merge-at-a-time flow.
    /// </summary>
    public static async Task<(bool success, string? error)> MergeIntoWorkspaceAsync(
        string workspacePath, string branch, int ticketId, CancellationToken ct)
    {
        var ff = await RunGitAsync(workspacePath, $"merge --ff-only {branch}", ct);
        if (ff.exitCode == 0) return (true, null);

        var noFf = await RunGitAsync(
            workspacePath,
            "-c user.name=\"GigaClaw Merge Queue\" -c user.email=\"merge-queue@gigaclaw.local\" " +
            $"merge --no-ff {branch} -m \"Merge {branch} (ticket #{ticketId})\"",
            ct);
        if (noFf.exitCode == 0) return (true, null);

        // Best-effort: never leave a half-merged index behind for the next poll to trip over.
        await RunGitAsync(workspacePath, "merge --abort", ct);
        return (false, $"git merge failed: {noFf.stderr.Trim()}");
    }

    private static (string fileName, string arguments) BuildShellInvocation(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/d /s /c \"{command}\"");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(
        string cwd, string args, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, GitTimeout, ct: ct);
        return (res.ExitCode ?? -1, res.Stdout, res.TimedOut ? "git timed out after 2 minutes" : res.Stderr);
    }
}

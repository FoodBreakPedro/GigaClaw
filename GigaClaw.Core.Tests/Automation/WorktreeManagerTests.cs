using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R5's git plumbing (doc/roadmap/lane-codex-runtime.md): <see cref="WorktreeManager.EnsureAsync"/>
/// creates/reuses a ticket's worktree idempotently, and <see cref="WorktreeManager.TryCleanupAsync"/>
/// only ever removes a clean, merged worktree — everything else is reported, never deleted. Uses
/// real git repositories in temp directories (cheap, deterministic — no mocking git itself).
/// </summary>
public class WorktreeManagerTests
{
    private static async Task RunGitAsync(string cwd, string args)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(res.Success, $"git {args} failed in {cwd}: {res.Stderr}");
    }

    private static async Task<string> InitRepoAsync(TempDir tmp)
    {
        var workspace = Path.Combine(tmp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, "init -q");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");
        return workspace;
    }

    // ── EnsureAsync: creation / reuse ───────────────────────────────────────

    [Fact]
    public async Task EnsureAsync_creates_a_worktree_and_branch_for_a_fresh_ticket()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);

        var result = await WorktreeManager.EnsureAsync(workspace, 42, CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal("ticket/42", result.Branch);
        Assert.True(Directory.Exists(result.Path));
        Assert.Equal(WorktreeManager.PathFor(workspace, 42), result.Path);
    }

    [Fact]
    public async Task EnsureAsync_is_idempotent_a_second_call_reuses_the_same_worktree()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);

        var first = await WorktreeManager.EnsureAsync(workspace, 7, CancellationToken.None);
        var second = await WorktreeManager.EnsureAsync(workspace, 7, CancellationToken.None);

        Assert.True(first.IsReady);
        Assert.True(second.IsReady);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Branch, second.Branch);

        var list = await ProcessRunner.RunAsync("git", "worktree list --porcelain", workspace, TimeSpan.FromSeconds(30));
        var entries = list.Stdout.Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal));
        // The main workspace checkout itself is one entry; the ticket worktree must appear exactly once.
        Assert.Equal(2, entries);
    }

    [Fact]
    public async Task EnsureAsync_reuses_an_existing_branch_instead_of_erroring_on_dash_b()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);
        await RunGitAsync(workspace, "branch ticket/9");

        var result = await WorktreeManager.EnsureAsync(workspace, 9, CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal("ticket/9", result.Branch);
        Assert.True(Directory.Exists(result.Path));
    }

    [Fact]
    public async Task EnsureAsync_fails_closed_when_the_workspace_is_not_a_git_repository()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "not-a-repo");
        Directory.CreateDirectory(workspace);

        var result = await WorktreeManager.EnsureAsync(workspace, 1, CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal(WorktreeOutcome.NotAGitRepo, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.False(Directory.Exists(WorktreeManager.PathFor(workspace, 1)));
    }

    [Fact]
    public async Task EnsureAsync_refuses_a_deterministic_path_occupied_by_a_non_worktree_directory()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);
        var occupied = WorktreeManager.PathFor(workspace, 3);
        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "mystery.txt"), "not git's business");

        var result = await WorktreeManager.EnsureAsync(workspace, 3, CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal(WorktreeOutcome.GitFailure, result.Outcome);
        // Refuses rather than deletes: the stray directory and its file must survive untouched.
        Assert.True(File.Exists(Path.Combine(occupied, "mystery.txt")));
    }

    // ── TryCleanupAsync: only clean+merged is ever deleted ──────────────────

    [Fact]
    public async Task TryCleanupAsync_removes_a_clean_merged_worktree()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);
        var ensured = await WorktreeManager.EnsureAsync(workspace, 10, CancellationToken.None);
        Assert.True(ensured.IsReady);

        // Fast-forward the workspace's own HEAD to the (unchanged) branch tip so the branch is
        // trivially "merged" without R6's merge machinery — this test is about the cleanup
        // mechanics, not the merge itself.
        await RunGitAsync(workspace, $"merge --ff-only {ensured.Branch}");

        var cleanup = await WorktreeManager.TryCleanupAsync(workspace, ensured.Branch!, ensured.Path!, CancellationToken.None);

        Assert.Equal(WorktreeCleanupOutcome.Cleaned, cleanup.Outcome);
        Assert.False(Directory.Exists(ensured.Path));
    }

    [Fact]
    public async Task TryCleanupAsync_never_deletes_a_dirty_worktree()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);
        var ensured = await WorktreeManager.EnsureAsync(workspace, 11, CancellationToken.None);
        Assert.True(ensured.IsReady);
        await File.WriteAllTextAsync(Path.Combine(ensured.Path!, "scratch.txt"), "uncommitted work");

        var cleanup = await WorktreeManager.TryCleanupAsync(workspace, ensured.Branch!, ensured.Path!, CancellationToken.None);

        Assert.Equal(WorktreeCleanupOutcome.Dirty, cleanup.Outcome);
        Assert.NotNull(cleanup.Reason);
        Assert.True(Directory.Exists(ensured.Path));
        Assert.True(File.Exists(Path.Combine(ensured.Path!, "scratch.txt")));
    }

    [Fact]
    public async Task TryCleanupAsync_never_deletes_a_clean_but_unmerged_worktree()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);
        var ensured = await WorktreeManager.EnsureAsync(workspace, 12, CancellationToken.None);
        Assert.True(ensured.IsReady);
        // Commit inside the worktree so the checkout is clean, but never fast-forward the
        // workspace's HEAD to it — the branch is not merged.
        await File.WriteAllTextAsync(Path.Combine(ensured.Path!, "feature.txt"), "done");
        await RunGitAsync(ensured.Path!, "add -A");
        await RunGitAsync(ensured.Path!, "commit -q -m feature");

        var cleanup = await WorktreeManager.TryCleanupAsync(workspace, ensured.Branch!, ensured.Path!, CancellationToken.None);

        Assert.Equal(WorktreeCleanupOutcome.NotMerged, cleanup.Outcome);
        Assert.NotNull(cleanup.Reason);
        Assert.True(Directory.Exists(ensured.Path));
    }

    [Fact]
    public async Task TryCleanupAsync_reports_already_gone_when_the_worktree_directory_is_missing()
    {
        using var tmp = new TempDir();
        var workspace = await InitRepoAsync(tmp);

        var cleanup = await WorktreeManager.TryCleanupAsync(
            workspace, "ticket/999", WorktreeManager.PathFor(workspace, 999), CancellationToken.None);

        Assert.Equal(WorktreeCleanupOutcome.AlreadyGone, cleanup.Outcome);
    }

    // ── Path convention ──────────────────────────────────────────────────────

    [Fact]
    public void PathFor_is_a_sibling_of_the_workspace_not_nested_inside_it()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "some-repo");
        var path = WorktreeManager.PathFor(workspace, 5);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "some-repo.worktrees", "ticket-5"), path);
        Assert.False(path.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}

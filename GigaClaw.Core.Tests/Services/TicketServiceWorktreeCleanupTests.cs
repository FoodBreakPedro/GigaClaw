using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// R6 (doc/roadmap/SESSION-HANDOFF.md, PLAN-remaining.md §1 item 3): before this fix, worktree
/// cleanup only ran through ActionExecutor's moveTicketStatus action, so a user dragging a ticket
/// to Done on the Board UI — which calls <see cref="TicketService.ReorderTicketAsync"/> directly,
/// never through ActionExecutor — silently orphaned the worktree. These tests exercise that exact
/// UI path (the same <c>TicketService</c> method <c>Board.razor</c>'s <c>OnDropReorder</c> calls)
/// with a real git repository, proving cleanup now fires there too with R5's unchanged semantics:
/// a clean, merged worktree is removed; a dirty one is flagged, never silently deleted.
/// </summary>
public sealed class TicketServiceWorktreeCleanupTests
{
    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required TicketService Tickets { get; init; }
        public required string Slug { get; init; }
        public required string Workspace { get; init; }

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task RunGitAsync(string cwd, string args)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(res.Success, $"git {args} failed in {cwd}: {res.Stderr}");
    }

    private static async Task InitRepoAsync(string workspace)
    {
        await RunGitAsync(workspace, "init -q");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");
    }

    private static async Task<Harness> BuildAsync(string projectName)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(projectName);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        await InitRepoAsync(workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);

        return new Harness
        {
            Tmp = tmp,
            Tickets = tickets,
            Slug = project.Slug,
            Workspace = workspace,
        };
    }

    private static async Task<List<string>> CommentsAsync(Harness h, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    [Fact]
    public async Task Dragging_a_ticket_to_Done_on_the_board_cleans_up_a_clean_merged_worktree()
    {
        using var h = await BuildAsync("wt-ui-done-clean");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Review");

        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady);
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "active");
        await RunGitAsync(h.Workspace, $"merge --ff-only {ensured.Branch}"); // makes the branch "merged"

        // The exact call Board.razor's OnDropReorder makes on a drag-and-drop status change —
        // NOT going through ActionExecutor or its moveTicketStatus action at all.
        await h.Tickets.ReorderTicketAsync(h.Slug, ticket.Id, "Done", 0);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Done", after!.Status);
        Assert.Equal("cleaned", after.WorktreeStatus);
        Assert.False(Directory.Exists(ensured.Path));
    }

    [Fact]
    public async Task Dragging_a_ticket_to_Done_on_the_board_flags_a_dirty_worktree_without_deleting_it()
    {
        using var h = await BuildAsync("wt-ui-done-dirty");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Review");

        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady);
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "active");
        await File.WriteAllTextAsync(Path.Combine(ensured.Path!, "scratch.txt"), "uncommitted");

        await h.Tickets.ReorderTicketAsync(h.Slug, ticket.Id, "Done", 0);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Done", after!.Status);
        Assert.Equal("dirty", after.WorktreeStatus);
        Assert.True(Directory.Exists(ensured.Path)); // never silently deleted
        Assert.True(File.Exists(Path.Combine(ensured.Path!, "scratch.txt")));

        var receipt = Assert.Single(await CommentsAsync(h, ticket.Id), c => c.Contains("worktree-cleanup-blocked/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("Dirty", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_ticket_already_marked_cleaned_is_not_touched_again_by_a_second_transition_into_Done()
    {
        // Single-ownership / idempotence proof: TryCleanupWorktreeOnDoneAsync is guarded by
        // WorktreeStatus != "cleaned", so re-entering Done a second time (whichever path drove the
        // first cleanup) never re-attempts it — this is how the fix avoids double-cleanup between
        // the ActionExecutor moveTicketStatus path and the Board drag-to-Done path.
        using var h = await BuildAsync("wt-ui-done-idempotent");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ship the feature", status: "Review");

        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady);
        // Already recorded as cleaned (as if a prior transition already ran cleanup), even though
        // the directory still happens to exist on disk — cleanup must not be re-attempted.
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "cleaned");

        await h.Tickets.ReorderTicketAsync(h.Slug, ticket.Id, "Doing", 0); // leave Done...
        await h.Tickets.ReorderTicketAsync(h.Slug, ticket.Id, "Done", 0);  // ...and re-enter it

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Done", after!.Status);
        Assert.Equal("cleaned", after.WorktreeStatus);
        // Cleanup was skipped entirely (guarded by the "cleaned" state), so the directory —
        // which a real cleanup would have removed via `git worktree remove` — is untouched.
        Assert.True(Directory.Exists(ensured.Path));
    }
}

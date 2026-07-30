using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R6's merge queue + integration gate (doc/roadmap/lane-codex-runtime.md, Task R6):
/// <see cref="MergeQueueStore"/> is the durable, ordered, per-project queue; <see cref="MergeQueueProcessor"/>
/// drains it one candidate at a time via <see cref="MergeEngine"/>'s rebase/integration/merge
/// mechanics; <see cref="MergeApprovalGate"/> is the trust anchor (an owner-settings.json allow-list,
/// exactly R3's <c>OutboundApprovalGate</c> pattern — never a ticket label). Uses real git
/// repositories in temp directories, the same idiom as <c>ActionExecutorWorktreeTests</c> /
/// <c>WorktreeManagerTests</c>.
/// </summary>
public class MergeQueueTests
{
    private static async Task RunGitAsync(string cwd, string args)
    {
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(res.Success, $"git {args} failed in {cwd}: {res.Stderr}\n{res.Stdout}");
    }

    private static async Task InitRepoAsync(string workspace)
    {
        await RunGitAsync(workspace, "init -q");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        // These repos live in a temp dir with no .gitattributes of their own, so on Windows runners
        // (Git for Windows ships core.autocrlf=true by default) `git worktree add` and `git merge
        // --ff-only` checkouts silently rewrite LF blobs to CRLF in the working tree — corrupting the
        // exact byte content the assertions below compare against. Pin autocrlf off for this
        // repo-local config (shared by every worktree of it) so checkouts round-trip bytes exactly.
        await RunGitAsync(workspace, "config core.autocrlf false");
        await File.WriteAllTextAsync(Path.Combine(workspace, "shared.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");
    }

    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required ProjectService Projects { get; init; }
        public required TicketService Tickets { get; init; }
        public required MergeQueueStore Queue { get; init; }
        public required MergeQueueProcessor Processor { get; init; }
        public required string Slug { get; init; }
        public required string Workspace { get; init; }

        public void Dispose() => Tmp.Dispose();
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
        var queue = new MergeQueueStore(projects);
        var appSettings = new AppSettingsService(tmp.Path);
        var processor = new MergeQueueProcessor(
            projects, tickets, queue, appSettings, NullLogger<MergeQueueProcessor>.Instance);

        return new Harness
        {
            Tmp = tmp,
            Projects = projects,
            Tickets = tickets,
            Queue = queue,
            Processor = processor,
            Slug = project.Slug,
            Workspace = workspace,
        };
    }

    /// <summary>Overwrites settings.json directly — the same file <see cref="AppSettingsService"/>
    /// re-reads on every <c>GetApprovedMergeProjects</c> call, so this simulates the owner editing
    /// it by hand without needing a dedicated setter.</summary>
    private static void SetMergeApproval(Harness h, bool approved)
    {
        var path = Path.Combine(h.Tmp.Path, "settings.json");
        var json = approved
            ? JsonSerializer.Serialize(new { ApprovedMergeProjects = new[] { h.Slug } })
            : "{}";
        File.WriteAllText(path, json);
    }

    private static async Task<(int ticketId, string worktreePath, string branch)> CreateTicketWithWorktreeAsync(
        Harness h, string title)
    {
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, title);
        var ensured = await WorktreeManager.EnsureAsync(h.Workspace, ticket.Id, CancellationToken.None);
        Assert.True(ensured.IsReady, ensured.Error);
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, ensured.Branch!, ensured.Path!, "active");
        return (ticket.Id, ensured.Path!, ensured.Branch!);
    }

    private static async Task<List<string>> CommentsAsync(Harness h, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    // ── Sequential merge of two candidates touching the same file ──────────

    [Fact]
    public async Task Two_tickets_editing_the_same_file_merge_sequentially_second_rebased_history_stays_linear()
    {
        using var h = await BuildAsync("mq-sequential");
        SetMergeApproval(h, true);

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        var (t2, wt2, b2) = await CreateTicketWithWorktreeAsync(h, "Ticket two");
        // Appends after the base line rather than touching t1's line, so the same FILE is edited by
        // both tickets without the same LINE conflicting — the scenario the acceptance criteria asks
        // for ("editing the same file... merge sequentially with the second rebased").
        await File.WriteAllTextAsync(Path.Combine(wt2, "README.md"), "hello\nfrom-t2\n");
        await RunGitAsync(wt2, "add -A");
        await RunGitAsync(wt2, "commit -q -m t2-change");

        var enqueue1 = await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: true, DateTime.UtcNow);
        var enqueue2 = await h.Queue.EnqueueAsync(h.Slug, t2, b2, integrationCommand: null, approved: true, DateTime.UtcNow.AddSeconds(1));
        Assert.True(enqueue1.IsNew);
        Assert.True(enqueue2.IsNew);

        var first = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(MergeQueueState.Merged, first!.State);
        Assert.Equal(t1, first.TicketId);

        // Ticket 2's branch must land ON TOP of ticket 1's change: after t1 merges, the workspace
        // HEAD already has t1's line, so processing t2 rebases t2's branch onto that new HEAD.
        var second = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(MergeQueueState.Merged, second!.State);
        Assert.Equal(t2, second.TicketId);

        // Nothing left to claim.
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));

        // Both files carry both tickets' changes in the workspace.
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
        Assert.Equal("hello\nfrom-t2\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "README.md")));

        // History choice: fast-forward after rebase-onto-HEAD keeps the log linear (no merge
        // commits) — verify there is exactly one commit per ticket change on top of the initial
        // commit, not a merge-commit graph.
        var log = await ProcessRunner.RunAsync("git", "log --oneline", h.Workspace, TimeSpan.FromSeconds(30));
        var lines = log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // initial + t1-change + t2-change, no merge commit lines
        Assert.DoesNotContain(lines, l => l.Contains("Merge", StringComparison.OrdinalIgnoreCase));

        var receipts1 = await CommentsAsync(h, t1);
        var receipts2 = await CommentsAsync(h, t2);
        Assert.Contains(receipts1, c => c.Contains("merge-completed/v1"));
        Assert.Contains(receipts2, c => c.Contains("merge-completed/v1"));
    }

    // ── Conflict handling ───────────────────────────────────────────────────

    [Fact]
    public async Task A_manufactured_conflict_bounces_to_Blocked_with_a_receipt_naming_the_file_and_leaves_the_worktree_rebase_free()
    {
        using var h = await BuildAsync("mq-conflict");
        SetMergeApproval(h, true);

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        var (t2, wt2, b2) = await CreateTicketWithWorktreeAsync(h, "Ticket two");
        // Edits the SAME line ticket one edits — guaranteed conflict once t1 has landed.
        await File.WriteAllTextAsync(Path.Combine(wt2, "shared.txt"), "base\nfrom-t2-conflicting\n");
        await RunGitAsync(wt2, "add -A");
        await RunGitAsync(wt2, "commit -q -m t2-conflicting-change");

        await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: true, DateTime.UtcNow);
        await h.Queue.EnqueueAsync(h.Slug, t2, b2, integrationCommand: null, approved: true, DateTime.UtcNow.AddSeconds(1));

        var first = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, first!.State);

        var second = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(MergeQueueState.Bounced, second!.State);
        Assert.Equal(t2, second.TicketId);

        var ticket2 = await h.Tickets.GetTicketAsync(h.Slug, t2);
        Assert.Equal("Blocked", ticket2!.Status);

        var receipt = Assert.Single(await CommentsAsync(h, t2), c => c.Contains("merge-bounced/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("conflict", doc.RootElement.GetProperty("cause").GetString());
        var files = doc.RootElement.GetProperty("conflictingFiles").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("shared.txt", files);

        // The rebase was aborted cleanly: the worktree carries no in-progress rebase state. A
        // worktree mid-rebase makes `git status` report "rebase in progress" (and refuses most
        // other git operations) — its absence, plus a clean exit, is the evidence the abort worked.
        var status = await ProcessRunner.RunAsync("git", "status", wt2, TimeSpan.FromSeconds(30));
        Assert.True(status.Success);
        Assert.DoesNotContain("rebase in progress", status.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ── Restart resume ──────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_state_survives_engine_restart_a_new_processor_instance_resumes_and_completes_pending_entries()
    {
        using var h = await BuildAsync("mq-restart");
        SetMergeApproval(h, true);

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: true, DateTime.UtcNow);

        // Simulate a crash mid-merge: claim the entry (State -> Merging) but never complete it —
        // no CompleteAsync call, exactly what a process kill between claim and completion leaves
        // behind. This is durable SQLite state, not anything held in this test's memory.
        var stuck = await h.Queue.ClaimNextAsync(h.Slug, approved: true, DateTime.UtcNow, CancellationToken.None);
        Assert.NotNull(stuck);
        Assert.Equal(MergeQueueState.Merging, stuck!.State);

        var beforeRestart = await h.Queue.ListAsync(h.Slug);
        Assert.Equal(MergeQueueState.Merging, Assert.Single(beforeRestart).State);

        // "Restart": a brand-new ProjectService/MergeQueueStore/MergeQueueProcessor instance
        // pointed at the SAME data directory — nothing carries over except what is on disk.
        var freshProjects = new ProjectService(h.Tmp.Path);
        var freshMembers = new MemberService(freshProjects);
        var freshTickets = new TicketService(freshProjects, freshMembers);
        var freshQueue = new MergeQueueStore(freshProjects);
        var freshAppSettings = new AppSettingsService(h.Tmp.Path);
        var freshProcessor = new MergeQueueProcessor(
            freshProjects, freshTickets, freshQueue, freshAppSettings, NullLogger<MergeQueueProcessor>.Instance);

        var resumed = await freshProcessor.ProcessProjectAsync(h.Slug, CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(MergeQueueState.Merged, resumed!.State);
        Assert.Equal(t1, resumed.TicketId);
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
    }

    // ── Approval gate + hot reload ───────────────────────────────────────────

    [Fact]
    public async Task Without_owner_approval_the_entry_holds_and_nothing_merges_approving_between_polls_lets_it_proceed()
    {
        using var h = await BuildAsync("mq-approval");
        // No SetMergeApproval call yet — the project starts unapproved.

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        var enqueue = await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: false, DateTime.UtcNow);
        Assert.Equal(MergeQueueState.Held, enqueue.Entry.State);

        // First poll: still unapproved — nothing claimable, nothing merges.
        var firstPoll = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Null(firstPoll);
        Assert.Equal(MergeQueueState.Held, Assert.Single(await h.Queue.ListAsync(h.Slug)).State);
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt"))); // unchanged

        // Owner approves between polls — settings.json is edited directly, exactly like a real
        // owner edit; the gate re-reads it on the very next call, no engine restart.
        SetMergeApproval(h, true);

        var secondPoll = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.NotNull(secondPoll);
        Assert.Equal(MergeQueueState.Merged, secondPoll!.State);
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
    }

    // ── Red integration command ──────────────────────────────────────────────

    [Fact]
    public async Task A_red_integration_command_bounces_with_the_output_excerpt_in_the_receipt()
    {
        using var h = await BuildAsync("mq-integration-red");
        SetMergeApproval(h, true);

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        // Portable failing command: a non-zero exit with recognizable stdout, understood by both
        // MergeEngine's cmd.exe (Windows) and /bin/sh (POSIX) shell invocations.
        const string failingCommand = "echo integration-output-marker && exit 1";
        await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: failingCommand, approved: true, DateTime.UtcNow);

        var result = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MergeQueueState.Bounced, result!.State);

        var ticket = await h.Tickets.GetTicketAsync(h.Slug, t1);
        Assert.Equal("Blocked", ticket!.Status);

        var receipt = Assert.Single(await CommentsAsync(h, t1), c => c.Contains("merge-bounced/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("integration-red", doc.RootElement.GetProperty("cause").GetString());
        Assert.Contains("integration-output-marker", doc.RootElement.GetProperty("outputExcerpt").GetString());

        // The rebase itself succeeded (no conflict) — only the integration gate failed — so the
        // workspace's HEAD must NOT have advanced.
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));
    }

    [Fact]
    public async Task A_green_integration_command_lets_the_merge_proceed()
    {
        using var h = await BuildAsync("mq-integration-green");
        SetMergeApproval(h, true);

        var (t1, wt1, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");
        await File.WriteAllTextAsync(Path.Combine(wt1, "shared.txt"), "base\nfrom-t1\n");
        await RunGitAsync(wt1, "add -A");
        await RunGitAsync(wt1, "commit -q -m t1-change");

        await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: "exit 0", approved: true, DateTime.UtcNow);

        var result = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MergeQueueState.Merged, result!.State);
        Assert.Equal("base\nfrom-t1\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "shared.txt")));

        var receipt = Assert.Single(await CommentsAsync(h, t1), c => c.Contains("merge-completed/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("passed", doc.RootElement.GetProperty("integration").GetString());
    }

    // ── Store-level: idempotent enqueue ──────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_is_idempotent_per_ticket_a_repeat_enqueue_returns_the_existing_active_entry()
    {
        using var h = await BuildAsync("mq-idempotent");
        var (t1, _, b1) = await CreateTicketWithWorktreeAsync(h, "Ticket one");

        var first = await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: false, DateTime.UtcNow);
        var second = await h.Queue.EnqueueAsync(h.Slug, t1, b1, integrationCommand: null, approved: false, DateTime.UtcNow.AddMinutes(1));

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Single(await h.Queue.ListAsync(h.Slug));
    }
}

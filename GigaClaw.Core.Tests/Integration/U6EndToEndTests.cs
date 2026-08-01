using System.Net;
using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Github;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Github;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Integration;

/// <summary>
/// U6 (doc/roadmap/PLAN-remaining.md §6 item 13) — the first half of the SP-4 gate: <b>one ticket
/// flows worktree → PR → CI → owner merge</b>. Each leg already has a unit suite: R5 worktrees
/// (<c>ActionExecutorWorktreeTests</c>, <c>WorktreeManagerTests</c>), R6 the merge queue
/// (<c>MergeQueueTests</c>), C7 the GitHub surface (<c>GitHubCheckStatusTests</c>,
/// <c>GitHubPullRequestTests</c>). What none of them proves is the join: that a check-run
/// conclusion arriving from GitHub actually reaches R6's <c>enqueueMerge</c>, that the owner gate
/// still stands between that and a landed commit, and that a restart in the middle does not open a
/// second pull request or enqueue the same branch twice.
/// <para>
/// <b>What is real.</b> Real git — a real workspace repository, a real R5 worktree on a real
/// <c>ticket/&lt;id&gt;</c> branch, a real <c>git push</c> into a real local bare repository, a
/// real fast-forward merge. Real services over a temp data directory: a real
/// <see cref="ActionExecutor"/>, <see cref="MergeQueueStore"/>, <see cref="MergeQueueProcessor"/>,
/// <see cref="MergeApprovalGate"/>, <see cref="FileLeaseStore"/>, and the real
/// <see cref="GitHubCheckStatusTrigger"/> polled the way <c>TriggerHandler</c> polls it.
/// </para>
/// <para>
/// <b>What is faked.</b> The GitHub HTTP transport, and nothing else: C7's
/// <see cref="FakeHttpMessageHandler"/> is the primary handler of the named
/// <see cref="GitHubApiClient"/> client, so no test here can reach the network, and every assertion
/// about "what GigaClaw sent GitHub" is made against the request the client actually built. The
/// dispatched agent is failed inside <see cref="ClaudeRunner"/> before any subprocess is spawned
/// (the <c>OllamaValidationError</c> fast-fail <c>ActionExecutorWorktreeTests</c> uses), so the
/// commit on the branch is authored by the test exactly as <c>Sp3GateTests</c> authors its own —
/// what is under test is the pipeline, not what an agent would have typed.
/// </para>
/// </summary>
[Collection("MockClaude")]
public sealed class U6EndToEndTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // Legs 1–4 — worktree → PR → CI → owner merge, one ticket, one pass
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The gate itself. A code ticket is dispatched through the production isolation path
    /// (<c>isolation: "worktree"</c>), its branch is pushed to the bare remote and a pull request
    /// opened through the GitHub surface, a green check-run conclusion arrives on that branch and
    /// fires <c>githubCheckStatus</c>, the automation's <c>enqueueMerge</c> puts the branch on R6's
    /// queue — and it stays there, unmerged, until the owner approves the project. Only then does
    /// it land, fast-forward, with the workspace file, the history, the receipts, the ticket status
    /// and the worktree all asserted afterwards.
    /// </summary>
    [Fact]
    public async Task A_ticket_flows_worktree_to_pull_request_to_green_ci_to_owner_merge()
    {
        using var tmp = new TempDir();
        using var h = await U6Harness.AttachAsync(tmp, "u6-flow", create: true);

        // ── leg 1: worktree ────────────────────────────────────────────────────
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Guard the empty header row", status: "Doing");
        await h.DispatchIsolatedAsync(ticket.Id, "programmer");

        var dispatched = (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!;
        Assert.Equal($"ticket/{ticket.Id}", dispatched.WorktreeBranch);        // durably recorded
        Assert.True(Directory.Exists(dispatched.WorktreePath));                // and really on disk
        var worktree = dispatched.WorktreePath!;
        Assert.NotEqual(Path.GetFullPath(h.Workspace), Path.GetFullPath(worktree));   // genuinely isolated
        await h.LeasesQuiesceAsync();

        // The work itself. The mock CLI writes no files, so the test authors the commit — and puts
        // the ticket reference in the subject, which is what binds the CI result back to the ticket.
        await U6Harness.CommitAsync(
            worktree, "src/exporter.txt", "base\nheader-guard\n",
            $"\"fix(exporter): guard the empty header row (ticket-{ticket.Id})\"");
        // Nothing has touched the workspace: isolation means the change is not there yet.
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "exporter.txt")));

        // ── leg 2: push + pull request ─────────────────────────────────────────
        var pr = await h.PullRequests.OpenForTicketAsync(h.Slug, ticket.Id);
        Assert.True(pr.Opened);
        Assert.Equal(7, pr.Number);

        var head = await U6Harness.GitOutAsync(worktree, "rev-parse HEAD");
        // Real git, real remote: the bare repository holds the branch at exactly the commit.
        Assert.Equal(head, await U6Harness.GitOutAsync(h.Bare, $"rev-parse refs/heads/ticket/{ticket.Id}"));
        // The request the client actually sent.
        var post = Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        Assert.Equal("/repos/acme/widgets/pulls", post.Request.RequestUri!.AbsolutePath);
        using (var body = JsonDocument.Parse(post.Body!))
        {
            Assert.Equal($"ticket/{ticket.Id}", body.RootElement.GetProperty("head").GetString());
            Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        }
        Assert.Contains(
            await h.CommentsAsync(ticket.Id),
            c => c.Contains(GitHubPullRequestService.ReceiptSchema, StringComparison.Ordinal));

        // ── leg 3: CI ──────────────────────────────────────────────────────────
        // The polled checks API is the webhook-equivalent: GigaClaw is a local app behind whatever
        // NAT the owner is on, so C7 polls rather than listens. This is that poll, on real data.
        h.ScriptChecks(U6Harness.CheckRuns((900, "build", "completed", "success")));
        var green = h.GreenRule(ticket.Id);
        var firings = await h.PollAsync(green);

        var firing = Assert.Single(firings);
        Assert.Equal(ticket.Id, firing.TicketId);      // bound by the commit message, no second API call
        var checkRequest = Assert.Single(h.Handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal));
        Assert.Contains(Uri.EscapeDataString($"ticket/{ticket.Id}"), checkRequest.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);

        await h.ExecuteAsync(green, firing);

        // ── leg 4: the owner gate ──────────────────────────────────────────────
        // enqueueMerge ran, so the branch is on the queue — and held, because no owner has approved
        // this project. Held is the whole point: CI going green is not authorization to land.
        var held = Assert.Single(await h.Queue.ListAsync(h.Slug));
        Assert.Equal(MergeQueueState.Held, held.State);
        Assert.Equal($"ticket/{ticket.Id}", held.Branch);
        Assert.Contains(await h.CommentsAsync(ticket.Id), c => c.Contains("merge-held/v1", StringComparison.Ordinal));
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "exporter.txt")));
        Assert.DoesNotContain(await h.CommentsAsync(ticket.Id), c => c.Contains("merge-completed/v1", StringComparison.Ordinal));

        // The owner edits settings.json between polls. No restart, no re-enqueue.
        h.SetMergeApproval(true);

        var merged = await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal(ticket.Id, merged.TicketId);
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));   // queue drained

        // ── the final truth: workspace, remote, history, ticket, worktree ──────
        Assert.Equal("base\nheader-guard\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "exporter.txt")));
        // Fast-forward, so history is linear: two commits, no merge commit, and HEAD is the branch tip.
        var log = await U6Harness.GitOutAsync(h.Workspace, "log --oneline");
        Assert.Equal(2, log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Empty(await U6Harness.GitOutAsync(h.Workspace, "log --merges --oneline"));
        Assert.Equal(head, await U6Harness.GitOutAsync(h.Workspace, "rev-parse HEAD"));
        // The bare remote still holds exactly what was pushed — GigaClaw landed the merge locally
        // and never pushed main; the remote is the CI/review surface, not the merge target.
        Assert.Equal(head, await U6Harness.GitOutAsync(h.Bare, $"rev-parse refs/heads/ticket/{ticket.Id}"));

        var completed = Assert.Single(await h.CommentsAsync(ticket.Id), c => c.Contains("merge-completed/v1", StringComparison.Ordinal));
        using (var receipt = JsonDocument.Parse(completed))
        {
            Assert.Equal("merge-completed/v1", receipt.RootElement.GetProperty("schema").GetString());
            Assert.Equal(ticket.Id, receipt.RootElement.GetProperty("ticketId").GetInt32());
            Assert.Equal($"ticket/{ticket.Id}", receipt.RootElement.GetProperty("branch").GetString());
        }

        // Done closes the loop, and R5's cleanup semantics apply: the branch is now an ancestor of
        // HEAD and the checkout is clean, so — and only so — the worktree is removed.
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Done", "automation");
        var final = (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!;
        Assert.Equal("Done", final.Status);
        Assert.Equal("cleaned", final.WorktreeStatus);
        Assert.False(Directory.Exists(worktree));
        Assert.DoesNotContain(await h.CommentsAsync(ticket.Id), c => c.Contains("worktree-cleanup-blocked/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(await h.CommentsAsync(ticket.Id), c => c.Contains("merge-bounced/v1", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Leg 5 — a red check must not enqueue
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The failure leg, asserted the way the SP-3 gate asserts every refusal: once on what did not
    /// happen and once on the record that says why. A failing check fires the failure automation
    /// (which comments and does not enqueue) and does <b>not</b> fire the success automation, so
    /// nothing reaches the merge queue at all — even with the owner's approval already in place,
    /// which is the strong form: the red result is what stops the merge, not the missing approval.
    /// </summary>
    [Fact]
    public async Task A_red_check_records_why_and_nothing_reaches_the_merge_queue()
    {
        using var tmp = new TempDir();
        using var h = await U6Harness.AttachAsync(tmp, "u6-red", create: true);
        h.SetMergeApproval(true);   // approval is not what is holding this back

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Guard the empty header row", status: "Doing");
        await h.DispatchIsolatedAsync(ticket.Id, "programmer");
        var worktree = (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.WorktreePath!;
        await h.LeasesQuiesceAsync();
        await U6Harness.CommitAsync(
            worktree, "src/exporter.txt", "base\nbroken\n",
            $"\"fix(exporter): guard the empty header row (ticket-{ticket.Id})\"");
        Assert.True((await h.PullRequests.OpenForTicketAsync(h.Slug, ticket.Id)).Opened);

        h.ScriptChecks(U6Harness.CheckRuns((900, "build", "completed", "failure")));

        // The success automation looks at the same commit and sees nothing to act on.
        var green = h.GreenRule(ticket.Id);
        Assert.Empty(await h.PollAsync(green));

        // The failure automation fires and records the result on the ticket.
        var red = h.RedRule(ticket.Id);
        var firing = Assert.Single(await h.PollAsync(red));
        Assert.Equal(ticket.Id, firing.TicketId);
        await h.ExecuteAsync(red, firing);

        // Nothing reached the queue — not held, not queued, not bounced: nothing.
        Assert.Empty(await h.Queue.ListAsync(h.Slug));
        Assert.Null(await h.Processor.ProcessProjectAsync(h.Slug, CancellationToken.None));
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(h.Workspace, "src", "exporter.txt")));

        // The record on the ticket. Note what it can and cannot say: once a firing is bound to a
        // ticket the check-run's own name is no longer in the firing, so `addComment`'s
        // {ticketTitle} placeholder renders the ticket, not the check. Naming the failing check on
        // the ticket would need a placeholder the action vocabulary does not have — recorded in
        // doc/roadmap/U6-EVIDENCE.md rather than papered over here.
        var comments = await h.CommentsAsync(ticket.Id);
        Assert.Contains(comments, c => c.Contains(U6Harness.RedMarker, StringComparison.Ordinal));
        Assert.Contains(comments, c => c.Contains($"ticket-{ticket.Id}", StringComparison.Ordinal)
                                    && c.Contains("NOT enqueued", StringComparison.Ordinal));
        Assert.DoesNotContain(comments, c => c.Contains("merge-held/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(comments, c => c.Contains("merge-completed/v1", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Leg 6 — a restart between the PR and CI
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every service is thrown away and rebuilt over the same data directory at the point the flow
    /// is most exposed: the pull request exists, but no check result has arrived yet. The new
    /// process must finish the flow without doing anything twice — C7's idempotency (the PR is
    /// re-found on GitHub rather than re-created) composing with R6's per-ticket enqueue idempotency
    /// and its <c>Merging</c>-claim recovery.
    /// </summary>
    [Fact]
    public async Task A_restart_between_the_pull_request_and_ci_completes_without_a_duplicate_pr_or_a_double_enqueue()
    {
        using var tmp = new TempDir();
        int ticketId;
        string worktree, head;

        // ── first process: dispatch, commit, push, open the PR ─────────────────
        {
            using var h = await U6Harness.AttachAsync(tmp, "u6-restart", create: true);
            var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Guard the empty header row", status: "Doing");
            ticketId = ticket.Id;
            await h.DispatchIsolatedAsync(ticketId, "programmer");
            worktree = (await h.Tickets.GetTicketAsync(h.Slug, ticketId))!.WorktreePath!;
            await h.LeasesQuiesceAsync();
            await U6Harness.CommitAsync(
                worktree, "src/exporter.txt", "base\nheader-guard\n",
                $"\"fix(exporter): guard the empty header row (ticket-{ticketId})\"");
            head = await U6Harness.GitOutAsync(worktree, "rev-parse HEAD");

            Assert.True((await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId)).Opened);
            Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        }

        // ── restart: brand-new everything over the same directory ──────────────
        // GitHub now reports the pull request the dead process opened. Nothing in memory survived;
        // the only thing that can prevent a duplicate is what GitHub says now.
        using var resumed = await U6Harness.AttachAsync(
            tmp, "u6-restart", create: false,
            existingPulls: """[{"number": 7, "html_url": "https://github.test/acme/widgets/pull/7"}]""");

        var again = await resumed.PullRequests.OpenForTicketAsync(resumed.Slug, ticketId);
        Assert.True(again.AlreadyOpen);
        Assert.False(again.Opened);
        Assert.Equal(7, again.Number);
        Assert.DoesNotContain(resumed.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        Assert.Single(
            await resumed.CommentsAsync(ticketId),
            c => c.Contains(GitHubPullRequestService.ReceiptSchema, StringComparison.Ordinal));

        // ── CI arrives in the new process ──────────────────────────────────────
        resumed.ScriptChecks(U6Harness.CheckRuns((900, "build", "completed", "success")));
        var green = resumed.GreenRule(ticketId);
        var firing = Assert.Single(await resumed.PollAsync(green));

        // The same green result delivered twice — a second poll, a re-fired automation — enqueues
        // once: the trigger's durable seen-state refuses the second firing, and the queue's
        // per-ticket active-entry uniqueness would refuse a second entry even if it did not.
        await resumed.ExecuteAsync(green, firing);
        Assert.Empty(await resumed.PollAsync(green));
        await resumed.ExecuteAsync(green, firing);
        var entry = Assert.Single(await resumed.Queue.ListAsync(resumed.Slug));
        Assert.Equal(MergeQueueState.Held, entry.State);
        Assert.Single(await resumed.CommentsAsync(ticketId), c => c.Contains("merge-held/v1", StringComparison.Ordinal));

        // ── the owner approves, and a crash mid-merge is recovered ─────────────
        resumed.SetMergeApproval(true);
        // Claim the entry and abandon it, exactly as a process killed mid-merge would.
        var stuck = await resumed.Queue.ClaimNextAsync(resumed.Slug, approved: true, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merging, stuck!.State);

        using var third = await U6Harness.AttachAsync(tmp, "u6-restart", create: false);
        var merged = await third.Processor.ProcessProjectAsync(third.Slug, CancellationToken.None);
        Assert.Equal(MergeQueueState.Merged, merged!.State);
        Assert.Equal(ticketId, merged.TicketId);
        Assert.Null(await third.Processor.ProcessProjectAsync(third.Slug, CancellationToken.None));

        Assert.Equal("base\nheader-guard\n", await File.ReadAllTextAsync(Path.Combine(third.Workspace, "src", "exporter.txt")));
        Assert.Equal(head, await U6Harness.GitOutAsync(third.Workspace, "rev-parse HEAD"));
        Assert.Single(await third.CommentsAsync(ticketId), c => c.Contains("merge-completed/v1", StringComparison.Ordinal));

        await third.Tickets.MoveTicketAsync(third.Slug, ticketId, "Done", "automation");
        var final = (await third.Tickets.GetTicketAsync(third.Slug, ticketId))!;
        Assert.Equal("cleaned", final.WorktreeStatus);
        Assert.False(Directory.Exists(worktree));
    }
}

/// <summary>
/// One full set of the real services over one temp data directory, widened from
/// <c>Sp3Harness</c> to carry the C7 GitHub surface as well: the merge queue and its processor, the
/// R4 lease store, an <see cref="ActionExecutor"/> wired to both, plus a
/// <see cref="GitHubApiClient"/> whose only fake part is its transport. <see cref="AttachAsync"/>
/// builds a second, entirely independent set over the same directory — which is what "the engine
/// restarted" means for state that lives in SQLite, in git, and in the workspace's
/// <c>dispatch-state.json</c> rather than in this process.
/// </summary>
internal sealed class U6Harness : IDisposable
{
    /// <summary>Fails a dispatch inside <see cref="ClaudeRunner"/> before any subprocess is spawned
    /// (<c>OllamaValidationError</c>) — the idiom <c>ActionExecutorWorktreeTests</c> uses when what
    /// is under test is whether the dispatch was allowed, not what the agent then did.</summary>
    public const string NonClaudeModel = "qwen3-coder:30b";

    /// <summary>The failure automation's comment marker — the vocabulary a red CI result leaves.</summary>
    public const string RedMarker = "GIGACLAW-CI v1 red";

    private const string SrcGlob = "src/**";

    private readonly TempDir _tmp;
    private volatile string _existingPulls;
    private volatile string _checkRuns;
    private int _automationSeq;

    private U6Harness(
        TempDir tmp, ProjectService projects, TicketService tickets, string slug, string workspace,
        string existingPulls)
    {
        _tmp = tmp;
        _existingPulls = existingPulls;
        _checkRuns = CheckRuns();
        Projects = projects;
        Tickets = tickets;
        Slug = slug;
        Workspace = workspace;
        Bare = Path.Combine(tmp.Path, "remote.git");

        var members = new MemberService(projects);
        Members = members;
        var sessions = new SessionRegistry();
        var appSettings = new AppSettingsService(tmp.Path);
        var cost = new CostTracker();
        Settings = appSettings;
        Sessions = sessions;

        AgentRuns = new AgentRunRegistry();
        Leases = new FileLeaseStore(projects);
        Queue = new MergeQueueStore(projects);
        Processor = new MergeQueueProcessor(
            projects, tickets, Queue, Leases, appSettings, NullLogger<MergeQueueProcessor>.Instance);

        var gate = new OutboundApprovalGate(appSettings.GetApprovedOutboundHosts);
        ReceiptSink = new RecordingReceiptSink();
        // One handler for the harness's whole life, so Requests is the complete transcript of what
        // GigaClaw sent GitHub; the answers it gives are re-scriptable mid-flow (CI has not run
        // yet; now it has) by swapping the two fields it reads.
        Handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(Respond(request)));
        Client = new GitHubApiClient(
            new FakeHttpClientFactory(Handler), gate, ReceiptSink, NullLogger<GitHubApiClient>.Instance);
        Links = new GitHubIssueLinkStore(projects);
        PullRequests = new GitHubPullRequestService(
            appSettings, Client, projects, tickets, gate, ReceiptSink,
            NullLogger<GitHubPullRequestService>.Instance);

        Executor = new ActionExecutor(
            tickets, members, new LabelService(projects), sessions, AgentRuns,
            new ClaudeRunner(sessions, AgentRuns, new RunConcurrencyGate(maxConcurrent: 1), NullLogger<ClaudeRunner>.Instance),
            cost, new LocalizationService(appSettings), projects,
            new RunStateManager(AgentRuns, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused,
            new TeamRunService(new TeamStore(projects, tickets), tickets, members, new AgentTeamService(), NullLogger<TeamRunService>.Instance),
            NullLogger.Instance,
            outboundGate: null,
            leases: Leases,
            mergeQueue: Queue,
            mergeApproval: new MergeApprovalGate(appSettings.GetApprovedMergeProjects));

        Runtime = new ProjectRuntime(slug) { Workspace = workspace, Config = new AutomationConfig() };
    }

    public ProjectService Projects { get; }
    public MemberService Members { get; }
    public TicketService Tickets { get; }
    public AppSettingsService Settings { get; }
    public SessionRegistry Sessions { get; }
    public AgentRunRegistry AgentRuns { get; }
    public FileLeaseStore Leases { get; }
    public MergeQueueStore Queue { get; }
    public MergeQueueProcessor Processor { get; }
    public GitHubApiClient Client { get; }
    public GitHubIssueLinkStore Links { get; }
    public GitHubPullRequestService PullRequests { get; }
    public RecordingReceiptSink ReceiptSink { get; }
    public ActionExecutor Executor { get; }
    public ProjectRuntime Runtime { get; }
    public string Slug { get; }
    public string Workspace { get; }

    /// <summary>The local bare repository standing in for the GitHub remote. Real git, no network.</summary>
    public string Bare { get; }

    /// <summary>The fake transport — every request GigaClaw sent GitHub, in order.</summary>
    public FakeHttpMessageHandler Handler { get; }

    public IReadOnlyList<(string Slug, int? TicketId, OutboundReceipt Receipt)> Receipts => ReceiptSink.Receipts;

    // ── construction ────────────────────────────────────────────────────────

    public static async Task<U6Harness> AttachAsync(
        TempDir tmp, string name, bool create, string? existingPulls = null)
    {
        var projects = new ProjectService(tmp.Path);
        var project = create
            ? await projects.CreateProjectAsync(name)
            : (await projects.ListProjectsAsync()).Single(p => p.Name == name);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);

        if (create)
        {
            await InitRepoAsync(workspace, Path.Combine(tmp.Path, "remote.git"));
            var existing = (await members.ListMembersAsync(project.Slug))
                .Select(member => member.Slug)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains("programmer"))
                await members.CreateMemberAsync(project.Slug, "programmer");
        }

        // Rewritten on attach as well as on create: a restart re-reads contracts.json and the skill
        // files from the workspace, so they have to be there for the second process too.
        WriteContract(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");

        var harness = new U6Harness(tmp, projects, tickets, project.Slug, workspace, existingPulls ?? "[]");
        // The GitHub surface is configured on every attach for the same reason: settings.json is
        // shared, but ApiBaseUrl/remote/base are what make this project point at the fake.
        harness.Settings.ConfigureGitHub(project.Slug, GitHubTestHarness.Config(), GitHubTestHarness.Token);
        harness.OwnerApprovesHost(GitHubTestHarness.ApiHost);
        return harness;
    }

    public void Dispose() { }

    /// <summary>Re-scripts what the checks API answers mid-flow — "CI has now run".</summary>
    public void ScriptChecks(string checkRunsJson) => _checkRuns = checkRunsJson;

    // ── GitHub scripting ────────────────────────────────────────────────────

    /// <summary>
    /// The whole GitHub surface U6 touches, matched on method + path the way
    /// <c>GitHubApiScript</c> does. Anything unrouted is a 404, so a request this suite did not
    /// anticipate fails loudly rather than being quietly answered.
    /// </summary>
    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        if (request.Method == HttpMethod.Get && path == "/repos/acme/widgets/pulls" && query.Contains("head=", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, _existingPulls);
        if (request.Method == HttpMethod.Post && path == "/repos/acme/widgets/pulls")
            return Json(HttpStatusCode.Created, """{"number": 7, "html_url": "https://github.test/acme/widgets/pull/7"}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/check-runs", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, _checkRuns);

        return Json(HttpStatusCode.NotFound, """{"message":"no scripted route"}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    public static string CheckRuns(params (long Id, string Name, string Status, string? Conclusion)[] runs) =>
        $$"""{"total_count": {{runs.Length}}, "check_runs": [""" +
        string.Join(",", runs.Select(r => $$"""
            {"id": {{r.Id}}, "name": "{{r.Name}}", "status": "{{r.Status}}",
             "conclusion": {{(r.Conclusion is null ? "null" : $"\"{r.Conclusion}\"")}}}
            """)) + "]}";

    // ── automations ─────────────────────────────────────────────────────────

    /// <summary>
    /// The success automation: a green check on the ticket's branch enqueues the merge. This is the
    /// composition U6 exists to prove — C7's trigger driving R6's action — and it is declared here
    /// rather than in <c>ProjectTemplate/Agents/automations.json</c>, which ships no
    /// <c>githubCheckStatus</c> automation at all (see doc/roadmap/U6-EVIDENCE.md).
    /// </summary>
    public AutomationRule GreenRule(int ticketId) => new()
    {
        Id = "u6-ci-green-enqueues-merge",
        Enabled = true,
        Trigger = new GitHubCheckStatusTriggerSpec
        {
            PollSeconds = 0,
            Conclusions = ["success"],
            Ref = $"ticket/{ticketId}",
        },
        Conditions = [],
        Actions =
        [
            new AddCommentActionSpec
            {
                Author = "automation",
                Content = "GIGACLAW-CI v1 green ticket-{ticketId} — every check on this branch concluded successfully; the branch is enqueued for the owner's merge queue.",
            },
            new EnqueueMergeActionSpec(),
        ],
    };

    /// <summary>
    /// The failure automation, built from the vocabulary that already exists: it comments and it
    /// does not enqueue. Nothing more is needed for the leg U6 has to prove — that a red check
    /// leaves a record and reaches no queue.
    /// </summary>
    public AutomationRule RedRule(int ticketId) => new()
    {
        Id = "u6-ci-red-records-and-stops",
        Enabled = true,
        Trigger = new GitHubCheckStatusTriggerSpec
        {
            PollSeconds = 0,
            Conclusions = ["failure", "timed_out", "cancelled"],
            Ref = $"ticket/{ticketId}",
        },
        Conditions = [],
        Actions =
        [
            new AddCommentActionSpec
            {
                Author = "automation",
                Content = RedMarker + " ticket-{ticketId} — {ticketTitle}: a check on this branch did not conclude successfully, so the branch was NOT enqueued for merge. Fix the failure and push again.",
            },
        ],
    };

    private TriggerContext Context(AutomationRule automation) => new()
    {
        ProjectSlug = Slug,
        WorkspacePath = Workspace,
        Automation = automation,
        Tickets = Tickets,
        Members = Members,
        Sessions = Sessions,
        Runs = AgentRuns,
        Now = DateTime.UtcNow,
    };

    /// <summary>One engine tick for one automation: exactly what <c>TriggerHandler</c> does.</summary>
    public async Task<IReadOnlyList<TriggerFiring>> PollAsync(AutomationRule automation) =>
        await new GitHubCheckStatusTrigger(
            (GitHubCheckStatusTriggerSpec)automation.Trigger!,
            new GitHubTriggerServices(Client, Settings, Links))
            .EvaluateAsync(Context(automation), CancellationToken.None);

    /// <summary>The other half of the tick: conditions, then the action chain.</summary>
    public async Task ExecuteAsync(AutomationRule automation, TriggerFiring firing)
    {
        Assert.True(await Executor.ConditionsMatchAsync(Runtime, automation, firing));
        await Executor.ExecuteAutomationAsync(Runtime, automation, firing, CancellationToken.None);
    }

    /// <summary>
    /// The ordinary per-agent dispatch an agent's own automation carries, with R5 isolation on —
    /// the production path <c>assignee-dispatch-code</c> takes for a code-touching contract.
    /// </summary>
    public async Task DispatchIsolatedAsync(int ticketId, string agent)
    {
        var rule = new AutomationRule
        {
            Id = $"u6-dispatch-{agent}-{Interlocked.Increment(ref _automationSeq)}",
            Enabled = true,
            Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo", "Doing", "Review"] },
            Conditions = [],
            Actions = [new RunAgentActionSpec
            {
                Agent = agent, MaxTurns = 1, Model = NonClaudeModel, Isolation = "worktree",
            }],
        };
        var ticket = await Tickets.GetTicketAsync(Slug, ticketId);
        await Executor.ExecuteAutomationAsync(
            Runtime, rule, new TriggerFiring(ticketId, ticket!.Title, ticket.Status), CancellationToken.None);
        Assert.True(
            await WaitUntilAsync(async () => (await Tickets.GetTicketAsync(Slug, ticketId))!.WorktreePath is not null),
            $"worktree isolation never recorded a worktree for ticket #{ticketId}");
    }

    /// <summary>A dispatch takes its lease before the run starts and releases it when the run ends;
    /// the merge-queue interlock reads that table, so the flow has to let go first.</summary>
    public async Task LeasesQuiesceAsync() =>
        Assert.True(
            await WaitUntilAsync(async () => (await Leases.ListActiveAsync(Slug)).Count == 0),
            "a dispatched run never released its file lease");

    // ── observation / owner actions ─────────────────────────────────────────

    public async Task<List<string>> CommentsAsync(int ticketId)
    {
        var ticket = await Tickets.GetTicketAsync(Slug, ticketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    /// <summary>Merges into settings.json directly — the same file the gates re-read per call, so
    /// this is exactly an owner editing it by hand between two polls.</summary>
    public void SetMergeApproval(bool approved) =>
        UpdateSettings(root => root["ApprovedMergeProjects"] = approved
            ? new System.Text.Json.Nodes.JsonArray { Slug }
            : new System.Text.Json.Nodes.JsonArray());

    public void OwnerApprovesHost(params string[] hosts) =>
        UpdateSettings(root =>
        {
            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (var host in hosts) array.Add(host);
            root["ApprovedOutboundHosts"] = array;
        });

    private void UpdateSettings(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var path = Path.Combine(_tmp.Path, "settings.json");
        var root = File.Exists(path)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject ?? []
            : [];
        edit(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            if (await condition()) return true;
            try { await Task.Delay(25, cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        return await condition();
    }

    // ── git ─────────────────────────────────────────────────────────────────

    public static async Task RunGitAsync(string cwd, string args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(60));
        Assert.True(result.Success, $"git {args} failed in {cwd}: {result.Stderr}\n{result.Stdout}");
    }

    public static async Task<string> GitOutAsync(string cwd, string args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(60));
        Assert.True(result.Success, $"git {args} failed in {cwd}: {result.Stderr}\n{result.Stdout}");
        return result.Stdout.Trim();
    }

    public static async Task CommitAsync(string worktree, string relativePath, string content, string message)
    {
        var full = Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await RunGitAsync(worktree, "add -A");
        await RunGitAsync(worktree, $"commit -q -m {message}");
    }

    private static async Task InitRepoAsync(string workspace, string bare)
    {
        await RunGitAsync(workspace, "init -q");
        // Deterministic default branch across git versions, so the pull request's base is real.
        await RunGitAsync(workspace, "symbolic-ref HEAD refs/heads/main");
        await RunGitAsync(workspace, "config user.email test@example.com");
        await RunGitAsync(workspace, "config user.name \"GigaClaw Test\"");
        await RunGitAsync(workspace, "config commit.gpgsign false");
        // Windows' Git ships core.autocrlf=true, which silently rewrites LF blobs to CRLF on
        // `git worktree add` / `git merge --ff-only` checkouts in a temp repo with no
        // .gitattributes — corrupting the exact bytes the assertions compare. Pin it off, exactly
        // as MergeQueueTests learned to (commit 4082184).
        await RunGitAsync(workspace, "config core.autocrlf false");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "exporter.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello\n");
        await RunGitAsync(workspace, "add -A");
        await RunGitAsync(workspace, "commit -q -m initial");

        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        await RunGitAsync(workspace, $"init -q --bare \"{bare}\"");
        await RunGitAsync(workspace, $"remote add origin \"{bare}\"");
    }

    /// <summary>The R4 contract for the dispatched agent: one scope, block mode — the shape the
    /// lease gate and the merge-queue interlock both read.</summary>
    private static void WriteContract(string workspace)
    {
        var agentsDir = Path.Combine(workspace, ".agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "contracts.json"), $$"""
            {
              "version": 1,
              "defaults": {
                "maxDispatchAttempts": 3,
                "retryBackoffSeconds": 300,
                "requireAtomicHandoff": true,
                "requireAuthorOnBoardWrites": true
              },
              "agents": {
                "programmer": {
                  "enforcement": "block",
                  "dispatches": ["assignment"],
                  "riskClass": "code-write",
                  "allowedWriteGlobs": ["{{SrcGlob}}"],
                  "ticketExit": ["Review", "Blocked", "Done"]
                }
              }
            }
            """);
    }
}

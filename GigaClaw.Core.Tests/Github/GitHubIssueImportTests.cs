using System.Net;
using System.Text.Json;
using GigaClaw.Core.Github;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// C7 part 1: labeled issues become tickets, exactly once, and finished tickets answer back.
/// Every assertion here is about a criterion the roadmap names — idempotence across a re-sync and
/// across a restart, the round trip, the policy preflight, and the token never leaving settings.
/// </summary>
public class GitHubIssueImportTests
{
    private const string IssuesPath = "/repos/acme/widgets/issues";

    private static string IssuesJson(params (int Number, string Title, string Body, string Updated)[] issues) =>
        "[" + string.Join(",", issues.Select(i => $$"""
            {
              "number": {{i.Number}},
              "title": {{JsonSerializer.Serialize(i.Title)}},
              "body": {{JsonSerializer.Serialize(i.Body)}},
              "state": "open",
              "html_url": "https://github.test/acme/widgets/issues/{{i.Number}}",
              "updated_at": "{{i.Updated}}"
            }
            """)) + "]";

    private static GitHubApiScript TwoIssues() => new GitHubApiScript()
        .Get(IssuesPath, HttpStatusCode.OK, IssuesJson(
            (11, "Crash on export", "Steps to reproduce...", "2026-07-01T10:00:00Z"),
            (12, "Add dark mode", "Users keep asking.", "2026-07-02T10:00:00Z")));

    private static async Task<GitHubTestHarness> ReadyAsync(
        GitHubApiScript script, GitHubProjectConfig? config = null)
    {
        var harness = await GitHubTestHarness.BuildAsync(script.Build());
        harness.OwnerApproves(GitHubTestHarness.ApiHost);
        harness.ConfigureGitHub(config ?? GitHubTestHarness.Config());
        return harness;
    }

    // ── Import ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Labeled_issues_become_tickets()
    {
        using var h = await ReadyAsync(TwoIssues());

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.True(result.Ran, result.Reason);
        Assert.Equal(2, result.Imported);
        var tickets = await h.Tickets.ListTicketsAsync(h.Slug);
        Assert.Equal(2, tickets.Count);
        Assert.Contains(tickets, t => t.Title == "Crash on export");
        Assert.Contains(tickets, t => t.Title == "Add dark mode");
    }

    [Fact]
    public async Task Only_the_configured_label_is_requested()
    {
        using var h = await ReadyAsync(TwoIssues(), GitHubTestHarness.Config(label: "needs-agent"));

        await h.Sync.SyncAsync(h.Slug);

        Assert.Contains("labels=needs-agent", h.Handler.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task Pull_requests_in_the_issues_feed_are_not_imported()
    {
        var script = new GitHubApiScript().Get(IssuesPath, HttpStatusCode.OK, """
            [
              {"number": 11, "title": "Crash on export", "body": "x", "state": "open",
               "html_url": "https://github.test/acme/widgets/issues/11", "updated_at": "2026-07-01T10:00:00Z"},
              {"number": 12, "title": "Fix the crash", "body": "y", "state": "open",
               "html_url": "https://github.test/acme/widgets/pull/12", "updated_at": "2026-07-02T10:00:00Z",
               "pull_request": {"url": "https://api.github.test/repos/acme/widgets/pulls/12"}}
            ]
            """);
        using var h = await ReadyAsync(script);

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.Equal(1, result.Imported);
        Assert.Equal("Crash on export", Assert.Single(await h.Tickets.ListTicketsAsync(h.Slug)).Title);
    }

    // ── Idempotence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Running_sync_twice_creates_no_duplicate_tickets()
    {
        using var h = await ReadyAsync(TwoIssues());

        var first = await h.Sync.SyncAsync(h.Slug);
        var second = await h.Sync.SyncAsync(h.Slug);

        Assert.Equal(2, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(2, (await h.Tickets.ListTicketsAsync(h.Slug)).Count);
        Assert.Equal(2, (await h.Links.ListAsync(h.Slug)).Count);
    }

    [Fact]
    public async Task A_restart_reads_the_mapping_table_and_still_creates_no_duplicates()
    {
        using var h = await ReadyAsync(TwoIssues());
        await h.Sync.SyncAsync(h.Slug);

        // Every service rebuilt over the same data dir: nothing in memory survives.
        var restarted = h.Restart(TwoIssues().Build());
        var afterRestart = await restarted.Sync.SyncAsync(restarted.Slug);

        Assert.Equal(0, afterRestart.Imported);
        Assert.Equal(2, afterRestart.Unchanged);
        Assert.Equal(2, (await restarted.Tickets.ListTicketsAsync(restarted.Slug)).Count);
    }

    [Fact]
    public async Task An_edited_issue_updates_its_ticket_rather_than_adding_one()
    {
        using var h = await ReadyAsync(TwoIssues());
        await h.Sync.SyncAsync(h.Slug);

        var edited = new GitHubApiScript().Get(IssuesPath, HttpStatusCode.OK, IssuesJson(
            (11, "Crash on export (still)", "New steps.", "2026-07-09T10:00:00Z"),
            (12, "Add dark mode", "Users keep asking.", "2026-07-02T10:00:00Z")));
        var restarted = h.Restart(edited.Build());

        var result = await restarted.Sync.SyncAsync(restarted.Slug);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Updated);
        var tickets = await restarted.Tickets.ListTicketsAsync(restarted.Slug);
        Assert.Equal(2, tickets.Count);
        Assert.Contains(tickets, t => t.Title == "Crash on export (still)");
    }

    [Fact]
    public async Task A_deleted_ticket_is_not_resurrected_by_the_next_sync()
    {
        using var h = await ReadyAsync(TwoIssues());
        await h.Sync.SyncAsync(h.Slug);
        var victim = (await h.Tickets.ListTicketsAsync(h.Slug)).First(t => t.Title == "Add dark mode");
        Assert.True(await h.Tickets.DeleteTicketAsync(h.Slug, victim.Id));

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.Equal(0, result.Imported);
        Assert.Single(await h.Tickets.ListTicketsAsync(h.Slug));
    }

    // ── Round trip ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_done_ticket_comments_on_and_closes_its_issue_exactly_once()
    {
        var script = TwoIssues()
            .Post($"{IssuesPath}/11/comments", HttpStatusCode.Created, """{"id":1}""")
            .Patch($"{IssuesPath}/11", HttpStatusCode.OK, """{"state":"closed"}""");
        using var h = await ReadyAsync(script, GitHubTestHarness.Config(commentOnDone: true, closeOnDone: true));

        await h.Sync.SyncAsync(h.Slug);
        var ticket = (await h.Tickets.ListTicketsAsync(h.Slug)).First(t => t.Title == "Crash on export");
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Done", "owner");

        var closing = await h.Sync.SyncAsync(h.Slug);
        var second = await h.Sync.SyncAsync(h.Slug);

        Assert.Equal(1, closing.CommentedIssues);
        Assert.Equal(1, closing.ClosedIssues);
        Assert.Equal(0, second.CommentedIssues);
        Assert.Equal(0, second.ClosedIssues);
        Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task The_round_trip_stays_off_unless_the_project_asks_for_it()
    {
        using var h = await ReadyAsync(TwoIssues());
        await h.Sync.SyncAsync(h.Slug);
        var ticket = (await h.Tickets.ListTicketsAsync(h.Slug)).First();
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Done", "owner");

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.Equal(0, result.CommentedIssues);
        Assert.Equal(0, result.ClosedIssues);
        Assert.DoesNotContain(h.Handler.Requests, r => r.Request.Method != HttpMethod.Get);
    }

    // ── Local-first default ─────────────────────────────────────────────────

    [Fact]
    public async Task An_unconfigured_project_never_reaches_the_network()
    {
        using var h = await GitHubTestHarness.BuildAsync(TwoIssues().Build());

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.False(result.Ran);
        Assert.Contains("not enabled", result.Reason);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task A_configured_project_without_a_token_never_reaches_the_network()
    {
        using var h = await GitHubTestHarness.BuildAsync(TwoIssues().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config(), token: null);

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.False(result.Ran);
        Assert.Contains("token", result.Reason);
        Assert.Empty(h.Handler.Requests);
    }
}

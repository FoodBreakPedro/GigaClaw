using System.Net;
using System.Text.Json;
using GigaClaw.Core.Automation.Policy;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// C7 acceptance criterion: "network calls pass the P3 policy layer". The GitHub surface is a
/// second outbound path beside the <c>httpRequest</c> action, and a second path that skipped the
/// gate would quietly undo R3 — the whole point of the approved-host list is that it is the only
/// way anything leaves the process.
/// <para>
/// These tests prove both halves of the contract: a blocked verdict stops the call and produces the
/// <c>outbound-denial/v1</c> receipt, and an approved host actually sends. The receipt is checked
/// field by field against the shape <c>ActionExecutor</c> writes, so the two surfaces stay one
/// vocabulary rather than two.
/// </para>
/// </summary>
public class GitHubOutboundPolicyTests
{
    private const string IssuesPath = "/repos/acme/widgets/issues";

    private static GitHubApiScript Script() => new GitHubApiScript()
        .Get(IssuesPath, HttpStatusCode.OK, """
            [{"number": 11, "title": "Crash on export", "body": "b", "state": "open",
              "html_url": "https://github.test/acme/widgets/issues/11",
              "updated_at": "2026-07-01T10:00:00Z"}]
            """);

    [Fact]
    public async Task A_blocked_verdict_stops_the_sync_before_anything_is_sent()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        // A fresh install approves no host: the gate's default is deny.
        h.ConfigureGitHub(GitHubTestHarness.Config());

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.False(result.Ran);
        Assert.True(result.DryRun);
        Assert.Empty(h.Handler.Requests);
        Assert.Empty(await h.Tickets.ListTicketsAsync(h.Slug));
    }

    [Fact]
    public async Task A_refused_github_call_produces_the_outbound_denial_receipt()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.ConfigureGitHub(GitHubTestHarness.Config());

        await h.Sync.SyncAsync(h.Slug);

        var entry = Assert.Single(h.Receipts);
        Assert.Equal(h.Slug, entry.Slug);
        using var doc = JsonDocument.Parse(entry.Receipt.ToJson());
        var root = doc.RootElement;
        Assert.Equal("outbound-denial/v1", root.GetProperty("schema").GetString());
        Assert.Equal("github-issue-sync", root.GetProperty("agent").GetString());
        Assert.Equal("githubRequest", root.GetProperty("action").GetString());
        Assert.Equal(GitHubTestHarness.ApiHost, root.GetProperty("host").GetString());
        Assert.Equal("outbound-approval", root.GetProperty("rule").GetString());
        Assert.Equal("dry-run", root.GetProperty("enforcementMode").GetString());
        Assert.Contains(GitHubTestHarness.ApiHost, root.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task An_approved_host_lets_the_sync_through()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.True(result.Ran, result.Reason);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Receipts);
    }

    [Fact]
    public async Task Approval_is_read_per_call_so_an_owner_edit_needs_no_restart()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.ConfigureGitHub(GitHubTestHarness.Config());

        Assert.True((await h.Sync.SyncAsync(h.Slug)).DryRun);
        Assert.Empty(h.Handler.Requests);

        h.OwnerApproves(GitHubTestHarness.ApiHost);   // same service instances throughout

        Assert.True((await h.Sync.SyncAsync(h.Slug)).Ran);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Approving_github_does_not_open_a_lookalike_host()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config() with { ApiBaseUrl = "https://notapi.github.test" });

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.True(result.DryRun);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task The_round_trip_write_is_gated_too_not_just_the_read()
    {
        // Import while approved, then withdraw the approval before the closing pass: the POST and
        // PATCH must be refused exactly as the GET would be. A gate that only covered the entry
        // read would leave the writes — the calls that change GitHub — ungoverned.
        var script = Script()
            .Post($"{IssuesPath}/11/comments", HttpStatusCode.Created, """{"id":1}""")
            .Patch($"{IssuesPath}/11", HttpStatusCode.OK, """{"state":"closed"}""");
        using var h = await GitHubTestHarness.BuildAsync(script.Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config(commentOnDone: true, closeOnDone: true));

        await h.Sync.SyncAsync(h.Slug);
        var ticket = Assert.Single(await h.Tickets.ListTicketsAsync(h.Slug));
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Done", "owner");

        h.OwnerApproves();   // owner revokes
        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.True(result.DryRun);
        Assert.DoesNotContain(h.Handler.Requests, r => r.Request.Method != HttpMethod.Get);
        Assert.NotEmpty(h.Receipts);
    }

    [Fact]
    public void The_receipt_shape_matches_the_httpRequest_action_receipt()
    {
        // Same schema string and the same field names ActionExecutor.WriteOutboundDenialReceiptAsync
        // emits — one outbound-denial vocabulary, two producers.
        var json = new OutboundReceipt("a", "githubRequest", "https://h/x", "h", "why").ToJson();
        using var doc = JsonDocument.Parse(json);
        foreach (var field in new[] { "schema", "agent", "action", "target", "host", "rule", "reason", "enforcementMode" })
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"receipt is missing '{field}'");
    }
}

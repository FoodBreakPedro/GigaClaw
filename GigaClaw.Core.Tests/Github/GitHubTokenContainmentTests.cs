using System.Net;
using System.Text.Json;
using GigaClaw.Core.Github;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// C7 acceptance criterion: "All tokens stored in settings, never in ticket content."
/// <para>
/// The token is the one value in this feature that can turn a convenience into an incident, and
/// the board is the wrong place for it in every direction: ticket bodies are rendered, exported,
/// injected into dispatch prompts, and writable by agents. These tests assert the negative — that
/// the PAT reaches the <c>Authorization</c> header and nothing else the board can see.
/// </para>
/// </summary>
public class GitHubTokenContainmentTests
{
    private const string IssuesPath = "/repos/acme/widgets/issues";
    private const string Token = GitHubTestHarness.Token;

    private static GitHubApiScript Script() => new GitHubApiScript()
        .Get(IssuesPath, HttpStatusCode.OK, """
            [{"number": 11, "title": "Crash on export", "body": "Steps to reproduce...",
              "state": "open", "html_url": "https://github.test/acme/widgets/issues/11",
              "updated_at": "2026-07-01T10:00:00Z"}]
            """)
        .Post($"{IssuesPath}/11/comments", HttpStatusCode.Created, """{"id":1}""")
        .Patch($"{IssuesPath}/11", HttpStatusCode.OK, """{"state":"closed"}""");

    [Fact]
    public async Task The_token_never_appears_in_any_created_ticket_content()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config(commentOnDone: true, closeOnDone: true));

        await h.Sync.SyncAsync(h.Slug);
        var ticket = Assert.Single(await h.Tickets.ListTicketsAsync(h.Slug));
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Done", "owner");
        await h.Sync.SyncAsync(h.Slug);

        var text = await h.TicketTextAsync();
        Assert.NotEmpty(text);
        Assert.DoesNotContain(text, t => t.Contains(Token, StringComparison.Ordinal));
        // Not just the literal: nothing PAT-shaped got copied in either.
        Assert.DoesNotContain(text, t => t.Contains("ghp_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_token_never_appears_in_a_policy_denial_receipt()
    {
        // No owner approval — every call is refused, so every call produces a receipt.
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.ConfigureGitHub(GitHubTestHarness.Config());

        await h.Sync.SyncAsync(h.Slug);

        var receipt = Assert.Single(h.Receipts).Receipt;
        var json = receipt.ToJson();
        Assert.DoesNotContain(Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_is_never_placed_in_a_request_url()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());

        await h.Sync.SyncAsync(h.Slug);

        Assert.NotEmpty(h.Handler.Requests);
        foreach (var (request, _) in h.Handler.Requests)
            Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_does_reach_the_authorization_header()
    {
        // The complement of the tests above: containment must not be achieved by never sending it.
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());

        await h.Sync.SyncAsync(h.Slug);

        var authorization = h.Handler.LastRequest.Headers.Authorization;
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal(Token, authorization.Parameter);
    }

    [Fact]
    public void The_config_dto_carries_no_token_property()
    {
        // GitHubProjectConfig is what the API returns. A token member here would leak on every GET.
        var names = typeof(GitHubProjectConfig).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_transport_error_is_redacted_before_it_can_reach_a_ticket()
    {
        var handler = Helpers.FakeHttpMessageHandler.Throw(
            new HttpRequestException($"connect failed while sending Authorization: Bearer {Token}"));
        using var h = await GitHubTestHarness.BuildAsync(handler);
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());

        var result = await h.Sync.SyncAsync(h.Slug);

        Assert.False(result.Ran);
        Assert.DoesNotContain(Token, result.Reason!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_token_is_readable_only_through_the_server_side_accessor()
    {
        using var tmp = new Helpers.TempDir();
        var settings = new Core.Services.AppSettingsService(tmp.Path);
        settings.ConfigureGitHub("proj", GitHubTestHarness.Config(), Token);

        Assert.Equal(Token, settings.GetGitHubToken("proj"));
        Assert.True(settings.GitHubTokenConfigured("proj"));
        // The config the API hands back knows a token exists; it does not know what it is.
        var config = settings.GetGitHubConfig("proj");
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(config), StringComparison.Ordinal);
    }
}

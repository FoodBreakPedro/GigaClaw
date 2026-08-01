using System.Net;
using System.Text.Json;
using GigaClaw.Core.Github;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// U6's new leg of the C7 surface: <see cref="GitHubPullRequestService"/> pushes a ticket's R5
/// worktree branch and opens its pull request. These are the capability's own containment and
/// policy tests — the end-to-end composition is <c>Integration/U6EndToEndTests</c>.
/// <para>
/// Everything is real except the HTTP transport and the "remote": the push is a real
/// <c>git push</c> into a real local bare repository, so "the branch reached the remote" is a fact
/// about git rather than about a mock.
/// </para>
/// </summary>
public class GitHubPullRequestTests
{
    private const string PullsPath = "/repos/acme/widgets/pulls";
    private const string Token = GitHubTestHarness.Token;

    private static GitHubApiScript Script(string? existing = null) => new GitHubApiScript()
        .Get($"{PullsPath}?head=", HttpStatusCode.OK, existing ?? "[]")
        .Post(PullsPath, HttpStatusCode.Created, """
            {"number": 42, "html_url": "https://github.test/acme/widgets/pull/42"}
            """);

    private static async Task RunGitAsync(string cwd, string args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));
        Assert.True(result.Success, $"git {args} failed in {cwd}: {result.Stderr}\n{result.Stdout}");
    }

    /// <summary>A workspace repo with one commit, a bare "remote", and a ticket branch to push.</summary>
    private static async Task<(int TicketId, string Branch, string Bare)> SeedAsync(
        GitHubTestHarness h, string remoteUrl = "", string remoteName = "origin")
    {
        await RunGitAsync(h.Workspace, "init -q");
        await RunGitAsync(h.Workspace, "config user.email test@example.com");
        await RunGitAsync(h.Workspace, "config user.name \"GigaClaw Test\"");
        // Paid-for lesson (commit 4082184): Windows' git ships core.autocrlf=true and silently
        // rewrites blobs on checkout, corrupting the bytes these assertions compare.
        await RunGitAsync(h.Workspace, "config core.autocrlf false");
        await RunGitAsync(h.Workspace, "config commit.gpgsign false");
        await File.WriteAllTextAsync(Path.Combine(h.Workspace, "README.md"), "hello\n");
        await RunGitAsync(h.Workspace, "add -A");
        await RunGitAsync(h.Workspace, "commit -q -m initial");

        var bare = Path.Combine(h.Tmp.Path, "remote.git");
        await RunGitAsync(h.Tmp.Path, $"init -q --bare \"{bare}\"");
        await RunGitAsync(h.Workspace, $"remote add {remoteName} \"{(remoteUrl.Length == 0 ? bare : remoteUrl)}\"");

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Rework the exporter", status: "Review");
        var branch = $"ticket/{ticket.Id}";
        await RunGitAsync(h.Workspace, $"branch {branch}");
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, branch, h.Workspace, "active");
        return (ticket.Id, branch, bare);
    }

    private static async Task<GitHubTestHarness> ReadyAsync(GitHubApiScript script)
    {
        var harness = await GitHubTestHarness.BuildAsync(script.Build());
        harness.OwnerApproves(GitHubTestHarness.ApiHost);
        harness.ConfigureGitHub(GitHubTestHarness.Config());
        return harness;
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_branch_reaches_the_remote_and_the_created_pull_request_carries_the_ticket()
    {
        using var h = await ReadyAsync(Script());
        var (ticketId, branch, bare) = await SeedAsync(h);

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        Assert.True(result.Opened);
        Assert.True(result.Pushed);
        Assert.Equal(42, result.Number);

        // Real git: the bare repository actually holds the branch.
        var remoteRefs = await ProcessRunner.RunAsync("git", "branch --list", bare, TimeSpan.FromSeconds(30));
        Assert.Contains(branch, remoteRefs.Stdout, StringComparison.Ordinal);

        // The request the client actually sent, not a stand-in for it.
        var post = Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        Assert.Equal(PullsPath, post.Request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(branch, body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Contains($"ticket-{ticketId}", body.RootElement.GetProperty("title").GetString()!, StringComparison.Ordinal);
        Assert.Contains($"ticket-{ticketId}", body.RootElement.GetProperty("body").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_twice_finds_the_existing_pull_request_instead_of_creating_a_second()
    {
        using var h = await ReadyAsync(Script());
        var (ticketId, _, _) = await SeedAsync(h);
        Assert.True((await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId)).Opened);

        // What GitHub says now is the whole idempotency mechanism: the second pass sees the PR the
        // first one created, and every service is rebuilt in between so nothing in memory helps.
        var restarted = h.Restart(Script("""
            [{"number": 42, "html_url": "https://github.test/acme/widgets/pull/42"}]
            """).Build());

        var second = await restarted.PullRequests.OpenForTicketAsync(restarted.Slug, ticketId);

        Assert.False(second.Opened);
        Assert.True(second.AlreadyOpen);
        Assert.Equal(42, second.Number);
        Assert.DoesNotContain(restarted.Handler.Requests, r => r.Request.Method == HttpMethod.Post);
        // And exactly one receipt on the ticket, ever.
        var ticket = await restarted.Tickets.GetTicketAsync(restarted.Slug, ticketId);
        Assert.Single(ticket!.Comments, c => c.Content.Contains(GitHubPullRequestService.ReceiptSchema, StringComparison.Ordinal));
    }

    // ── local-first + policy ────────────────────────────────────────────────

    [Fact]
    public async Task An_unconfigured_project_never_pushes_and_never_reaches_the_network()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        var (ticketId, branch, bare) = await SeedAsync(h);

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        Assert.False(result.Published);
        Assert.False(result.Pushed);
        Assert.Empty(h.Handler.Requests);
        var remoteRefs = await ProcessRunner.RunAsync("git", "branch --list", bare, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(branch, remoteRefs.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_that_never_ran_isolated_is_skipped_before_anything_leaves_the_process()
    {
        using var h = await ReadyAsync(Script());
        await SeedAsync(h);
        var plain = await h.Tickets.CreateTicketAsync(h.Slug, "No worktree here", status: "Review");

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, plain.Id);

        Assert.False(result.Published);
        Assert.False(result.Pushed);
        Assert.Contains("worktree", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task An_unapproved_api_host_refuses_the_pull_request_and_receipts_it()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.ConfigureGitHub(GitHubTestHarness.Config());   // configured, but no host approved
        var (ticketId, _, _) = await SeedAsync(h);

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        Assert.True(result.DryRun);
        Assert.Empty(h.Handler.Requests);
        var receipt = Assert.Single(h.Receipts).Receipt;
        Assert.Equal("github-pull-request", receipt.Agent);
        Assert.Equal("githubRequest", receipt.Action);
        Assert.Equal(GitHubTestHarness.ApiHost, receipt.Host);
    }

    [Fact]
    public async Task An_unapproved_push_host_refuses_the_push_and_receipts_it_before_git_runs()
    {
        using var h = await ReadyAsync(Script());
        // The API host is approved; the remote's host is not — so the push is refused on its own
        // merits rather than riding in on the API approval.
        var (ticketId, _, _) = await SeedAsync(h, remoteUrl: "https://git.forge.test/acme/widgets.git");

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        Assert.True(result.DryRun);
        Assert.False(result.Pushed);
        Assert.Empty(h.Handler.Requests);       // the PR was never even looked up
        var receipt = Assert.Single(h.Receipts).Receipt;
        Assert.Equal("gitPush", receipt.Action);
        Assert.Equal("git.forge.test", receipt.Host);
    }

    [Fact]
    public async Task An_approved_push_host_is_allowed_through_the_gate()
    {
        // The complement of the refusal above: containment must not be achieved by refusing
        // everything. A remote whose host the owner approved gets as far as git itself. Port 1 on
        // loopback refuses the connection immediately — no DNS, no credential prompt, no network.
        using var h = await ReadyAsync(Script());
        h.OwnerApproves(GitHubTestHarness.ApiHost, "127.0.0.1");
        var (ticketId, _, _) = await SeedAsync(h, remoteUrl: "https://127.0.0.1:1/acme/widgets.git");

        var result = await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        Assert.False(result.DryRun);            // not a policy refusal…
        Assert.False(result.Pushed);            // …but git could not connect, which is git's answer
        Assert.Empty(h.Receipts);               // and the gate wrote nothing, because it said yes
    }

    // ── token containment ───────────────────────────────────────────────────

    [Fact]
    public async Task The_token_never_appears_in_any_ticket_content_the_pull_request_leg_writes()
    {
        using var h = await ReadyAsync(Script());
        var (ticketId, _, _) = await SeedAsync(h);

        await h.PullRequests.OpenForTicketAsync(h.Slug, ticketId);

        var text = await h.TicketTextAsync();
        Assert.NotEmpty(text);
        Assert.DoesNotContain(text, t => t.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(text, t => t.Contains("ghp_", StringComparison.Ordinal));
        foreach (var (request, _) in h.Handler.Requests)
            Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal(Token, h.Handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void A_remote_url_carrying_credentials_is_stripped_before_it_can_be_receipted()
    {
        // A remote configured as https://x-access-token:<PAT>@host/… is the realistic way a PAT
        // ends up somewhere GigaClaw prints. The receipt target must not be that place.
        var sanitized = GitHubPullRequestService.SanitizeRemote(
            $"https://x-access-token:{Token}@git.forge.test/acme/widgets.git");

        Assert.Equal("https://git.forge.test/acme/widgets.git", sanitized);
        Assert.DoesNotContain(Token, sanitized, StringComparison.Ordinal);
    }

    // ── which remotes count as outbound ─────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "github.com")]
    [InlineData("ssh://git@github.com/acme/widgets.git", "github.com")]
    [InlineData("git@github.com:acme/widgets.git", "github.com")]
    [InlineData("https://x-access-token:secret@git.forge.test/acme/widgets.git", "git.forge.test")]
    public void A_remote_that_names_a_host_is_outbound_and_is_gated(string url, string expected) =>
        Assert.Equal(expected, GitHubPullRequestService.RemoteHost(url));

    [Theory]
    [InlineData("/tmp/remote.git")]
    [InlineData("C:\\repos\\remote.git")]
    [InlineData("../sibling.git")]
    [InlineData("file:///tmp/remote.git")]
    [InlineData("")]
    public void A_remote_that_is_a_filesystem_path_is_not_outbound_traffic(string url) =>
        Assert.Null(GitHubPullRequestService.RemoteHost(url));
}

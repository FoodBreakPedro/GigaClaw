using System.Net;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Github;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Github;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// U6 follow-up (a): the <c>openPullRequest</c> <see cref="ActionSpec"/> is the executor's own arm
/// around <see cref="GitHubPullRequestService"/> — its natural home beside <c>enqueueMerge</c> in a
/// verdict-gate chain. These tests are the executor-level counterpart to
/// <c>GitHubPullRequestTests</c> (which covers the service itself): whether the action fires the
/// service correctly, and — the requirement this suite exists to prove — that an unconfigured
/// project fails <b>closed with a receipt</b> rather than throwing.
/// </summary>
public class ActionExecutorOpenPullRequestTests
{
    private const string PullsPath = "/repos/acme/widgets/pulls";

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

    /// <summary>A workspace repo with one commit, a bare "remote", and a ticket dispatched with an
    /// R5 worktree branch recorded — the precondition <c>OpenForTicketAsync</c> requires.</summary>
    private static async Task<(int TicketId, string Branch, string Bare)> SeedAsync(GitHubTestHarness h)
    {
        await RunGitAsync(h.Workspace, "init -q");
        await RunGitAsync(h.Workspace, "config user.email test@example.com");
        await RunGitAsync(h.Workspace, "config user.name \"GigaClaw Test\"");
        await RunGitAsync(h.Workspace, "config core.autocrlf false");
        await RunGitAsync(h.Workspace, "config commit.gpgsign false");
        await File.WriteAllTextAsync(Path.Combine(h.Workspace, "README.md"), "hello\n");
        await RunGitAsync(h.Workspace, "add -A");
        await RunGitAsync(h.Workspace, "commit -q -m initial");

        var bare = Path.Combine(h.Tmp.Path, "remote.git");
        await RunGitAsync(h.Tmp.Path, $"init -q --bare \"{bare}\"");
        await RunGitAsync(h.Workspace, $"remote add origin \"{bare}\"");

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Rework the exporter", status: "Review");
        var branch = $"ticket/{ticket.Id}";
        await RunGitAsync(h.Workspace, $"branch {branch}");
        await h.Tickets.SetWorktreeStateAsync(h.Slug, ticket.Id, branch, h.Workspace, "active");
        return (ticket.Id, branch, bare);
    }

    private static ActionExecutor BuildExecutor(GitHubTestHarness h, GitHubPullRequestService? pullRequests) => new(
        h.Tickets, h.Members, new LabelService(h.Projects), new SessionRegistry(), new AgentRunRegistry(),
        new ClaudeRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(maxConcurrent: 1), NullLogger<ClaudeRunner>.Instance),
        new CostTracker(), new LocalizationService(h.Settings), h.Projects,
        new RunStateManager(new AgentRunRegistry(), new CostTracker(), h.Tickets, NullLogger.Instance),
        FakeHttpClientFactory.Unused,
        new TeamRunService(new TeamStore(h.Projects, h.Tickets), h.Tickets, h.Members, new AgentTeamService(), NullLogger<TeamRunService>.Instance),
        NullLogger.Instance,
        outboundGate: null,
        leases: null,
        mergeQueue: null,
        mergeApproval: null,
        pullRequests: pullRequests);

    private static AutomationRule Chain(params ActionSpec[] actions) => new()
    {
        Id = "open-pr-probe",
        Trigger = new StatusChangeTriggerSpec { To = "Review" },
        Actions = actions.ToList(),
    };

    // ── happy path: the action fires the service ────────────────────────────

    [Fact]
    public async Task Configured_project_pushes_the_branch_and_opens_the_pull_request()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());
        var (ticketId, branch, bare) = await SeedAsync(h);
        var executor = BuildExecutor(h, h.PullRequests);

        await executor.ExecuteAutomationAsync(
            new ProjectRuntime(h.Slug) { Workspace = h.Workspace, Config = new AutomationConfig() },
            Chain(new OpenPullRequestActionSpec()),
            new TriggerFiring(ticketId, "Rework the exporter", "Review"),
            CancellationToken.None);

        var remoteRefs = await ProcessRunner.RunAsync("git", "branch --list", bare, TimeSpan.FromSeconds(30));
        Assert.Contains(branch, remoteRefs.Stdout, StringComparison.Ordinal);
        Assert.Single(h.Handler.Requests, r => r.Request.Method == HttpMethod.Post);

        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        Assert.Contains(ticket!.Comments, c => c.Content.Contains(GitHubPullRequestService.ReceiptSchema, StringComparison.Ordinal));
    }

    // ── fail closed, not thrown ──────────────────────────────────────────────

    /// <summary>
    /// The requirement U6-EVIDENCE.md's follow-up (b) names explicitly: a project with no GitHub
    /// configuration must not throw — <see cref="GitHubPullRequestService.OpenForTicketAsync"/>
    /// already returns rather than throwing, and the action's own job is to make that outcome
    /// visible on the ticket instead of letting it vanish silently.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_project_fails_closed_with_a_note_instead_of_throwing()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        // Deliberately no ConfigureGitHub call — this project never opted in.
        var (ticketId, branch, bare) = await SeedAsync(h);
        var executor = BuildExecutor(h, h.PullRequests);

        var exception = await Record.ExceptionAsync(() => executor.ExecuteAutomationAsync(
            new ProjectRuntime(h.Slug) { Workspace = h.Workspace, Config = new AutomationConfig() },
            Chain(new OpenPullRequestActionSpec()),
            new TriggerFiring(ticketId, "Rework the exporter", "Review"),
            CancellationToken.None));

        Assert.Null(exception);
        Assert.Empty(h.Handler.Requests);
        var remoteRefs = await ProcessRunner.RunAsync("git", "branch --list", bare, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(branch, remoteRefs.Stdout, StringComparison.Ordinal);

        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        Assert.Contains(ticket!.Activities, a => a.Text.Contains("Pull request not opened", StringComparison.Ordinal));
    }

    /// <summary>Unwired (no host ever registered <see cref="GitHubPullRequestService"/>, mirroring
    /// how <c>_mergeQueue</c> null means <c>enqueueMerge</c> is a logged no-op): the action must not
    /// crash the chain just because the capability was never composed in.</summary>
    [Fact]
    public async Task An_executor_with_no_pull_request_service_wired_is_a_logged_no_op()
    {
        using var h = await GitHubTestHarness.BuildAsync(Script().Build());
        h.OwnerApproves(GitHubTestHarness.ApiHost);
        h.ConfigureGitHub(GitHubTestHarness.Config());
        var (ticketId, branch, bare) = await SeedAsync(h);
        var executor = BuildExecutor(h, pullRequests: null);

        var exception = await Record.ExceptionAsync(() => executor.ExecuteAutomationAsync(
            new ProjectRuntime(h.Slug) { Workspace = h.Workspace, Config = new AutomationConfig() },
            Chain(new OpenPullRequestActionSpec()),
            new TriggerFiring(ticketId, "Rework the exporter", "Review"),
            CancellationToken.None));

        Assert.Null(exception);
        Assert.Empty(h.Handler.Requests);
        var remoteRefs = await ProcessRunner.RunAsync("git", "branch --list", bare, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(branch, remoteRefs.Stdout, StringComparison.Ordinal);
    }
}

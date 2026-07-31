using System.Net;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Github;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// C7 part 2: "PR comment from owner re-dispatches the assignee with the comment as steering input."
/// <para>
/// The re-dispatch mechanism under test is C3's, not a new one: the trigger writes the comment onto
/// the ticket as <c>github-owner-feedback/v1</c>, and <c>ActionExecutor.ComposeDispatchContextAsync</c>
/// renders it into the prompt beside the repair brief. These tests cover both halves — the trigger
/// firing and filtering, and the injection — plus the authority rule that keeps a repo-writing agent
/// from steering itself.
/// </para>
/// </summary>
public class GitHubPrFeedbackTests
{
    private const string CommentsPath = "/repos/acme/widgets/pulls/comments";
    private const string PullPath = "/repos/acme/widgets/pulls/7";

    private static string CommentsJson(params (long Id, string Author, string Body)[] comments) =>
        "[" + string.Join(",", comments.Select(c => $$"""
            {
              "id": {{c.Id}},
              "user": {"login": "{{c.Author}}"},
              "body": {{System.Text.Json.JsonSerializer.Serialize(c.Body)}},
              "html_url": "https://github.test/acme/widgets/pull/7#discussion_r{{c.Id}}",
              "pull_request_url": "https://api.github.test/repos/acme/widgets/pulls/7",
              "created_at": "2026-07-05T09:00:00Z"
            }
            """)) + "]";

    private static GitHubApiScript Script(string commentsJson, string? pullJson = null)
    {
        var script = new GitHubApiScript().Get(CommentsPath, HttpStatusCode.OK, commentsJson);
        if (pullJson is not null) script.Get(PullPath, HttpStatusCode.OK, pullJson);
        return script;
    }

    private static async Task<(GitHubTestHarness Harness, int TicketId)> ReadyAsync(
        GitHubApiScript script, IReadOnlyList<string>? owners = null)
    {
        var harness = await GitHubTestHarness.BuildAsync(script.Build());
        harness.OwnerApproves(GitHubTestHarness.ApiHost);
        harness.ConfigureGitHub(GitHubTestHarness.Config(ownerLogins: owners ?? ["octocat"]));
        var member = await harness.Members.CreateMemberAsync(harness.Slug, "programmer");
        var ticket = await harness.Tickets.CreateTicketAsync(
            harness.Slug, "Rework the exporter", status: "Doing", assignedTo: member.Slug);
        return (harness, ticket.Id);
    }

    private static TriggerContext Context(GitHubTestHarness h, AutomationRule automation) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Workspace,
        Automation = automation,
        Tickets = h.Tickets,
        Members = h.Members,
        Sessions = new Core.Automation.SessionRegistry(),
        Runs = new AgentRunRegistry(),
        Now = DateTime.UtcNow,
    };

    private static GitHubPrCommentTrigger Trigger(GitHubTestHarness h, GitHubPrCommentTriggerSpec? spec = null) =>
        new(spec ?? new GitHubPrCommentTriggerSpec { PollSeconds = 0 },
            new GitHubTriggerServices(h.Client, h.Settings, h.Links));

    private static AutomationRule Rule(string id = "pr-feedback") => new()
    {
        Id = id,
        Trigger = new GitHubPrCommentTriggerSpec { PollSeconds = 0 },
    };

    // ── Firing ──────────────────────────────────────────────────────────────

    /// <summary>The first ticket of a fresh project. Fixtures reference it as <c>ticket-1</c>.</summary>
    private const int PlaceholderId = 1;

    [Fact]
    public async Task An_owner_comment_naming_a_ticket_fires_for_that_ticket()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", $"ticket-{PlaceholderId} — this drops the header row."))));
        using var harness = h;
        Assert.Equal(PlaceholderId, ticketId);

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        var firing = Assert.Single(firings);
        Assert.Equal(ticketId, firing.TicketId);
        Assert.Equal("Doing", firing.TicketStatus);
    }

    [Fact]
    public async Task The_comment_is_recorded_on_the_ticket_before_anything_can_fail()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", $"ticket-{PlaceholderId} the header row is dropped"))));
        using var harness = h;

        await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        var ticket = await harness.Tickets.GetTicketAsync(harness.Slug, ticketId);
        var record = Assert.Single(ticket!.Comments);
        Assert.True(OwnerFeedback.TryRead(record.Content, out var item));
        Assert.Equal("octocat", item!.Author);
        Assert.Equal(7, item.PullRequestNumber);
        Assert.Contains("the header row is dropped", item.Body);
    }

    [Fact]
    public async Task A_comment_from_anyone_else_fires_nothing()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "drive-by", $"ticket-{PlaceholderId} rewrite everything"))));
        using var harness = h;

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        Assert.Empty(firings);
        var ticket = await harness.Tickets.GetTicketAsync(harness.Slug, ticketId);
        Assert.Empty(ticket!.Comments);
    }

    [Fact]
    public async Task With_no_configured_owner_login_nothing_can_steer_an_agent()
    {
        // Fail closed: an empty owner list is not "anyone", it is "no one".
        var (h, _) = await ReadyAsync(
            Script(CommentsJson((501, "octocat", $"ticket-{PlaceholderId} fix it"))), owners: []);
        using var harness = h;

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        Assert.Empty(firings);
    }

    [Fact]
    public void An_automation_can_narrow_the_owner_list_but_never_widen_it()
    {
        // The automation file lives in the workspace and is agent-writable; settings.json is not.
        var config = GitHubTestHarness.Config(ownerLogins: ["octocat"]);
        Assert.Equal(
            ["octocat"],
            GitHubPrCommentTrigger.ResolveOwnerLogins(config, []).Order());
        Assert.Empty(GitHubPrCommentTrigger.ResolveOwnerLogins(config, ["impostor"]));
        Assert.Equal(
            ["octocat"],
            GitHubPrCommentTrigger.ResolveOwnerLogins(config, ["octocat", "impostor"]).Order());
    }

    [Fact]
    public async Task The_same_comment_does_not_fire_twice()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", $"ticket-{PlaceholderId} tighten the validation"))));
        using var harness = h;
        var trigger = Trigger(harness);
        var context = Context(harness, Rule());

        var first = await trigger.EvaluateAsync(context, CancellationToken.None);
        var second = await trigger.EvaluateAsync(context, CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        var ticket = await harness.Tickets.GetTicketAsync(harness.Slug, ticketId);
        Assert.Single(ticket!.Comments);
    }

    [Fact]
    public async Task Several_comments_on_one_pull_request_produce_one_firing_and_several_feedback_records()
    {
        // Three comments must not become three competing runs on the same files.
        var (h, ticketId) = await ReadyAsync(Script(CommentsJson(
            (501, "octocat", $"ticket-{PlaceholderId} the header row is dropped"),
            (502, "octocat", $"ticket-{PlaceholderId} and the totals are off by one"),
            (503, "octocat", $"ticket-{PlaceholderId} rename the flag while you are here"))));
        using var harness = h;

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        Assert.Single(firings);
        var ticket = await harness.Tickets.GetTicketAsync(harness.Slug, ticketId);
        Assert.Equal(3, ticket!.Comments.Count(c => OwnerFeedback.ContainsMarker(c.Content)));
    }

    [Fact]
    public async Task A_comment_with_no_resolvable_ticket_falls_back_to_the_pull_request_then_gives_up()
    {
        var (h, _) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", "please have another look")),
            pullJson: """{"title":"Housekeeping","body":"No references here.","head":{"ref":"chore/tidy"}}"""));
        using var harness = h;

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        // Guessing a ticket would re-dispatch an agent onto work the comment was never about.
        Assert.Empty(firings);
    }

    [Fact]
    public async Task The_pull_requests_branch_name_resolves_the_ticket_when_the_comment_does_not()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", "please have another look")),
            pullJson: """{"title":"Exporter fixes","body":"","head":{"ref":"feature/ticket-1-exporter"}}"""));
        using var harness = h;

        var firing = Assert.Single(
            await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None));

        Assert.Equal(ticketId, firing.TicketId);
    }

    [Fact]
    public async Task A_pull_request_closing_an_imported_issue_resolves_through_the_link_table()
    {
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", "one more thing")),
            pullJson: """{"title":"Exporter fixes","body":"Closes #42","head":{"ref":"feature/exporter"}}"""));
        using var harness = h;
        await harness.Links.UpsertAsync(harness.Slug, new GitHubIssueLink(
            "acme/widgets", 42, ticketId, "open", null, DateTime.UtcNow, false));

        var firing = Assert.Single(
            await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None));

        Assert.Equal(ticketId, firing.TicketId);
    }

    [Fact]
    public async Task A_policy_refusal_fires_nothing_and_leaves_a_receipt()
    {
        var (h, _) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", $"ticket-{PlaceholderId} fix it"))));
        using var harness = h;
        harness.OwnerApproves();   // owner revokes the host

        var firings = await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        Assert.Empty(firings);
        Assert.Empty(harness.Handler.Requests);
        Assert.NotEmpty(harness.Receipts);
    }

    // ── Injection (the C3 mechanism, reused) ────────────────────────────────

    [Fact]
    public void Outstanding_feedback_is_everything_since_the_last_handoff()
    {
        var first = OwnerFeedback.RenderComment(Item(1, "fix the header"));
        var second = OwnerFeedback.RenderComment(Item(2, "and the totals"));
        var handoff = "GIGACLAW-HANDOFF v1 programmer ticket-1 run-abc\n\n```json\n{}\n```";
        var third = OwnerFeedback.RenderComment(Item(3, "one more"));

        Assert.Equal(2, OwnerFeedback.Outstanding([first, second]).Count);
        // The agent answered: the episode closes exactly the way a SHIP closes a repair episode.
        Assert.Empty(OwnerFeedback.Outstanding([first, second, handoff]));
        Assert.Single(OwnerFeedback.Outstanding([first, second, handoff, third]));
    }

    [Fact]
    public void The_brief_carries_the_comment_text_the_agent_has_to_act_on()
    {
        var brief = OwnerFeedback.RenderBrief([Item(1, "The header row is dropped on empty input.")], 7);

        Assert.Contains("ticket #7", brief);
        Assert.Contains("The header row is dropped on empty input.", brief);
        Assert.Contains("octocat", brief);
        Assert.Contains("PR #7", brief);
    }

    [Fact]
    public void An_unreadable_feedback_comment_is_skipped_not_half_parsed()
    {
        Assert.Empty(OwnerFeedback.Outstanding([
            "GIGACLAW-GH-FEEDBACK v1 pr-7 comment-1\n\n```json\n{not json}\n```",
            "just an ordinary comment",
        ]));
    }

    [Fact]
    public async Task The_comment_reaches_the_re_dispatch_prompt()
    {
        // The end-to-end criterion: trigger → ticket comment → dispatch context. Same executor
        // path C3's repair brief takes, which is the whole reason no second mechanism was added.
        var (h, ticketId) = await ReadyAsync(Script(
            CommentsJson((501, "octocat", $"ticket-{PlaceholderId} the header row is dropped on empty input"))));
        using var harness = h;
        var executor = BuildExecutor(harness);
        var runtime = new ProjectRuntime(harness.Slug)
        {
            Workspace = harness.Workspace,
            Config = new AutomationConfig(),
        };

        Assert.Equal("do the work", await executor.ComposeDispatchContextAsync(runtime, ticketId, "do the work"));

        await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None);

        var composed = await executor.ComposeDispatchContextAsync(runtime, ticketId, "do the work");
        Assert.Contains("Owner feedback", composed!, StringComparison.Ordinal);
        Assert.Contains("the header row is dropped on empty input", composed, StringComparison.Ordinal);
        Assert.Contains("octocat", composed, StringComparison.Ordinal);
        Assert.EndsWith("do the work", composed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_with_no_feedback_dispatches_exactly_as_before()
    {
        var (h, ticketId) = await ReadyAsync(Script(CommentsJson()));
        using var harness = h;
        var executor = BuildExecutor(harness);
        var runtime = new ProjectRuntime(harness.Slug)
        {
            Workspace = harness.Workspace,
            Config = new AutomationConfig(),
        };

        Assert.Equal("do the work", await executor.ComposeDispatchContextAsync(runtime, ticketId, "do the work"));
    }

    private static ActionExecutor BuildExecutor(GitHubTestHarness h)
    {
        var runs = new AgentRunRegistry();
        var sessions = new Core.Automation.SessionRegistry();
        var cost = new CostTracker();
        return new ActionExecutor(
            h.Tickets, h.Members, new Core.Services.LabelService(h.Projects), sessions, runs,
            new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeRunner>.Instance),
            cost,
            new Core.Services.LocalizationService(h.Settings),
            h.Projects,
            new RunStateManager(runs, cost, h.Tickets, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            Helpers.FakeHttpClientFactory.Unused,
            Helpers.TestTeamRuns.For(h.Projects, h.Tickets),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static OwnerFeedbackItem Item(long id, string body) =>
        new(id, 7, "octocat", body, $"https://github.test/acme/widgets/pull/7#discussion_r{id}",
            new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Utc));
}

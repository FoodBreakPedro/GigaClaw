using System.Net;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Github;

/// <summary>
/// C7 part 3: check-run conclusions as a trigger in the <c>gitCommit</c> family. The family
/// membership is the design claim under test — the trigger resolves its commit from the workspace's
/// own git the way <see cref="GitCommitTrigger"/> does, keeps its seen-state in the same durable
/// store, and fires ticketless when nothing binds the commit to a ticket.
/// </summary>
public class GitHubCheckStatusTests
{
    private const string CheckRunsPath = "/repos/acme/widgets";

    private static string CheckRunsJson(params (long Id, string Name, string Status, string? Conclusion)[] runs) =>
        $$"""
        {"total_count": {{runs.Length}}, "check_runs": [
        """
        + string.Join(",", runs.Select(r => $$"""
            {"id": {{r.Id}}, "name": "{{r.Name}}", "status": "{{r.Status}}",
             "conclusion": {{(r.Conclusion is null ? "null" : $"\"{r.Conclusion}\"")}}}
            """))
        + "]}";

    private static GitHubApiScript Script(string checkRunsJson) =>
        new GitHubApiScript().Get(CheckRunsPath, HttpStatusCode.OK, checkRunsJson);

    /// <summary>A real git repo with one commit, so the trigger's HEAD lookup is the shipped one.</summary>
    private static async Task<string> InitRepoAsync(string workspace, string commitSubject)
    {
        async Task Git(string args) =>
            await ProcessRunner.RunAsync("git", args, workspace, TimeSpan.FromSeconds(30));

        await Git("init -q");
        await Git("config user.email test@example.com");
        await Git("config user.name Test");
        await Git("config commit.gpgsign false");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "hello");
        await Git("add -A");
        var quoted = commitSubject.Replace("\"", "\\\"");
        await Git($"commit -q -m \"{quoted}\"");
        var head = await ProcessRunner.RunAsync("git", "rev-parse HEAD", workspace, TimeSpan.FromSeconds(30));
        return head.Stdout!.Trim();
    }

    private static async Task<GitHubTestHarness> ReadyAsync(GitHubApiScript script, string commitSubject = "Fix the exporter")
    {
        var harness = await GitHubTestHarness.BuildAsync(script.Build());
        harness.OwnerApproves(GitHubTestHarness.ApiHost);
        harness.ConfigureGitHub(GitHubTestHarness.Config());
        await InitRepoAsync(harness.Workspace, commitSubject);
        return harness;
    }

    private static TriggerContext Context(GitHubTestHarness h, AutomationRule automation) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Workspace,
        Automation = automation,
        Tickets = h.Tickets,
        Members = h.Members,
        Sessions = new SessionRegistry(),
        Runs = new AgentRunRegistry(),
        Now = DateTime.UtcNow,
    };

    private static GitHubCheckStatusTrigger Trigger(GitHubTestHarness h, GitHubCheckStatusTriggerSpec? spec = null) =>
        new(spec ?? new GitHubCheckStatusTriggerSpec { PollSeconds = 0 },
            new GitHubTriggerServices(h.Client, h.Settings, h.Links));

    private static AutomationRule Rule(string id = "ci-status") => new()
    {
        Id = id,
        Trigger = new GitHubCheckStatusTriggerSpec { PollSeconds = 0 },
    };

    // ── Firing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_check_on_the_workspace_head_fires()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));

        var firing = Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));

        Assert.Null(firing.TicketId);            // gitCommit-family shape: ticketless by default
        Assert.Contains("build", firing.TicketTitle);
        Assert.Contains("failure", firing.TicketTitle);
    }

    [Fact]
    public async Task The_commit_is_resolved_from_the_workspace_git_the_same_way_gitCommit_does()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        var head = (await ProcessRunner.RunAsync("git", "rev-parse HEAD", h.Workspace, TimeSpan.FromSeconds(30))).Stdout!.Trim();

        await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None);

        Assert.Contains(head, h.Handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.EndsWith("/check-runs", h.Handler.LastRequest.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task A_check_that_has_not_concluded_fires_nothing()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "in_progress", null))));

        Assert.Empty(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_configured_conclusions_fire()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson(
            (900, "build", "completed", "success"),
            (901, "lint", "completed", "failure"))));

        var firing = Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));

        Assert.Contains("lint", firing.TicketTitle);
    }

    [Fact]
    public async Task An_empty_conclusion_list_fires_on_every_concluded_check()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson(
            (900, "build", "completed", "success"),
            (901, "lint", "completed", "failure"),
            (902, "e2e", "queued", null))));
        var spec = new GitHubCheckStatusTriggerSpec { PollSeconds = 0, Conclusions = [] };

        var firings = await Trigger(h, spec).EvaluateAsync(Context(h, Rule()), CancellationToken.None);

        Assert.Equal(2, firings.Count);
    }

    [Fact]
    public async Task A_pinned_ref_is_watched_instead_of_the_workspace_head()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        var spec = new GitHubCheckStatusTriggerSpec { PollSeconds = 0, Ref = "main" };

        await Trigger(h, spec).EvaluateAsync(Context(h, Rule()), CancellationToken.None);

        Assert.Contains("/commits/main/check-runs", h.Handler.LastRequest.RequestUri!.AbsolutePath);
    }

    // ── Dedupe ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_check_result_does_not_fire_twice()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        var trigger = Trigger(h);
        var context = Context(h, Rule());

        Assert.Single(await trigger.EvaluateAsync(context, CancellationToken.None));
        Assert.Empty(await trigger.EvaluateAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task The_seen_state_survives_a_restart()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));

        // A brand-new trigger instance with a brand-new SessionRegistry: the only thing carrying
        // the memory across is the workspace's dispatch-state.json, which is the point.
        var restarted = Trigger(h);
        Assert.Empty(await restarted.EvaluateAsync(Context(h, Rule()), CancellationToken.None));
    }

    [Fact]
    public async Task A_rerun_that_flips_the_conclusion_is_a_new_event()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        var spec = new GitHubCheckStatusTriggerSpec { PollSeconds = 0, Conclusions = ["failure", "success"] };
        Assert.Single(await Trigger(h, spec).EvaluateAsync(Context(h, Rule()), CancellationToken.None));

        var green = h.Restart(Script(CheckRunsJson((900, "build", "completed", "success"))).Build());
        Assert.Single(await Trigger(green, spec).EvaluateAsync(Context(green, Rule()), CancellationToken.None));
    }

    [Fact]
    public async Task Two_automations_do_not_swallow_each_others_events()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));

        Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule("ci-a")), CancellationToken.None));
        Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule("ci-b")), CancellationToken.None));
    }

    // ── Ticket binding ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_commit_message_naming_a_ticket_binds_the_firing_to_it()
    {
        using var h = await ReadyAsync(
            Script(CheckRunsJson((900, "build", "completed", "failure"))),
            commitSubject: "fix(exporter): drop the empty header row (ticket-1)");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Rework the exporter", status: "Doing");

        var firing = Assert.Single(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));

        Assert.Equal(ticket.Id, firing.TicketId);
        Assert.Equal("Doing", firing.TicketStatus);
    }

    // ── Policy + local-first ────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_refusal_fires_nothing_and_leaves_a_receipt()
    {
        using var h = await ReadyAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))));
        h.OwnerApproves();   // owner revokes the host

        Assert.Empty(await Trigger(h).EvaluateAsync(Context(h, Rule()), CancellationToken.None));
        Assert.Empty(h.Handler.Requests);
        var receipt = Assert.Single(h.Receipts).Receipt;
        Assert.Equal("github-check-status", receipt.Agent);
        Assert.Equal("githubRequest", receipt.Action);
    }

    [Fact]
    public async Task An_unconfigured_project_never_reaches_the_network()
    {
        var h = await GitHubTestHarness.BuildAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))).Build());
        using var harness = h;
        await InitRepoAsync(harness.Workspace, "Fix the exporter");

        Assert.Empty(await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task A_workspace_without_a_git_repo_fires_nothing()
    {
        var h = await GitHubTestHarness.BuildAsync(Script(CheckRunsJson((900, "build", "completed", "failure"))).Build());
        using var harness = h;
        harness.OwnerApproves(GitHubTestHarness.ApiHost);
        harness.ConfigureGitHub(GitHubTestHarness.Config());
        // No git init: there is no HEAD to ask GitHub about.

        Assert.Empty(await Trigger(harness).EvaluateAsync(Context(harness, Rule()), CancellationToken.None));
        Assert.Empty(harness.Handler.Requests);
    }

    // ── Vocabulary registration ─────────────────────────────────────────────

    [Fact]
    public void Both_github_triggers_round_trip_through_the_automation_config_vocabulary()
    {
        var json = """
            {
              "automations": [
                { "id": "ci", "trigger": { "type": "githubCheckStatus", "pollSeconds": 90, "conclusions": ["failure"], "ref": "main" }, "actions": [] },
                { "id": "fb", "trigger": { "type": "githubPrComment", "pollSeconds": 60, "ownerLogins": ["octocat"] }, "actions": [] }
              ]
            }
            """;

        var config = System.Text.Json.JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;

        var ci = Assert.IsType<GitHubCheckStatusTriggerSpec>(config.Automations[0].Trigger);
        Assert.Equal(90, ci.PollSeconds);
        Assert.Equal(["failure"], ci.Conclusions);
        Assert.Equal("main", ci.Ref);
        Assert.Equal("githubCheckStatus", ci.UiTypeKey);

        var feedback = Assert.IsType<GitHubPrCommentTriggerSpec>(config.Automations[1].Trigger);
        Assert.Equal(["octocat"], feedback.OwnerLogins);
        Assert.Equal("githubPrComment", feedback.UiTypeKey);

        // Round-trips back out under the same discriminators.
        var rewritten = System.Text.Json.JsonSerializer.Serialize(config, AutomationStore.JsonOptions);
        Assert.Contains("githubCheckStatus", rewritten);
        Assert.Contains("githubPrComment", rewritten);
    }
}

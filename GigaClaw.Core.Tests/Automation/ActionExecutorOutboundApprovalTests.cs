using System.Net;
using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R3's shared-boundary criterion: the host-side <c>httpRequest</c> action is governed by
/// <see cref="OutboundApprovalGate"/> anchored on the OWNER's app-level settings.json — the file
/// that sits outside every workspace and therefore outside every agent's write globs. Unlike
/// <see cref="ActionExecutorHttpRequestTests"/> (which approves the fixture host to test HTTP
/// mechanics), this suite wires the executor exactly the way <see cref="AutomationEngine"/> does
/// in production: gate → <see cref="AppSettingsService.GetApprovedOutboundHosts"/> → settings.json,
/// re-read per execution.
/// </summary>
public class ActionExecutorOutboundApprovalTests
{
    // ── Harness (same idioms as ActionExecutorHttpRequestTests, but production gate wiring) ──

    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required ProjectService Projects { get; init; }
        public required TicketService Tickets { get; init; }
        public required LabelService Labels { get; init; }
        public required MemberService Members { get; init; }
        public required SessionRegistry Sessions { get; init; }
        public required AgentRunRegistry Runs { get; init; }
        public required ActionExecutor Executor { get; init; }
        public required ProjectRuntime Runtime { get; init; }
        public required FakeHttpMessageHandler Handler { get; init; }
        public required int TicketId { get; init; }
        public required string Slug { get; init; }

        /// <summary>The trusted owner action: writing the approved-host list into the app-level
        /// settings.json. Nothing in any workspace can produce this file.</summary>
        public void OwnerApproves(params string[] hosts) =>
            File.WriteAllText(
                Path.Combine(Tmp.Path, "settings.json"),
                JsonSerializer.Serialize(new { ApprovedOutboundHosts = hosts }));

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task<Harness> BuildAsync(FakeHttpMessageHandler handler, string description = "")
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("outbound-approval-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var appSettings = new AppSettingsService(tmp.Path);

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost,
            new LocalizationService(appSettings), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            new FakeHttpClientFactory(handler),
            TestTeamRuns.For(projects, tickets),
            NullLogger.Instance,
            // Production wiring, verbatim: the anchor is the owner settings file, read per call.
            new OutboundApprovalGate(appSettings.GetApprovedOutboundHosts));

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Publish the launch post", description: description, status: "Review");

        return new Harness
        {
            Tmp = tmp,
            Projects = projects,
            Tickets = tickets,
            Labels = labels,
            Members = members,
            Sessions = sessions,
            Runs = runs,
            Executor = executor,
            Handler = handler,
            TicketId = ticket.Id,
            Slug = project.Slug,
            Runtime = new ProjectRuntime(project.Slug)
            {
                Workspace = workspace,
                Config = new AutomationConfig { Automations = [] },
            },
        };
    }

    private sealed class CompletionTrigger : ITrigger
    {
        public readonly TaskCompletionSource<bool> Completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

        public Task CompleteFiringAsync(TriggerContext ctx, TriggerFiring firing, bool succeeded, DateTime? completedAt = null)
        {
            Completed.TrySetResult(succeeded);
            return Task.CompletedTask;
        }
    }

    private static TriggerContext BuildContext(Harness h, AutomationRule automation) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Runtime.Workspace!,
        Automation = automation,
        Tickets = h.Tickets,
        Members = h.Members,
        Sessions = h.Sessions,
        Runs = h.Runs,
        Now = DateTime.UtcNow,
    };

    private static AutomationRule Chain(string id, params ActionSpec[] actions) => new()
    {
        Id = id,
        Trigger = new StatusChangeTriggerSpec { To = "Review" },
        Actions = actions.ToList(),
    };

    private static async Task RunToCompletionAsync(Harness h, AutomationRule automation, string firedStatus = "Review")
    {
        var trigger = new CompletionTrigger();
        var firing = new TriggerFiring(h.TicketId, "Publish the launch post", firedStatus);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation, firing, CancellationToken.None, trigger, BuildContext(h, automation));

        var finished = await Task.WhenAny(trigger.Completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == trigger.Completed.Task, "Action chain did not finalize within 10s");
    }

    private static async Task DispatchAsync(Harness h, AutomationRule automation)
    {
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation,
            new TriggerFiring(h.TicketId, "Publish the launch post", "Review"),
            CancellationToken.None);
    }

    private static async Task<List<string>> CommentsAsync(Harness h)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    /// <summary>Polls for a comment matching <paramref name="predicate"/> — denial receipts are
    /// written inside the detached chain, so they land asynchronously like FailureComments do.</summary>
    private static async Task<string> WaitForCommentAsync(Harness h, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var match = (await CommentsAsync(h)).FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(20);
        }
        Assert.Fail("Expected comment did not appear within 10s");
        return null!; // unreachable
    }

    private static HttpRequestActionSpec Post(string url, bool abortOnFailure = false) =>
        new() { Url = url, Method = "POST", AbortOnFailure = abortOnFailure, TimeoutSeconds = 5 };

    // ── Dry-run without trusted approval ────────────────────────────────────

    [Fact]
    public async Task Without_trusted_approval_httpRequest_is_dry_run_and_nothing_is_sent()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);
        // No OwnerApproves call: a fresh install has no approved hosts — deny by default.

        await DispatchAsync(h, Chain("outbound-probe", Post("https://cms.example/api/publish")));

        await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Denial_receipt_names_agent_action_target_and_rule()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        await DispatchAsync(h, Chain("outbound-probe", Post("https://cms.example/api/publish")));

        var receipt = await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("outbound-denial/v1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("outbound-probe", doc.RootElement.GetProperty("agent").GetString());
        Assert.Equal("httpRequest", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("https://cms.example/api/publish", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal("outbound-approval", doc.RootElement.GetProperty("rule").GetString());
        // The reason names the host, so the owner knows exactly what to approve.
        Assert.Contains("cms.example", doc.RootElement.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task Dry_run_honors_abortOnFailure_so_downstream_actions_do_not_run()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        await DispatchAsync(h, Chain("outbound-probe",
            Post("https://cms.example/api/publish", abortOnFailure: true),
            new AddCommentActionSpec { Content = "downstream ran", Author = "automation" }));

        await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        await Task.Delay(300); // give a (wrongly) continuing chain time to add its comment
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(await CommentsAsync(h), c => c.Contains("downstream ran"));
    }

    [Fact]
    public async Task Dry_run_publishes_chain_values_and_continues_when_abortOnFailure_is_not_set()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h, Chain("outbound-probe",
            Post("https://cms.example/api/publish", abortOnFailure: false),
            new AddCommentActionSpec { Content = "status={http.status} dryRun={http.dryRun}", Author = "automation" }));

        Assert.Empty(handler.Requests);
        Assert.Contains(await CommentsAsync(h), c => c == "status=0 dryRun=true");
    }

    // ── Sending with trusted approval + hot reload ──────────────────────────

    [Fact]
    public async Task With_trusted_approval_in_owner_settings_the_request_sends()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, """{"id":1}""");
        using var h = await BuildAsync(handler);
        h.OwnerApproves("cms.example");

        await RunToCompletionAsync(h, Chain("outbound-probe", Post("https://cms.example/api/publish")));

        Assert.Single(handler.Requests);
        Assert.Equal("https://cms.example/api/publish", handler.LastRequest.RequestUri!.ToString());
        Assert.DoesNotContain(await CommentsAsync(h), c => c.Contains("outbound-denial/v1"));
    }

    [Fact]
    public async Task Approval_is_read_per_execution_so_an_owner_edit_takes_effect_without_restart()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        // Same executor instance throughout — no engine restart between the two firings.
        await DispatchAsync(h, Chain("outbound-probe", Post("https://cms.example/api/publish")));
        await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        Assert.Empty(handler.Requests);

        h.OwnerApproves("cms.example");

        await RunToCompletionAsync(h, Chain("outbound-probe", Post("https://cms.example/api/publish")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Approval_for_one_host_does_not_open_another()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);
        h.OwnerApproves("cms.example");

        await DispatchAsync(h, Chain("outbound-probe", Post("https://other.example/api/exfiltrate")));

        await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        Assert.Empty(handler.Requests);
    }

    // ── CMS dispatch regression: the real shipped automation, labels aligned ─

    private const string WriterDraft = """
        ---
        title: How to Ship Faster
        slug: how-to-ship-faster
        excerpt: A short teaser.
        contentType: article
        seo:
          title: How to Ship Faster | ZabalaZone
          description: Practical tips for shipping faster.
          primaryKeyword: ship faster
        imagePrompt: a rocket launching from a laptop
        ---
        # How to Ship Faster

        Body content with **markdown**.
        """;

    private static AutomationRule LoadRealCmsDispatchAutomation()
    {
        var json = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "ProjectTemplate", "Agents", "automations.json"));
        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)
            ?? throw new InvalidDataException("Template automations.json deserialized to null.");
        return Assert.Single(config.Automations, a => a.Id == "cms-dispatch-on-done");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GigaClaw repository root.");
    }

    private async Task<Harness> BuildCmsDispatchHarnessAsync(FakeHttpMessageHandler handler)
    {
        var h = await BuildAsync(handler, description: WriterDraft);

        // "Labels align": ready-for-cms + approved present, dispatched absent — exactly the
        // condition set the shipped automation declares.
        var ready = await h.Labels.CreateLabelAsync(h.Slug, "ready-for-cms", "#00aa00");
        var approved = await h.Labels.CreateLabelAsync(h.Slug, "approved", "#0000aa");
        await h.Tickets.SetTicketLabelsAsync(h.Slug, h.TicketId, [ready.Id, approved.Id]);
        return h;
    }

    [Fact]
    public async Task Real_cms_dispatch_automation_still_sends_end_to_end_when_labels_align_and_host_is_approved()
    {
        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            """{"id":9,"slug":"how-to-ship-faster","adminUrl":"https://zz.example/admin/9"}""");
        using var h = await BuildCmsDispatchHarnessAsync(handler);
        h.OwnerApproves("zabalazone.example"); // the host the shipped automation targets

        var automation = LoadRealCmsDispatchAutomation();
        var firing = new TriggerFiring(h.TicketId, "Publish the launch post", "Done");

        Assert.True(await h.Executor.ConditionsMatchAsync(h.Runtime, automation, firing),
            "cms-dispatch-on-done conditions must match when labels align");

        await RunToCompletionAsync(h, automation, firedStatus: "Done");

        Assert.Single(h.Handler.Requests);
        Assert.Equal("https://zabalazone.example/api/ai/draft", handler.LastRequest.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("How to Ship Faster", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(h.Slug, doc.RootElement.GetProperty("venture").GetString());
        Assert.DoesNotContain(await CommentsAsync(h), c => c.Contains("outbound-denial/v1"));
        // The success receipt the automation itself writes still lands.
        Assert.Contains(await CommentsAsync(h), c => c.StartsWith("Dispatched to CMS:"));
    }

    [Fact]
    public async Task Real_cms_dispatch_automation_is_dry_run_when_the_owner_has_not_approved_the_host()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildCmsDispatchHarnessAsync(handler);
        // Labels align, but no owner approval — the agent-reachable half is not authorization.

        await DispatchAsync(h, LoadRealCmsDispatchAutomation());

        var receipt = await WaitForCommentAsync(h, c => c.Contains("outbound-denial/v1"));
        Assert.Empty(handler.Requests);
        using var doc = JsonDocument.Parse(receipt);
        Assert.Equal("zabalazone.example", doc.RootElement.GetProperty("host").GetString());
        // abortOnFailure=true in the shipped spec: the dispatched label must NOT be applied.
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
        Assert.DoesNotContain(ticket!.Labels, l => l.Name == "dispatched");
    }
}

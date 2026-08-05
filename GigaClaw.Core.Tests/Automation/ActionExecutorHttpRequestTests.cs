using System.Net;
using System.Net.Http;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Covers the httpRequest action and the chain-value capture that makes its response usable by
/// later actions (feature AD-2: an addComment writing the CMS receipt back onto the ticket).
/// <para>
/// httpRequest blocks on network I/O, so the executor detaches the rest of the chain onto a
/// background task and <c>ExecuteAutomationAsync</c> returns immediately. Tests therefore
/// synchronize on an observable produced by the chain itself: <see cref="CompletionTrigger"/>
/// (the chain's terminal finalize) for success paths, and the request log of the fake handler
/// for abort paths — where the chain returns early and never finalizes.
/// </para>
/// </summary>
public class ActionExecutorHttpRequestTests
{
    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required ProjectService Projects { get; init; }
        public required TicketService Tickets { get; init; }
        public required MemberService Members { get; init; }
        public required SessionRegistry Sessions { get; init; }
        public required AgentRunRegistry Runs { get; init; }
        public required ActionExecutor Executor { get; init; }
        public required ProjectRuntime Runtime { get; init; }
        public required FakeHttpMessageHandler Handler { get; init; }
        public required int TicketId { get; init; }
        public required string Slug { get; init; }

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task<Harness> BuildAsync(FakeHttpMessageHandler handler, string description = "")
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("http-request-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var loc = new LocalizationService(new AppSettingsService(tmp.Path));

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            new FakeHttpClientFactory(handler),
            TestTeamRuns.For(projects, tickets),
            NullLogger.Instance,
            // These tests cover httpRequest mechanics, not outbound approval — approve the
            // fixture host so the R3 preflight stays out of the way. The approval boundary
            // itself is covered by ActionExecutorOutboundApprovalTests.
            new GigaClaw.Core.Automation.Policy.OutboundApprovalGate(() => ["cms.example"]));

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Publish the launch post", description: description, status: "Review");

        return new Harness
        {
            Tmp = tmp,
            Projects = projects,
            Tickets = tickets,
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

    /// <summary>Captures the chain's terminal finalize so success paths need no polling.</summary>
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

    /// <summary>Runs the chain and waits for its terminal finalize (success paths only).</summary>
    private static async Task RunToCompletionAsync(Harness h, params ActionSpec[] actions)
    {
        var automation = new AutomationRule
        {
            Id = "http-chain",
            Trigger = new StatusChangeTriggerSpec { To = "Review" },
            Actions = actions.ToList(),
        };
        var trigger = new CompletionTrigger();
        var firing = new TriggerFiring(h.TicketId, "Publish the launch post", "Review");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation, firing, CancellationToken.None, trigger, BuildContext(h, automation));

        var finished = await Task.WhenAny(trigger.Completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == trigger.Completed.Task, "Action chain did not finalize within 10s");
    }

    /// <summary>Dispatches the chain without waiting for a finalize — for abort paths.</summary>
    private static async Task DispatchAsync(Harness h, params ActionSpec[] actions)
    {
        var automation = new AutomationRule
        {
            Id = "http-chain",
            Trigger = new StatusChangeTriggerSpec { To = "Review" },
            Actions = actions.ToList(),
        };
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime,
            automation,
            new TriggerFiring(h.TicketId, "Publish the launch post", "Review"),
            CancellationToken.None);
    }

    private static async Task WaitForRequestsAsync(Harness h, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (h.Handler.Requests.Count >= count) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Expected at least {count} HTTP request(s), saw {h.Handler.Requests.Count}");
    }

    private static async Task<List<string>> CommentsAsync(Harness h)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    private static HttpRequestActionSpec Post(string url, bool abortOnFailure = false) =>
        new() { Url = url, Method = "POST", AbortOnFailure = abortOnFailure, TimeoutSeconds = 5 };

    // ── Success: response captured and rendered by a later addComment ────────

    [Fact]
    public async Task Successful_request_captures_status_and_flattened_json_body_for_later_actions()
    {
        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            """{"id":42,"slug":"launch-post","adminUrl":"https://cms.example/admin/42"}""");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("https://cms.example/api/publish"),
            new AddCommentActionSpec
            {
                Content = "Published #{http.body.id} ({http.body.slug}) → {http.body.adminUrl} [status {http.status}]",
                Author = "automation",
            });

        var comment = Assert.Single(await CommentsAsync(h));
        Assert.Equal(
            "Published #42 (launch-post) → https://cms.example/admin/42 [status 200]",
            comment);
    }

    [Fact]
    public async Task Url_and_body_templates_are_rendered_from_the_firing()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/tickets/{ticketId}");
        spec.BodyTemplate = """{"title":"{ticketTitle}","project":"{slug}"}""";
        spec.Headers["X-Ticket"] = "{ticketId}";

        await RunToCompletionAsync(h, spec);

        await WaitForRequestsAsync(h, 1);
        Assert.Equal($"https://cms.example/api/tickets/{h.TicketId}", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal($$"""{"title":"Publish the launch post","project":"{{h.Slug}}"}""", handler.LastBody);
        Assert.Equal(h.TicketId.ToString(), Assert.Single(handler.LastRequest.Headers.GetValues("X-Ticket")));
    }

    [Fact]
    public async Task Spec_objects_are_never_mutated_by_rendering()
    {
        // The chain snapshot holds the same spec instances as the on-disk config, so a chain that
        // substituted in place would poison every later firing.
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/tickets/{ticketId}");
        spec.BodyTemplate = """{"title":"{ticketTitle}"}""";

        await RunToCompletionAsync(h, spec);

        Assert.Equal("https://cms.example/api/tickets/{ticketId}", spec.Url);
        Assert.Equal("""{"title":"{ticketTitle}"}""", spec.BodyTemplate);
    }

    // ── Failure handling: 4xx / 5xx × abortOnFailure ─────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Failed_request_aborts_the_rest_of_the_chain_when_abortOnFailure_is_set(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Respond(status, """{"error":"nope"}""");
        using var h = await BuildAsync(handler);

        await DispatchAsync(h,
            Post("https://cms.example/first", abortOnFailure: true),
            Post("https://cms.example/second"));

        await WaitForRequestsAsync(h, 1);
        await Task.Delay(300); // give a (wrongly) continuing chain time to issue the second call
        Assert.Single(handler.Requests);
        Assert.Equal("https://cms.example/first", handler.LastRequest.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Failed_request_continues_the_chain_when_abortOnFailure_is_not_set(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Respond(status, """{"error":"nope"}""");
        using var h = await BuildAsync(handler);

        await DispatchAsync(h,
            Post("https://cms.example/first", abortOnFailure: false),
            Post("https://cms.example/second"));

        await WaitForRequestsAsync(h, 2);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://cms.example/second", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Failed_request_still_publishes_its_status_to_the_chain()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.Conflict, "already published");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("https://cms.example/api/publish", abortOnFailure: false),
            new AddCommentActionSpec { Content = "status={http.status} body={http.body}", Author = "automation" });

        Assert.Equal("status=409 body=already published", Assert.Single(await CommentsAsync(h)));
    }

    // ── Timeout ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timed_out_request_does_not_crash_and_honors_abortOnFailure()
    {
        var handler = FakeHttpMessageHandler.Hang();
        using var h = await BuildAsync(handler);

        var hanging = Post("https://cms.example/slow", abortOnFailure: true);
        hanging.TimeoutSeconds = 1;

        await DispatchAsync(h, hanging, Post("https://cms.example/second"));

        await WaitForRequestsAsync(h, 1);
        await Task.Delay(TimeSpan.FromSeconds(2)); // outlive the 1s timeout
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timed_out_request_lets_the_chain_continue_when_abortOnFailure_is_not_set()
    {
        var responses = 0;
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            // Only the first call hangs; the follow-up must still be dispatched.
            if (Interlocked.Increment(ref responses) == 1)
                await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var h = await BuildAsync(handler);

        var hanging = Post("https://cms.example/slow", abortOnFailure: false);
        hanging.TimeoutSeconds = 1;

        await DispatchAsync(h, hanging, Post("https://cms.example/second"));

        await WaitForRequestsAsync(h, 2);
        Assert.Equal("https://cms.example/second", handler.LastRequest.RequestUri!.ToString());
    }

    // ── Malformed / non-JSON bodies ─────────────────────────────────────────

    [Fact]
    public async Task Malformed_json_body_is_stored_raw_without_crashing()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, """{"id": 42, oops}""");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("https://cms.example/api/publish"),
            new AddCommentActionSpec { Content = "raw={http.body} field={http.body.id}", Author = "automation" });

        // Raw body captured; no field placeholders published, so {http.body.id} stays verbatim.
        Assert.Equal("""raw={"id": 42, oops} field={http.body.id}""", Assert.Single(await CommentsAsync(h)));
    }

    [Fact]
    public async Task Non_json_body_is_stored_raw()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "  OK, queued  ", "text/plain");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("https://cms.example/api/publish"),
            new AddCommentActionSpec { Content = "[{http.body}]", Author = "automation" });

        Assert.Equal("[OK, queued]", Assert.Single(await CommentsAsync(h)));
    }

    [Fact]
    public async Task Nested_json_values_are_captured_as_raw_json_text()
    {
        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK, """{"id":7,"meta":{"tag":"x"},"ok":true,"missing":null}""");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("https://cms.example/api/publish"),
            new AddCommentActionSpec
            {
                Content = "{http.body.id}|{http.body.meta}|{http.body.ok}|{http.body.missing}|",
                Author = "automation",
            });

        Assert.Equal("""7|{"tag":"x"}|true||""", Assert.Single(await CommentsAsync(h)));
    }

    // ── SecretRef ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretRef_resolves_the_environment_variable_into_a_bearer_header()
    {
        const string varName = "GIGACLAW_TEST_CMS_TOKEN";
        Environment.SetEnvironmentVariable(varName, "s3cr3t-value");
        try
        {
            var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
            using var h = await BuildAsync(handler);

            var spec = Post("https://cms.example/api/publish");
            spec.SecretRef = varName;

            await RunToCompletionAsync(h, spec);

            await WaitForRequestsAsync(h, 1);
            Assert.Equal("Bearer s3cr3t-value",
                Assert.Single(handler.LastRequest.Headers.GetValues("Authorization")));
        }
        finally { Environment.SetEnvironmentVariable(varName, null); }
    }

    [Fact]
    public async Task Missing_SecretRef_blocks_dispatch_without_issuing_a_request()
    {
        const string varName = "GIGACLAW_TEST_DEFINITELY_NOT_SET";
        Environment.SetEnvironmentVariable(varName, null);
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish", abortOnFailure: true);
        spec.SecretRef = varName;

        await DispatchAsync(h, spec);

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Explicit_authorization_header_wins_over_SecretRef()
    {
        const string varName = "GIGACLAW_TEST_CMS_TOKEN_2";
        Environment.SetEnvironmentVariable(varName, null);
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish");
        spec.SecretRef = varName;
        spec.Headers["Authorization"] = "Basic explicit";

        await RunToCompletionAsync(h, spec);

        await WaitForRequestsAsync(h, 1);
        Assert.Equal("Basic explicit",
            Assert.Single(handler.LastRequest.Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task Unresolved_url_placeholder_blocks_dispatch_without_issuing_a_request()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        await DispatchAsync(h, Post("https://cms.example/api/{missingRoute}", abortOnFailure: true));

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unresolved_header_placeholder_blocks_dispatch_without_issuing_a_request()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish", abortOnFailure: true);
        spec.Headers["X-Trace"] = "{missingTraceId}";

        await DispatchAsync(h, spec);

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unresolved_content_type_placeholder_blocks_dispatch_without_issuing_a_request()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish", abortOnFailure: true);
        spec.Headers["Content-Type"] = "application/{missingFormat}";
        spec.BodyTemplate = """{"ok":true}""";

        await DispatchAsync(h, spec);

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unresolved_body_placeholder_blocks_dispatch_without_issuing_a_request()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish", abortOnFailure: true);
        spec.BodyTemplate = """{"title":"{missingTitle}"}""";

        await DispatchAsync(h, spec);

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ordinary_json_braces_in_body_do_not_count_as_unresolved_placeholders()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish");
        spec.BodyTemplate = """{"meta":{"ok":true},"items":[{"name":"one"}]}""";

        await RunToCompletionAsync(h, spec);

        await WaitForRequestsAsync(h, 1);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastBody!);
        Assert.True(doc.RootElement.GetProperty("meta").GetProperty("ok").GetBoolean());
        Assert.Equal("one", doc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    // ── Bad configuration ───────────────────────────────────────────────────

    [Fact]
    public async Task Non_absolute_url_is_skipped_without_issuing_a_request()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        await RunToCompletionAsync(h,
            Post("not-a-url"),
            new AddCommentActionSpec { Content = "reached status={http.status}", Author = "automation" });

        Assert.Empty(handler.Requests);
        // Chain continues (abortOnFailure not set) and the placeholder still renders.
        Assert.Equal("reached status=0", Assert.Single(await CommentsAsync(h)));
    }

    // ── {projectSlug} placeholder ─────────────────────────────────────────

    [Fact]
    public async Task ProjectSlug_placeholder_renders_the_firing_projects_slug()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish");
        spec.BodyTemplate = """{"venture":"{projectSlug}","sourceTicket":"{projectSlug}#{ticketId}"}""";

        await RunToCompletionAsync(h, spec);

        await WaitForRequestsAsync(h, 1);
        Assert.Equal($$"""{"venture":"{{h.Slug}}","sourceTicket":"{{h.Slug}}#{{h.TicketId}}"}""", handler.LastBody);
    }

    // ── {draft.*} frontmatter placeholders (AD-7) ──────────────────────────

    private const string ValidDraftDescription = """
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

    private static HttpRequestActionSpec CmsDispatch(bool abortOnFailure = true) => new()
    {
        Url = "https://cms.example/api/ai/draft",
        Method = "POST",
        AbortOnFailure = abortOnFailure,
        TimeoutSeconds = 5,
        BodyTemplate = """
            {"title":"{draft.title}","slug":"{draft.slug}","body":"{draft.body}","excerpt":"{draft.excerpt}","contentType":"{draft.contentType}","seo":{"title":"{draft.seo.title}","description":"{draft.seo.description}","primaryKeyword":"{draft.seo.primaryKeyword}"},"venture":"{projectSlug}"}
            """,
    };

    [Fact]
    public async Task Draft_placeholders_are_rendered_from_ticket_description_frontmatter()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, """{"id":1,"slug":"how-to-ship-faster","adminUrl":"https://zz.example/admin/1"}""");
        using var h = await BuildAsync(handler, description: ValidDraftDescription);

        await RunToCompletionAsync(h, CmsDispatch());

        await WaitForRequestsAsync(h, 1);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("How to Ship Faster", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("how-to-ship-faster", doc.RootElement.GetProperty("slug").GetString());
        Assert.Contains("Body content with **markdown**.", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("ship faster", doc.RootElement.GetProperty("seo").GetProperty("primaryKeyword").GetString());
        Assert.Equal(h.Slug, doc.RootElement.GetProperty("venture").GetString());
    }

    [Fact]
    public async Task Draft_values_containing_quotes_and_newlines_are_JSON_escaped_in_the_body()
    {
        const string description = """
            ---
            title: A "quoted" title
            ---
            Line one
            Line two with "quotes" and a \ backslash
            """;
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler, description: description);

        var spec = CmsDispatch();
        spec.BodyTemplate = """{"title":"{draft.title}","body":"{draft.body}"}""";
        await RunToCompletionAsync(h, spec);

        await WaitForRequestsAsync(h, 1);
        // The body the executor actually sent must itself be valid, parseable JSON.
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("""A "quoted" title""", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Line one\nLine two with \"quotes\" and a \\ backslash", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Missing_frontmatter_blocks_dispatch_without_posting_a_malformed_body()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler, description: "just a plain description, no frontmatter at all");

        await DispatchAsync(h, CmsDispatch(abortOnFailure: true));

        await Task.Delay(300); // give a wrongly-dispatching executor time to issue the request
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Missing_title_in_frontmatter_blocks_dispatch()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler, description: "---\nslug: no-title-here\n---\nbody");

        await DispatchAsync(h, CmsDispatch(abortOnFailure: true));

        await Task.Delay(300);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Parse_failure_honors_abortOnFailure_false_and_continues_the_chain()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler, description: "no frontmatter here");

        await RunToCompletionAsync(h,
            CmsDispatch(abortOnFailure: false),
            new AddCommentActionSpec { Content = "after parse failure, status={http.status}", Author = "automation" });

        Assert.Empty(handler.Requests);
        Assert.Equal("after parse failure, status=0", Assert.Single(await CommentsAsync(h)));
    }

    // ── FailureComment / FailureStatus ─────────────────────────────────────

    [Fact]
    public async Task FailureComment_and_FailureStatus_are_applied_on_a_non_2xx_response()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.InternalServerError, "boom");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish", abortOnFailure: true);
        spec.FailureComment = "Dispatch failed: status={http.status} error={http.error}";
        spec.FailureStatus = "Blocked";

        await DispatchAsync(h, spec);
        await WaitForRequestsAsync(h, 1);

        // Poll: the failure side effects run inside the (detached) chain after the response.
        Ticket? ticket = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
            if (ticket!.Status == "Blocked" && ticket.Comments.Count > 0) break;
            await Task.Delay(20);
        }

        Assert.Equal("Blocked", ticket!.Status);
        var comment = Assert.Single(ticket.Comments);
        Assert.Equal("Dispatch failed: status=500 error=HTTP 500", comment.Content);
        Assert.Equal("automation", comment.Author);
    }

    [Fact]
    public async Task FailureComment_and_FailureStatus_are_applied_on_a_frontmatter_parse_failure()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler, description: "no frontmatter here");

        var spec = CmsDispatch(abortOnFailure: true);
        spec.FailureComment = "CMS dispatch failed: {http.error}";
        spec.FailureStatus = "Blocked";

        await DispatchAsync(h, spec);

        Ticket? ticket = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
            if (ticket!.Status == "Blocked" && ticket.Comments.Count > 0) break;
            await Task.Delay(20);
        }

        Assert.Empty(handler.Requests);
        Assert.Equal("Blocked", ticket!.Status);
        var comment = Assert.Single(ticket.Comments);
        Assert.StartsWith("CMS dispatch failed: frontmatter:", comment.Content);
    }

    [Fact]
    public async Task FailureComment_is_not_posted_on_success()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "{}");
        using var h = await BuildAsync(handler);

        var spec = Post("https://cms.example/api/publish");
        spec.FailureComment = "should never appear";
        spec.FailureStatus = "Blocked";

        await RunToCompletionAsync(h, spec);

        var ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
        Assert.Empty(ticket!.Comments);
        Assert.Equal("Review", ticket.Status);
    }

    // ── Task 10 / AD-7 round-trip contract: a realistic content-writer draft through the
    //    *real* cms-dispatch-on-done BodyTemplate shipped in ProjectTemplate/Agents/automations.json.
    //    This is the "sentinel-style validation" the plan's risk table calls for: if the
    //    content-writer SKILL.md contract and the DraftFrontmatter parser ever drift from what
    //    the CMS dispatch template expects, this test goes red instead of a silent bad draft. ──

    private const string RealisticWriterDraft = """
        ---
        title: 5 Ways to Cut Kanban Cycle Time
        slug: cut-kanban-cycle-time
        categorySlug: operations
        excerpt: Five field-tested changes that shrink the Todo-to-Done gap without adding headcount.
        contentType: article
        tags:
          - workflow automation
          - kanban
          - AI-assisted publishing
        seo:
          title: Cut Kanban Cycle Time: 5 Field-Tested Fixes
          description: Five practical, field-tested ways to shrink kanban cycle time without hiring — from WIP limits to automated quality gates.
          primaryKeyword: reduce kanban cycle time
        imagePrompt: a stopwatch resting on a kanban board with cards flowing left to right, editorial photography style
        ---
        # 5 Ways to Cut Kanban Cycle Time

        Cycle time is the single number that tells you whether your board is actually moving
        work, or just displaying it.

        ## 1. Cap work in progress

        A WIP limit forces the team to finish before starting. Without one, everything looks
        "in progress" and nothing is.

        ## 2. Automate the quality gate

        Draft, critique, and revise as board columns — not as a person who has to remember to
        review — closes the loop without adding a meeting.
        """;

    private static string LoadRealCmsDispatchBodyTemplate()
    {
        var json = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "ProjectTemplate", "Agents", "automations.json"));
        var config = System.Text.Json.JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)
            ?? throw new InvalidDataException("Template automations.json deserialized to null.");
        var dispatch = Assert.Single(config.Automations, a => a.Id == "cms-dispatch-on-done");
        return Assert.Single(dispatch.Actions.OfType<HttpRequestActionSpec>()).BodyTemplate;
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

    [Fact]
    public void Realistic_writer_draft_parses_and_carries_every_field_the_real_cms_template_references()
    {
        Assert.True(DraftFrontmatter.TryParse(RealisticWriterDraft, out var draft, out var error));
        Assert.Null(error);
        Assert.Equal("5 Ways to Cut Kanban Cycle Time", draft!.Title);
        Assert.Equal("cut-kanban-cycle-time", draft.Slug);
        Assert.Equal("operations", draft.CategorySlug);
        Assert.Equal(["workflow automation", "kanban", "AI-assisted publishing"], draft.Tags);
        Assert.False(string.IsNullOrWhiteSpace(draft.Excerpt));
        Assert.Equal("article", draft.ContentType);
        Assert.False(string.IsNullOrWhiteSpace(draft.ImagePrompt)); // AD-8: always emitted
        Assert.False(string.IsNullOrWhiteSpace(draft.SeoTitle));
        Assert.False(string.IsNullOrWhiteSpace(draft.SeoDescription));
        Assert.False(string.IsNullOrWhiteSpace(draft.SeoPrimaryKeyword));
        Assert.Contains("# 5 Ways to Cut Kanban Cycle Time", draft.Body);

        // Not a hand-copied template — the actual shipped one.
        var template = LoadRealCmsDispatchBodyTemplate();
        foreach (var placeholder in new[]
        {
            "{draft.title}", "{draft.slug}", "{draft.body}", "{draft.excerpt}", "{draft.contentType}",
            "{draft.seo.title}", "{draft.seo.description}", "{draft.seo.primaryKeyword}",
        })
        {
            Assert.Contains(placeholder, template);
        }
    }

    [Fact]
    public async Task Realistic_writer_draft_dispatches_through_the_real_cms_template_as_valid_json()
    {
        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            """{"id":9,"slug":"cut-kanban-cycle-time","adminUrl":"https://zz.example/admin/9"}""");
        using var h = await BuildAsync(handler, description: RealisticWriterDraft);

        var spec = new HttpRequestActionSpec
        {
            Url = "https://cms.example/api/ai/draft",
            Method = "POST",
            BodyTemplate = LoadRealCmsDispatchBodyTemplate(),
            TimeoutSeconds = 5,
            AbortOnFailure = true,
        };

        await RunToCompletionAsync(h, spec);
        await WaitForRequestsAsync(h, 1);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("5 Ways to Cut Kanban Cycle Time", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("cut-kanban-cycle-time", doc.RootElement.GetProperty("slug").GetString());
        Assert.Equal("article", doc.RootElement.GetProperty("contentType").GetString());
        Assert.Equal("reduce kanban cycle time", doc.RootElement.GetProperty("seo").GetProperty("primaryKeyword").GetString());
        Assert.Contains("Cycle time is the single number", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal(h.Slug, doc.RootElement.GetProperty("venture").GetString());
        Assert.Equal($"{h.Slug}#{h.TicketId}", doc.RootElement.GetProperty("sourceTicket").GetString());
        if (doc.RootElement.TryGetProperty("categorySlug", out var categorySlug))
            Assert.Equal("operations", categorySlug.GetString());
        if (doc.RootElement.TryGetProperty("tags", out var tags))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Array, tags.ValueKind);
            Assert.Equal(
                new[] { "workflow automation", "kanban", "AI-assisted publishing" },
                tags.EnumerateArray().Select(tag => tag.GetString() ?? "").ToArray());
        }
    }
}

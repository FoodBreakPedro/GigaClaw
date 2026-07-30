using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Covers the instant (signal) path of ticketCommentAdded: conditions must be evaluated
/// against the ticket's live status, and a comment consumed by a signal dispatch must not
/// be re-fired by the 30s poll path.
/// </summary>
public class TicketCommentAddedSignalPathTests
{
    private sealed class Harness : IDisposable
    {
        public TempDir Tmp { get; } = new();
        public ProjectService Projects { get; }
        public MemberService Members { get; }
        public TicketService Tickets { get; }
        public LabelService Labels { get; }
        public SessionRegistry Sessions { get; } = new();
        public AgentRunRegistry Runs { get; } = new();
        public ActionExecutor Executor { get; }

        public Harness()
        {
            Projects = new ProjectService(Tmp.Path);
            Members = new MemberService(Projects);
            Tickets = new TicketService(Projects, Members);
            Labels = new LabelService(Projects);
            var cost = new CostTracker();
            var runner = new ClaudeRunner(
                Sessions, Runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
            Executor = new ActionExecutor(
                Tickets, Members, Labels, Sessions, Runs, runner, cost,
                new LocalizationService(new AppSettingsService(Tmp.Path)), Projects,
                new RunStateManager(Runs, cost, Tickets, NullLogger.Instance),
                FakeHttpClientFactory.Unused, TestTeamRuns.For(Projects, Tickets), NullLogger.Instance);
        }

        public TriggerContext BuildContext(string slug, string workspace, AutomationRule automation) =>
            new()
            {
                ProjectSlug = slug,
                WorkspacePath = workspace,
                Automation = automation,
                Tickets = Tickets,
                Members = Members,
                Sessions = Sessions,
                Runs = Runs,
                Now = DateTime.UtcNow,
            };

        public void Dispose() => Tmp.Dispose();
    }

    private static AutomationRule CommentAutomation(
        TicketCommentAddedTriggerSpec spec,
        List<ConditionSpec>? conditions = null,
        List<ActionSpec>? actions = null) =>
        new()
        {
            Id = "comment-reply",
            Trigger = spec,
            Conditions = conditions ?? [],
            Actions = actions ?? [],
        };

    // ── Condition evaluation on signal firings ──────────────────────────────

    [Fact]
    public async Task Signal_firing_passes_ticketInColumn_when_ticket_in_named_column()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-cond-pass");
        var ticket = await h.Tickets.CreateTicketAsync(project.Slug, "Fix login", status: "InProgress");
        var automation = CommentAutomation(
            new TicketCommentAddedTriggerSpec(),
            conditions: [new TicketInColumnConditionSpec { Columns = ["InProgress"] }]);
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = h.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };

        // Signal firings carry only the ticket id — no title, no status.
        var signalFiring = new TriggerFiring(ticket.Id, null, null);

        Assert.True(await h.Executor.ConditionsMatchAsync(runtime, automation, signalFiring));
    }

    [Fact]
    public async Task Signal_firing_fails_ticketInColumn_when_ticket_in_other_column()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-cond-fail");
        var ticket = await h.Tickets.CreateTicketAsync(project.Slug, "Fix login", status: "Backlog");
        var automation = CommentAutomation(
            new TicketCommentAddedTriggerSpec(),
            conditions: [new TicketInColumnConditionSpec { Columns = ["InProgress"] }]);
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = h.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };

        Assert.False(await h.Executor.ConditionsMatchAsync(
            runtime, automation, new TriggerFiring(ticket.Id, null, null)));
    }

    [Fact]
    public async Task Ticketless_firing_still_fails_ticketInColumn()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-cond-ticketless");
        var automation = CommentAutomation(
            new TicketCommentAddedTriggerSpec(),
            conditions: [new TicketInColumnConditionSpec { Columns = ["InProgress"] }]);
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = h.Projects.ResolveWorkspacePath(project),
            Config = new AutomationConfig(),
        };

        Assert.False(await h.Executor.ConditionsMatchAsync(
            runtime, automation, new TriggerFiring(null, null, null)));
    }

    // ── Signal/poll deduplication at the trigger level ──────────────────────

    [Fact]
    public async Task Consumed_signal_comment_is_not_refired_by_poll_but_later_comments_are()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-dedup");
        var workspace = h.Projects.ResolveWorkspacePath(project);
        var ticket = await h.Tickets.CreateTicketAsync(project.Slug, "Fix login", status: "InProgress");
        await h.Tickets.AddCommentAsync(project.Slug, ticket.Id, "please check", "owner");

        var spec = new TicketCommentAddedTriggerSpec { PollSeconds = 0 };
        var trigger = new TicketCommentAddedTrigger(spec);
        var automation = CommentAutomation(spec);

        Assert.True(trigger.TryHandleExternalSignal(
            new CommentAddedSignal(ticket.Id, "owner", "please check"), out var signalFirings));
        var firing = Assert.Single(signalFirings);

        // What TriggerHandler does when the signal firing passes conditions and dispatches.
        await trigger.ConsumeSignalFiringAsync(h.BuildContext(project.Slug, workspace, automation), firing);

        var pollFirings = await trigger.EvaluateAsync(
            h.BuildContext(project.Slug, workspace, automation), CancellationToken.None);
        Assert.Empty(pollFirings);

        // The cursor must not have advanced past the consumed comment: a later comment still fires.
        await h.Tickets.AddCommentAsync(project.Slug, ticket.Id, "one more thing", "owner");
        var laterFirings = await trigger.EvaluateAsync(
            h.BuildContext(project.Slug, workspace, automation), CancellationToken.None);
        Assert.Equal(ticket.Id, Assert.Single(laterFirings).TicketId);
    }

    // ── End-to-end through TriggerHandler ───────────────────────────────────

    private static (TriggerHandler Handler, ProjectRuntimeManager Manager) BuildEngine(Harness h, AutomationStore store)
    {
        var manager = new ProjectRuntimeManager(
            store, new TriggerStateStore(h.Projects), h.Projects, NullLogger.Instance);
        var handler = new TriggerHandler(
            h.Projects, manager, h.Executor, h.Tickets, h.Members, h.Sessions, h.Runs,
            TestTeamRuns.For(h.Projects, h.Tickets), NullLogger.Instance);
        return (handler, manager);
    }

    private static AutomationConfig ReplyBotConfig(int pollSeconds) => new()
    {
        Automations =
        [
            new AutomationRule
            {
                Id = "reply-bot",
                Trigger = new TicketCommentAddedTriggerSpec
                {
                    PollSeconds = pollSeconds,
                    Authors = ["owner"],
                },
                Conditions = [new TicketInColumnConditionSpec { Columns = ["InProgress"] }],
                Actions = [new AddCommentActionSpec { Content = "ack", Author = "bot" }],
            },
        ],
    };

    private static async Task<int> BotCommentCountAsync(Harness h, string slug, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(slug, ticketId);
        Assert.NotNull(ticket);
        return ticket.Comments.Count(c => c.Author == "bot");
    }

    [Fact]
    public async Task Signal_dispatch_passes_conditions_while_poll_is_dormant()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-instant");
        using var store = new AutomationStore(h.Projects);
        var (handler, manager) = BuildEngine(h, store);

        // Poll every hour: after the warm-up tick below, any dispatch inside this test
        // can only come from the signal path.
        await store.SaveAsync(project.Slug, ReplyBotConfig(pollSeconds: 3600));
        var ticket = await h.Tickets.CreateTicketAsync(project.Slug, "Fix login", status: "InProgress");
        await handler.ProcessTickAsync(CancellationToken.None); // warm-up: first poll runs, then goes dormant

        await h.Tickets.AddCommentAsync(project.Slug, ticket.Id, "please check", "owner");
        await manager.NotifySignalAsync(project.Slug, new CommentAddedSignal(ticket.Id, "owner", "please check"));
        await handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, await BotCommentCountAsync(h, project.Slug, ticket.Id));
    }

    [Fact]
    public async Task Poll_does_not_double_fire_after_signal_dispatch()
    {
        using var h = new Harness();
        var project = await h.Projects.CreateProjectAsync("signal-no-dup");
        using var store = new AutomationStore(h.Projects);
        var (handler, manager) = BuildEngine(h, store);

        // Poll on every tick: without dedup, the poll immediately re-fires the comment
        // the signal path just dispatched.
        await store.SaveAsync(project.Slug, ReplyBotConfig(pollSeconds: 0));
        var ticket = await h.Tickets.CreateTicketAsync(project.Slug, "Fix login", status: "InProgress");

        await h.Tickets.AddCommentAsync(project.Slug, ticket.Id, "please check", "owner");
        await manager.NotifySignalAsync(project.Slug, new CommentAddedSignal(ticket.Id, "owner", "please check"));
        for (var tick = 0; tick < 3; tick++)
            await handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, await BotCommentCountAsync(h, project.Slug, ticket.Id));
    }
}

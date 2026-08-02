using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The reviewer-retry receipt is the budget: <see cref="ReviewerRetry.Resolve"/> counts one retry
/// per <c>GIGACLAW-REREVIEW v1</c> comment on the ticket. So the receipt has to be written by the
/// thing it claims to record — a dispatch — and by nothing else.
/// <para>
/// It used to be an <c>addComment</c> action sitting in front of the <c>runAgent</c> in
/// <c>automations.json</c>, which made it a lie whenever the dispatch did not happen. A reviewer
/// busy in its own concurrency group is skipped by <see cref="RunStateManager.ShouldSkipAsync"/>,
/// and a skipped <c>runAgent</c> ends the chain — but the receipt ahead of it was already
/// committed. The ticket was then unreachable: the <c>withinCap</c> arm no longer matched (budget
/// spent), the <c>exhausted</c> arm waits for a reviewer comment nobody asked for, and the retry
/// arms move no ticket, so no column poll would find it either.
/// </para>
/// </summary>
[Collection("MockClaude")]
public class ReviewerRetryReceiptTests
{
    private sealed record Harness(
        ActionExecutor Executor,
        ProjectRuntime Runtime,
        TicketService Tickets,
        AgentRunRegistry Runs,
        int TicketId);

    private static async Task<Harness> BuildAsync(string dataDir, string scenario = "default")
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("reviewer-retry-receipt");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "qa-tester", scenario: scenario);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new ClaudeRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<ClaudeRunner>.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost,
            new LocalizationService(new AppSettingsService(dataDir)), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            FakeHttpClientFactory.Unused, TestTeamRuns.For(projects, tickets), NullLogger.Instance);

        await members.CreateMemberAsync(project.Slug, "qa-tester");
        await members.CreateMemberAsync(project.Slug, "programmer");
        var ticket = await tickets.CreateTicketAsync(
            project.Slug, "Ship the change", status: "Review", assignedTo: "programmer");

        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = workspace,
            Config = new AutomationConfig { Automations = [] },
        };
        return new Harness(executor, runtime, tickets, runs, ticket.Id);
    }

    /// <summary>The shape every shipped retry arm has: a withinCap budget and a reviewer re-run.</summary>
    private static AutomationRule RetryArm() => new()
    {
        Id = "verdict-gate-qa-reviewer-retry",
        Enabled = true,
        Trigger = new TicketCommentAddedTriggerSpec { Authors = ["qa-tester"] },
        Conditions =
        [
            new ReviewerRetryBudgetConditionSpec { Mode = "withinCap", Agent = "qa-tester", MaxRetries = 1 },
        ],
        Actions =
        [
            new RunAgentActionSpec { Agent = "qa-tester", ConcurrencyGroup = "qa-tester", MaxTurns = 1 },
        ],
    };

    private static AgentRun Busy(string projectSlug, string group) => new()
    {
        RunId = "already-running",
        ProjectSlug = projectSlug,
        TicketId = null,
        AgentName = group,
        SkillFile = $"{group}/SKILL.md",
        ConcurrencyGroup = group,
        StartedAt = DateTime.UtcNow,
    };

    private static async Task WaitForRunEndAsync(AgentRunRegistry runs, string slug)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            if (!runs.ActiveForProject(slug).Any(r => r.AgentName == "qa-tester")) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for the re-review dispatch to finish.");
    }

    private static int ReceiptsOn(GigaClaw.Core.Models.Ticket ticket) =>
        ticket.Comments.Count(c => ReviewerRetry.IsRetryReceipt(c.Content, out _));

    // ── The regression ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_retry_that_never_dispatches_writes_no_receipt_and_spends_no_budget()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        // The reviewer is already busy in its own concurrency group, so the dispatch is skipped.
        h.Runs.Register(Busy(h.Runtime.Slug, "qa-tester"));

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, RetryArm(), new TriggerFiring(h.TicketId, "Ship the change", "Review"),
            CancellationToken.None);

        var after = (await h.Tickets.GetTicketAsync(h.Runtime.Slug, h.TicketId))!;
        Assert.Equal(0, ReceiptsOn(after));

        // Nothing was asked, so nothing was spent: the arm still matches on the next firing rather
        // than stranding the ticket between a spent budget and an exhaustion arm that never fires.
        var state = ReviewerRetry.Resolve(
            [.. after.Comments.OrderBy(c => c.CreatedAt)
                .Select(c => new VerdictComment(c.Content, c.Author, c.CreatedAt))],
            maxRetries: 1,
            agent: "qa-tester");
        Assert.Equal(0, state.RetriesUsed);
        Assert.False(state.Exhausted);
        Assert.Single(h.Runs.ActiveForProject(h.Runtime.Slug));
    }

    // ── The positive half of the same rule ──────────────────────────────────

    [Fact]
    public async Task A_dispatched_retry_writes_exactly_one_receipt_and_spends_the_budget()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, RetryArm(), new TriggerFiring(h.TicketId, "Ship the change", "Review"),
            CancellationToken.None);
        await WaitForRunEndAsync(h.Runs, h.Runtime.Slug);

        var after = (await h.Tickets.GetTicketAsync(h.Runtime.Slug, h.TicketId))!;
        var receipt = Assert.Single(after.Comments, c => ReviewerRetry.IsRetryReceipt(c.Content, out _));
        Assert.Equal("automation", receipt.Author);
        Assert.True(ReviewerRetry.IsRetryReceipt(receipt.Content, out var reviewer));
        Assert.Equal("qa-tester", reviewer);

        var state = ReviewerRetry.Resolve(
            [.. after.Comments.OrderBy(c => c.CreatedAt)
                .Select(c => new VerdictComment(c.Content, c.Author, c.CreatedAt))],
            maxRetries: 1,
            agent: "qa-tester");
        Assert.Equal(1, state.RetriesUsed);
        Assert.True(state.Exhausted);
    }

    /// <summary>
    /// The receipt is minted from the automation's own <c>withinCap</c> condition, so an ordinary
    /// dispatch — every other <c>runAgent</c> in the template — must not grow one.
    /// </summary>
    [Fact]
    public async Task An_ordinary_dispatch_writes_no_reviewer_retry_receipt()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        var plain = RetryArm();
        plain.Id = "qa-on-review";
        plain.Conditions = [];

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, plain, new TriggerFiring(h.TicketId, "Ship the change", "Review"),
            CancellationToken.None);
        await WaitForRunEndAsync(h.Runs, h.Runtime.Slug);

        var after = (await h.Tickets.GetTicketAsync(h.Runtime.Slug, h.TicketId))!;
        Assert.Equal(0, ReceiptsOn(after));
    }
}

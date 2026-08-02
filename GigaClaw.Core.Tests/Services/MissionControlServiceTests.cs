using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Plan 4.2: the cross-project Mission Control aggregation. Covers the sections whose logic is not
/// a plain projection — the vs-yesterday delta baseline, the attention queue's sources, and the
/// receipt-marker classification behind the activity feed.
/// </summary>
public sealed class MissionControlServiceTests
{
    private static (MissionControlService Mission, ProjectService Projects, TicketService Tickets,
        TicketStatSnapshotService Snapshots, MemberService Members) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var snapshots = new TicketStatSnapshotService(projects);
        var media = new LocalMediaJobService(
            projects, tickets, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalMediaJobService>.Instance);
        var mission = new MissionControlService(
            projects, tickets, snapshots, media, new AgentTeamService(), members,
            new AutomationStore(projects), new AgentRunRegistry());
        return (mission, projects, tickets, snapshots, members);
    }

    /// <summary>A snapshot may only be captured shortly after the day it describes ends, so tests
    /// that need a baseline have to say so explicitly instead of passing a bare UtcNow.</summary>
    private static DateTime JustAfterMidnight() => DateTime.UtcNow.Date.AddHours(1);

    [Fact]
    public async Task BuildAsync_HidesDeltasUntilAYesterdaySnapshotExists()
    {
        using var tmp = new TempDir();
        var (mission, projects, tickets, snapshots, _) = BuildSut(tmp);
        var project = await projects.CreateProjectAsync("mc-delta");
        await tickets.CreateTicketAsync(project.Slug, "One", status: "Backlog");

        var dayOne = await mission.BuildAsync(DateTime.UtcNow);
        Assert.All(dayOne.Kpis, kpi => Assert.Null(kpi.Delta));

        await snapshots.CaptureProjectAsync(project.Slug, JustAfterMidnight());
        await tickets.CreateTicketAsync(project.Slug, "Two", status: "Backlog");

        var dayTwo = await mission.BuildAsync(DateTime.UtcNow);
        var backlog = dayTwo.Kpis.Single(k => k.Column == "Backlog");
        Assert.Equal(2, backlog.Count);
        Assert.Equal(1, backlog.Delta);
    }

    [Fact]
    public async Task BuildAsync_ComputesDeltasOverOnlyTheProjectsThatHaveABaseline()
    {
        using var tmp = new TempDir();
        var (mission, projects, tickets, snapshots, _) = BuildSut(tmp);
        var baselined = await projects.CreateProjectAsync("mc-mixed-baselined");
        var fresh = await projects.CreateProjectAsync("mc-mixed-fresh");

        await tickets.CreateTicketAsync(baselined.Slug, "One", status: "Backlog");
        await snapshots.CaptureProjectAsync(baselined.Slug, JustAfterMidnight()); // baseline: 1 Backlog

        await tickets.CreateTicketAsync(baselined.Slug, "Two", status: "Backlog");
        await tickets.CreateTicketAsync(fresh.Slug, "Three", status: "Backlog");
        await tickets.CreateTicketAsync(fresh.Slug, "Four", status: "Backlog");

        var snapshot = await mission.BuildAsync(DateTime.UtcNow);
        var backlog = snapshot.Kpis.Single(k => k.Column == "Backlog");

        // The headline count is the whole board; the delta is only the baselined project's own
        // arithmetic. Differencing 4 live tickets against a 1-ticket baseline would report +3 and
        // blame it on one day of work that never happened.
        Assert.Equal(4, backlog.Count);
        Assert.Equal(1, backlog.Delta);
    }

    [Fact]
    public async Task BuildAsync_ShowsRosterAgentsThatHaveNeverRun()
    {
        using var tmp = new TempDir();
        var (mission, projects, _, _, members) = BuildSut(tmp);
        var project = await projects.CreateProjectAsync("mc-roster");
        await members.CreateMemberAsync(project.Slug, "qa-tester");

        var snapshot = await mission.BuildAsync(DateTime.UtcNow);

        // A configured agent with no dispatch at all is a finding, not an absence of data — the
        // workload tile used to drop it entirely because it had no cost-journal line.
        var agent = Assert.Single(snapshot.Workload, w => w.Agent == "qa-tester");
        Assert.Equal(0, agent.Dispatches);
        Assert.Null(agent.LastRunAtUtc);
        Assert.Equal("very-stale", agent.Staleness);
    }

    [Fact]
    public async Task BuildAsync_ExcludesPausedProjectsFromEverySection()
    {
        using var tmp = new TempDir();
        var (mission, projects, tickets, _, _) = BuildSut(tmp);
        var active = await projects.CreateProjectAsync("mc-active");
        var paused = await projects.CreateProjectAsync("mc-paused");
        await tickets.CreateTicketAsync(active.Slug, "Visible", status: "Todo");
        await tickets.CreateTicketAsync(paused.Slug, "Hidden", status: "Todo");
        await projects.TogglePauseAsync(paused.Slug);

        var snapshot = await mission.BuildAsync(DateTime.UtcNow);

        Assert.Single(snapshot.Projects);
        Assert.Equal(1, snapshot.Kpis.Single(k => k.Column == "Todo").Count);
        Assert.DoesNotContain(snapshot.RecentTickets, t => t.Title == "Hidden");
    }

    [Fact]
    public async Task BuildAsync_QueuesBlockedTicketsApprovalsAndCostCappedTickets()
    {
        using var tmp = new TempDir();
        var (mission, projects, tickets, _, _) = BuildSut(tmp);
        var project = await projects.CreateProjectAsync("mc-attention");

        var blocked = await tickets.CreateTicketAsync(project.Slug, "Merge deadlock", status: "Blocked");
        await tickets.AddCommentAsync(project.Slug, blocked.Id,
            $"GIGACLAW-GATE v1 ticket-{blocked.Id} blocked — qa-tester returned BLOCK.");

        var approval = await tickets.CreateTicketAsync(project.Slug, "Publish the post", status: "Review");
        var label = await new LabelService(projects)
            .CreateLabelAsync(project.Slug, MissionControlService.PendingApprovalLabel, "#f59e0b");
        await tickets.SetTicketLabelsAsync(project.Slug, approval.Id, [label.Id]);

        var capped = await tickets.CreateTicketAsync(project.Slug, "Runaway loop", status: "Todo");
        await tickets.AddCommentAsync(project.Slug, capped.Id,
            TicketCostCap.RenderReceipt(capped.Id, 5m, 5.4m, "programmer"), TicketCostCap.ReceiptAuthor);

        var snapshot = await mission.BuildAsync(DateTime.UtcNow);

        var blockedItem = Assert.Single(snapshot.Attention, i => i.Kind == "blocked");
        Assert.Equal(MissionSeverity.Critical, blockedItem.Severity);
        // The blocked reason is agent-authored prose, so it travels as a literal MissionText rather
        // than a localization key — the page renders it verbatim.
        Assert.Null(blockedItem.Detail.Key);
        Assert.Equal("qa-tester returned BLOCK", Assert.Single(blockedItem.Detail.Args));

        var approvalItem = Assert.Single(snapshot.Attention, i => i.Kind == "approval");
        Assert.Equal(approval.Id, approvalItem.TicketId);

        var cappedItem = Assert.Single(snapshot.Attention, i => i.Kind == "costcap");
        Assert.Equal(capped.Id, cappedItem.TicketId);

        // Critical sorts above warning so the queue reads top-down by urgency.
        Assert.Equal("blocked", snapshot.Attention[0].Kind);
    }

    [Fact]
    public async Task BuildAsync_StatusMixExcludesDone_AndResolvedTodayCountsIt()
    {
        using var tmp = new TempDir();
        var (mission, projects, tickets, _, _) = BuildSut(tmp);
        var project = await projects.CreateProjectAsync("mc-mix");
        await tickets.CreateTicketAsync(project.Slug, "Open", status: "Todo");
        var done = await tickets.CreateTicketAsync(project.Slug, "Shipped", status: "Todo");
        await tickets.MoveTicketAsync(project.Slug, done.Id, "Done");

        var snapshot = await mission.BuildAsync(DateTime.UtcNow);

        Assert.False(snapshot.StatusMix.ContainsKey("Done"));
        Assert.Equal(1, snapshot.StatusMix["Todo"]);
        Assert.Equal(1, snapshot.Kpis.Single(k => k.Column == MissionControlService.ResolvedKpi).Count);
    }

    [Theory]
    [InlineData("GIGACLAW-GATE v1 ticket-7 SHIP — gate passed.", "gate")]
    [InlineData("GIGACLAW-REPAIR v1 ticket-7 escalated 2/2", "repair")]
    [InlineData("GIGACLAW-REREVIEW v1 ticket-7 retry 1/1", "rereview")]
    [InlineData("GIGACLAW-COSTCAP v1 ticket-7 cap=5 spent=5.4", "costcap")]
    [InlineData("GIGACLAW-HANDOFF v1 ticket-7", "handoff")]
    public void DescribeMarker_ClassifiesKnownReceipts(string content, string expectedKind)
    {
        var (kind, text) = MissionControlService.DescribeMarker(content, 7, "qa-tester");
        Assert.Equal(expectedKind, kind);
        Assert.NotNull(text);
    }

    [Fact]
    public void DescribeMarker_DropsUnknownMarkers()
    {
        // The feed is a summary; an unrecognized marker belongs on the ticket, not in the feed.
        var (kind, _) = MissionControlService.DescribeMarker("GIGACLAW-SOMETHING-NEW v1 ticket-7", 7, "agent");
        Assert.Null(kind);
    }
}

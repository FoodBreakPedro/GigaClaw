using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Phase 1.1/1.5 of the return-to-sender work: the two assignee-shaped knobs the intake and
/// triage automations need from <see cref="TicketInColumnTrigger"/> — an "unassigned only" filter
/// the existing <c>assigneeSlug</c> cannot express, and an assignee applied when the bounded
/// retry series runs out.
/// </summary>
public class TicketInColumnTriggerIntakeTests
{
    private sealed record Fixture(
        TriggerContext Context,
        TicketService Tickets,
        MemberService Members,
        string Slug);

    private static async Task<Fixture> BuildAsync(
        string dataDir, TicketInColumnTriggerSpec spec, DateTime now, string automationId = "intake")
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("intake-trigger-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);

        var context = new TriggerContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            Automation = new AutomationRule { Id = automationId, Trigger = spec, Actions = [] },
            Tickets = tickets,
            Members = members,
            Sessions = new SessionRegistry(),
            Runs = new AgentRunRegistry(),
            Now = now,
        };
        return new Fixture(context, tickets, members, project.Slug);
    }

    private static TriggerContext At(TriggerContext source, DateTime now) => new()
    {
        ProjectSlug = source.ProjectSlug,
        WorkspacePath = source.WorkspacePath,
        Automation = source.Automation,
        Tickets = source.Tickets,
        Members = source.Members,
        Sessions = source.Sessions,
        Runs = source.Runs,
        Now = now,
    };

    // ── 1.1 the unassigned filter ───────────────────────────────────────────

    [Fact]
    public async Task Unassigned_trigger_fires_only_for_tickets_with_no_assignee()
    {
        using var tmp = new TempDir();
        var spec = new TicketInColumnTriggerSpec { Columns = ["Backlog"], Seconds = 1, Unassigned = true };
        var f = await BuildAsync(tmp.Path, spec, DateTime.UtcNow);
        await f.Members.CreateMemberAsync(f.Slug, "groomer");

        var intake = await f.Tickets.CreateTicketAsync(f.Slug, "Raw request", status: "Backlog");
        await f.Tickets.CreateTicketAsync(f.Slug, "Already routed", status: "Backlog", assignedTo: "groomer");

        var firings = await new TicketInColumnTrigger(spec).EvaluateAsync(f.Context, CancellationToken.None);

        var only = Assert.Single(firings);
        Assert.Equal(intake.Id, only.TicketId);
    }

    /// <summary>
    /// The whole point of the flag: an empty <c>assigneeSlug</c> means "any assignee", so without
    /// it the intake arm and the `groomer` arm would both claim every Backlog ticket.
    /// </summary>
    [Fact]
    public async Task An_empty_assignee_slug_still_means_any_assignee()
    {
        using var tmp = new TempDir();
        var spec = new TicketInColumnTriggerSpec { Columns = ["Backlog"], Seconds = 1, AssigneeSlug = "" };
        var f = await BuildAsync(tmp.Path, spec, DateTime.UtcNow);
        await f.Members.CreateMemberAsync(f.Slug, "groomer");

        await f.Tickets.CreateTicketAsync(f.Slug, "Raw request", status: "Backlog");
        await f.Tickets.CreateTicketAsync(f.Slug, "Already routed", status: "Backlog", assignedTo: "groomer");

        var firings = await new TicketInColumnTrigger(spec).EvaluateAsync(f.Context, CancellationToken.None);
        Assert.Equal(2, firings.Count);
    }

    /// <summary>A spec that asks for both matches nothing rather than silently picking one.</summary>
    [Fact]
    public async Task Unassigned_and_an_assignee_slug_together_match_nothing()
    {
        using var tmp = new TempDir();
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Backlog"],
            Seconds = 1,
            Unassigned = true,
            AssigneeSlug = "groomer",
        };
        var f = await BuildAsync(tmp.Path, spec, DateTime.UtcNow);
        await f.Members.CreateMemberAsync(f.Slug, "groomer");
        await f.Tickets.CreateTicketAsync(f.Slug, "Raw request", status: "Backlog");
        await f.Tickets.CreateTicketAsync(f.Slug, "Already routed", status: "Backlog", assignedTo: "groomer");

        Assert.Empty(await new TicketInColumnTrigger(spec).EvaluateAsync(f.Context, CancellationToken.None));
    }

    // ── 1.5 exhaustedAssignee ───────────────────────────────────────────────

    [Fact]
    public async Task Exhausting_the_retry_series_reassigns_before_it_moves_the_ticket()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
            ExhaustedAssignee = "groomer",
            ExhaustedStatus = "Backlog",
        };
        var f = await BuildAsync(tmp.Path, spec, start);
        await f.Members.CreateMemberAsync(f.Slug, "groomer");
        await f.Members.CreateMemberAsync(f.Slug, "programmer");
        var ticket = await f.Tickets.CreateTicketAsync(
            f.Slug, "Parked", status: "Todo", assignedTo: "programmer");

        var trigger = new TicketInColumnTrigger(spec);
        await trigger.CompleteFiringAsync(
            f.Context, new TriggerFiring(ticket.Id, "Parked", "Todo"), succeeded: true, start);

        var after = (await f.Tickets.GetTicketAsync(f.Slug, ticket.Id))!;
        Assert.Equal("groomer", after.AssignedTo);
        Assert.Equal("Backlog", after.Status);
    }

    /// <summary>
    /// Exhaustion handling exists to unstick a ticket, so a slug that is not a member of the
    /// project is skipped rather than aborting the move that gets the ticket out of the loop.
    /// </summary>
    [Fact]
    public async Task An_unknown_exhausted_assignee_is_skipped_and_the_move_still_happens()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
            ExhaustedAssignee = "no-such-agent",
            ExhaustedStatus = "Backlog",
        };
        var f = await BuildAsync(tmp.Path, spec, start);
        await f.Members.CreateMemberAsync(f.Slug, "programmer");
        var ticket = await f.Tickets.CreateTicketAsync(
            f.Slug, "Parked", status: "Todo", assignedTo: "programmer");

        await new TicketInColumnTrigger(spec).CompleteFiringAsync(
            f.Context, new TriggerFiring(ticket.Id, "Parked", "Todo"), succeeded: true, start);

        var after = (await f.Tickets.GetTicketAsync(f.Slug, ticket.Id))!;
        Assert.Equal("programmer", after.AssignedTo);
        Assert.Equal("Backlog", after.Status);
    }

    /// <summary>
    /// An <c>exhaustedAssignee</c> on its own is enough to make the series terminal: before this
    /// existed, exhaustion handling only ran when a status or a comment was configured, so a
    /// reassign-only spec would have counted attempts forever and never acted on them. The replay
    /// asserts the claim is taken once — the series stays suspended and nothing fires again.
    /// </summary>
    [Fact]
    public async Task An_exhausted_assignee_alone_triggers_exhaustion_handling_once()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
            ExhaustedAssignee = "groomer",
        };
        var f = await BuildAsync(tmp.Path, spec, start);
        await f.Members.CreateMemberAsync(f.Slug, "groomer");
        await f.Members.CreateMemberAsync(f.Slug, "programmer");
        var ticket = await f.Tickets.CreateTicketAsync(
            f.Slug, "Parked", status: "Todo", assignedTo: "programmer");
        var firing = new TriggerFiring(ticket.Id, "Parked", "Todo");

        await new TicketInColumnTrigger(spec).CompleteFiringAsync(f.Context, firing, succeeded: true, start);
        Assert.Equal("groomer", (await f.Tickets.GetTicketAsync(f.Slug, ticket.Id))!.AssignedTo);

        // Replaying the completion against an unchanged ticket is a no-op, and the ticket is no
        // longer served to the automation that gave up on it.
        var later = At(f.Context, start.AddSeconds(5));
        await new TicketInColumnTrigger(spec).CompleteFiringAsync(later, firing, succeeded: true, later.Now);
        Assert.Equal("groomer", (await f.Tickets.GetTicketAsync(f.Slug, ticket.Id))!.AssignedTo);
        Assert.Empty(await new TicketInColumnTrigger(spec).EvaluateAsync(later, CancellationToken.None));
    }
}

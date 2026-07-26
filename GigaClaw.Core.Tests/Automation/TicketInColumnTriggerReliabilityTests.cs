using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

public class TicketInColumnTriggerReliabilityTests
{
    private static async Task<(TriggerContext Context, TicketService Tickets, int TicketId)> BuildAsync(
        string dataDir,
        TicketInColumnTriggerSpec spec,
        DateTime now)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("bounded-trigger-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Parked", status: "Todo");
        var automation = new AutomationRule
        {
            Id = "resume-agent",
            Trigger = spec,
            Actions = [],
        };
        return (new TriggerContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            Automation = automation,
            Tickets = tickets,
            Members = members,
            Sessions = new SessionRegistry(),
            Runs = new AgentRunRegistry(),
            Now = now,
        }, tickets, ticket.Id);
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

    [Fact]
    public async Task Consecutive_attempts_are_bounded_and_survive_trigger_restart()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 2,
            RetryBackoffSeconds = 10,
        };
        var (ctx, _, ticketId) = await BuildAsync(tmp.Path, spec, start);
        var firing = new TriggerFiring(ticketId, "Parked", "Todo");

        var first = new TicketInColumnTrigger(spec);
        Assert.Single(await first.EvaluateAsync(ctx, CancellationToken.None));
        await first.CompleteFiringAsync(ctx, firing, succeeded: false, start);

        var afterBackoff = At(ctx, start.AddSeconds(11));
        var restarted = new TicketInColumnTrigger(spec);
        Assert.Single(await restarted.EvaluateAsync(afterBackoff, CancellationToken.None));
        await restarted.CompleteFiringAsync(afterBackoff, firing, succeeded: true, afterBackoff.Now);

        var afterSecondBackoff = At(ctx, start.AddSeconds(22));
        var restartedAgain = new TicketInColumnTrigger(spec);
        Assert.Empty(await restartedAgain.EvaluateAsync(afterSecondBackoff, CancellationToken.None));
    }

    [Fact]
    public async Task Ticket_edit_resets_suspended_attempt_series()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
        };
        var (ctx, tickets, ticketId) = await BuildAsync(tmp.Path, spec, start);
        var trigger = new TicketInColumnTrigger(spec);
        var firing = new TriggerFiring(ticketId, "Parked", "Todo");
        await trigger.CompleteFiringAsync(ctx, firing, succeeded: false, start);

        var suspended = new TicketInColumnTrigger(spec);
        Assert.Empty(await suspended.EvaluateAsync(At(ctx, start.AddSeconds(1)), CancellationToken.None));

        await Task.Delay(10);
        await tickets.UpdateTicketAsync(ctx.ProjectSlug, ticketId, description: "Owner supplied new information");

        var afterEdit = new TicketInColumnTrigger(spec);
        Assert.Single(await afterEdit.EvaluateAsync(At(ctx, start.AddSeconds(2)), CancellationToken.None));
    }

    [Fact]
    public async Task Comment_and_updatedAt_change_do_not_reset_attempt_series()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
        };
        var (ctx, tickets, ticketId) = await BuildAsync(tmp.Path, spec, start);
        var firing = new TriggerFiring(ticketId, "Parked", "Todo");
        await new TicketInColumnTrigger(spec)
            .CompleteFiringAsync(ctx, firing, succeeded: true, start);

        await tickets.AddCommentAsync(ctx.ProjectSlug, ticketId, "Agent progress note", "automation");

        var restarted = new TicketInColumnTrigger(spec);
        Assert.Empty(await restarted.EvaluateAsync(At(ctx, start.AddSeconds(1)), CancellationToken.None));
    }

    [Fact]
    public async Task Exhaustion_moves_and_comments_exactly_once()
    {
        using var tmp = new TempDir();
        var start = DateTime.UtcNow;
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = ["Todo"],
            Seconds = 1,
            MaxConsecutiveFirings = 1,
            RetryBackoffSeconds = 0,
            ExhaustedStatus = "Blocked",
            ExhaustedComment = "Automation stopped after the retry cap.",
        };
        var (ctx, tickets, ticketId) = await BuildAsync(tmp.Path, spec, start);
        var trigger = new TicketInColumnTrigger(spec);
        var firing = new TriggerFiring(ticketId, "Parked", "Todo");

        await trigger.CompleteFiringAsync(ctx, firing, succeeded: false, start);
        await trigger.CompleteFiringAsync(ctx, firing, succeeded: false, start.AddSeconds(1));

        var ticket = await tickets.GetTicketAsync(ctx.ProjectSlug, ticketId);
        Assert.NotNull(ticket);
        Assert.Equal("Blocked", ticket.Status);
        Assert.Single(
            ticket.Comments,
            c => c.Author == "automation" && c.Content == spec.ExhaustedComment);
    }
}

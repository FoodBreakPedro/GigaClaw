using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Plan 2.1 — the per-ticket spend ceiling (<see cref="AutomationConfig.MaxTicketCostUsd"/>) as
/// enforced by <see cref="RunStateManager.ShouldSkipAsync"/>. Exercised through a real
/// <see cref="TicketService"/> on a real project database, because the whole point of the cap is
/// that it reads the ticket's own durable <c>AgentCostUsd</c> column and writes its evidence back
/// onto the ticket — a mocked ticket store would prove none of that.
/// </summary>
public class TicketCostCapTests
{
    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required TicketService Tickets { get; init; }
        public required ProjectRuntime Runtime { get; init; }
        public required string Slug { get; init; }

        public RunStateManager Sut() =>
            new(new AgentRunRegistry(), new CostTracker(), Tickets, NullLogger.Instance);

        public void SetCap(decimal? cap) => Runtime.Config!.MaxTicketCostUsd = cap;

        public Task<bool> ShouldSkipAsync(int ticketId, string agent = "programmer") =>
            Sut().ShouldSkipAsync(
                Runtime,
                new RunAgentActionSpec { Agent = agent },
                new TriggerFiring(ticketId, null, null),
                agent,
                agent);

        public async Task<Ticket> ReloadAsync(int ticketId) =>
            (await Tickets.GetTicketAsync(Slug, ticketId))!;

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task<Harness> BuildAsync(decimal? cap)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("cost-cap-" + Guid.NewGuid().ToString("N")[..8]);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "programmer");
        await members.CreateMemberAsync(project.Slug, TicketCostCap.TriageAgent);
        var tickets = new TicketService(projects, members);

        return new Harness
        {
            Tmp = tmp,
            Tickets = tickets,
            Slug = project.Slug,
            Runtime = new ProjectRuntime(project.Slug)
            {
                Workspace = workspace,
                Config = new AutomationConfig { Automations = [], MaxTicketCostUsd = cap },
            },
        };
    }

    private static async Task<Ticket> SeedTicketAsync(Harness h, double spentUsd, string assignee = "programmer")
    {
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "expensive work", "a ticket that keeps asking for more", "owner",
            status: "Todo", assignedTo: assignee);
        if (spentUsd > 0)
            await h.Tickets.AddAgentUsageAsync(h.Slug, ticket.Id, tokens: 1_000, costUsd: spentUsd);
        return ticket;
    }

    private static int ReceiptCount(Ticket ticket) =>
        ticket.Comments.Count(c => c.Content.Contains(TicketCostCap.MarkerPrefix, StringComparison.Ordinal));

    // ── The cap fires ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_at_the_cap_is_skipped_with_one_receipt_and_handed_to_the_triage_agent()
    {
        using var h = await BuildAsync(cap: 5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 5.0);

        Assert.True(await h.ShouldSkipAsync(ticket.Id));

        var after = await h.ReloadAsync(ticket.Id);
        Assert.Equal(1, ReceiptCount(after));
        Assert.Equal(TicketCostCap.TriageAgent, after.AssignedTo);
    }

    [Fact]
    public async Task Receipt_names_the_cap_the_spend_and_the_skipped_agent()
    {
        using var h = await BuildAsync(cap: 2.5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 4.25);

        Assert.True(await h.ShouldSkipAsync(ticket.Id));

        var receipt = (await h.ReloadAsync(ticket.Id)).Comments
            .Single(c => c.Content.Contains(TicketCostCap.MarkerPrefix, StringComparison.Ordinal));
        Assert.Contains($"{TicketCostCap.MarkerPrefix} ticket-{ticket.Id} cap=2.5 spent=4.25", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("programmer", receipt.Content, StringComparison.Ordinal);
        Assert.Equal(TicketCostCap.ReceiptAuthor, receipt.Author);
    }

    // ── …once, and only once ──────────────────────────────────────────────────

    [Fact]
    public async Task Re_evaluating_a_breached_ticket_keeps_skipping_without_a_second_receipt()
    {
        using var h = await BuildAsync(cap: 5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 12.0);

        for (var tick = 0; tick < 4; tick++)
            Assert.True(await h.ShouldSkipAsync(ticket.Id));

        var after = await h.ReloadAsync(ticket.Id);
        Assert.Equal(1, ReceiptCount(after));
        Assert.Equal(TicketCostCap.TriageAgent, after.AssignedTo);
    }

    [Fact]
    public async Task Raising_the_cap_opens_a_new_episode_that_may_report_again()
    {
        using var h = await BuildAsync(cap: 5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 12.0);

        Assert.True(await h.ShouldSkipAsync(ticket.Id));
        Assert.Equal(1, ReceiptCount(await h.ReloadAsync(ticket.Id)));

        // Owner raises the ceiling but not far enough — a different cap, so a fresh episode.
        h.SetCap(10m);
        Assert.True(await h.ShouldSkipAsync(ticket.Id));
        Assert.Equal(2, ReceiptCount(await h.ReloadAsync(ticket.Id)));

        // …and the new episode is itself reported only once.
        Assert.True(await h.ShouldSkipAsync(ticket.Id));
        Assert.Equal(2, ReceiptCount(await h.ReloadAsync(ticket.Id)));
    }

    // ── No exemptions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_triage_agents_own_dispatch_is_skipped_too_so_the_breach_cannot_fund_a_loop()
    {
        using var h = await BuildAsync(cap: 5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 12.0, assignee: TicketCostCap.TriageAgent);

        Assert.True(await h.ShouldSkipAsync(ticket.Id, agent: TicketCostCap.TriageAgent));

        var after = await h.ReloadAsync(ticket.Id);
        Assert.Equal(1, ReceiptCount(after));
        // Already owned by triage — the hand-off is a no-op rather than a redundant re-assignment.
        Assert.Equal(TicketCostCap.TriageAgent, after.AssignedTo);
    }

    // ── Under the cap, and with no cap at all ─────────────────────────────────

    [Fact]
    public async Task Dispatch_below_the_cap_is_untouched()
    {
        using var h = await BuildAsync(cap: 5m);
        var ticket = await SeedTicketAsync(h, spentUsd: 4.99);

        Assert.False(await h.ShouldSkipAsync(ticket.Id));

        var after = await h.ReloadAsync(ticket.Id);
        Assert.Equal(0, ReceiptCount(after));
        Assert.Equal("programmer", after.AssignedTo);
    }

    [Fact]
    public async Task No_cap_configured_never_skips_however_expensive_the_ticket_got()
    {
        using var h = await BuildAsync(cap: null);
        var ticket = await SeedTicketAsync(h, spentUsd: 999.0);

        Assert.False(await h.ShouldSkipAsync(ticket.Id));
        Assert.Equal(0, ReceiptCount(await h.ReloadAsync(ticket.Id)));
    }

    /// <summary>
    /// Omitting the key is the only "off". A cap of zero is a decision an owner can reach through
    /// the UI's own number field, and it says "this ticket gets no further spend" — reading it as
    /// "unlimited" would turn the strictest setting available into the loosest one.
    /// </summary>
    [Fact]
    public async Task A_cap_of_zero_stops_every_dispatch_rather_than_disabling_the_gate()
    {
        using var h = await BuildAsync(cap: 0m);
        var ticket = await SeedTicketAsync(h, spentUsd: 0.0);

        Assert.True(await h.ShouldSkipAsync(ticket.Id));

        var after = await h.ReloadAsync(ticket.Id);
        Assert.Equal(1, ReceiptCount(after));
        Assert.Equal(TicketCostCap.TriageAgent, after.AssignedTo);
    }

    [Fact]
    public async Task A_firing_with_no_ticket_is_never_capped()
    {
        using var h = await BuildAsync(cap: 0.01m);

        var skip = await h.Sut().ShouldSkipAsync(
            h.Runtime,
            new RunAgentActionSpec { Agent = "programmer" },
            new TriggerFiring(null, null, null),
            "programmer",
            "programmer");

        Assert.False(skip);
    }

    // ── Receipt recount primitives ────────────────────────────────────────────

    [Fact]
    public void AlreadyReported_matches_only_the_same_ticket_and_the_same_cap()
    {
        var receipt = TicketCostCap.RenderReceipt(ticketId: 42, cap: 5m, spent: 6.5m, skippedAgent: "programmer");
        string?[] trail = [null, "unrelated chatter", receipt];

        Assert.True(TicketCostCap.AlreadyReported(trail, 42, 5m));
        Assert.True(TicketCostCap.AlreadyReported(trail, 42, 5.00m));   // scale must not matter
        Assert.False(TicketCostCap.AlreadyReported(trail, 42, 10m));    // cap moved → new episode
        Assert.False(TicketCostCap.AlreadyReported(trail, 43, 5m));     // another ticket's receipt
        Assert.False(TicketCostCap.AlreadyReported([], 42, 5m));
    }
}

using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Phase 3.1 (i-asked-gemini-to-swirling-hejlsberg.md): blocked-reason chip. Covers the pure
/// marker-parsing logic and its wiring into <see cref="TicketService.ListTicketsAsync"/>.
/// </summary>
public sealed class BlockedReasonTests
{
    private static async Task<(TicketService Tickets, string Slug)> BuildSutAsync(TempDir tmp, string name)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(name);
        var tickets = new TicketService(projects, new MemberService(projects));
        return (tickets, project.Slug);
    }

    [Theory]
    [InlineData(
        "GIGACLAW-GATE v1 ticket-42 blocked — the reviewer returned BLOCK.\n\nMore prose below.",
        "the reviewer returned BLOCK")]
    [InlineData(
        "GIGACLAW-GATE v1 ticket-7 blocked — qa-tester was asked again and still produced no usable verdict.\n\nDetails.",
        "qa-tester was asked again and still produced no usable verd…")]
    [InlineData(
        "GIGACLAW-REPAIR v1 ticket-9 escalated 2/2\n\nThe repair budget for this ticket is spent…",
        "Repair budget exhausted (2/2)")]
    public void ExtractBlockedReason_ParsesKnownMarkerShapes(string comment, string expected)
    {
        Assert.Equal(expected, TicketService.ExtractBlockedReason(comment));
    }

    [Fact]
    public void ExtractBlockedReason_FallsBackToTruncatedFirstLineOnDrift()
    {
        // Marker present but the "verb — reason" shape has drifted (no em-dash separator):
        // still recognizable as a gate receipt, so it degrades to the truncated first line
        // rather than throwing or silently dropping the chip.
        var comment = "GIGACLAW-GATE v1 ticket-3 something unexpected happened here with no dash";
        var reason = TicketService.ExtractBlockedReason(comment);
        Assert.NotNull(reason);
        Assert.True(reason!.Length <= 60);
    }

    [Fact]
    public void ExtractBlockedReason_ReturnsNullWhenNoMarkerPresent()
    {
        Assert.Null(TicketService.ExtractBlockedReason("Just a regular comment, nothing to see here."));
        Assert.Null(TicketService.ExtractBlockedReason(""));
    }

    [Fact]
    public async Task ListTicketsAsync_PopulatesBlockedReasonOnlyForBlockedTickets()
    {
        using var tmp = new TempDir();
        var (tickets, slug) = await BuildSutAsync(tmp, "blocked-reason-wiring");

        var blocked = await tickets.CreateTicketAsync(slug, "Needs a human", status: "Blocked");
        var inReview = await tickets.CreateTicketAsync(slug, "Still in review", status: "Todo");

        await tickets.AddCommentAsync(slug, blocked.Id, "some earlier chatter");
        await tickets.AddCommentAsync(
            slug,
            blocked.Id,
            "GIGACLAW-GATE v1 ticket-{0} blocked — qa-tester returned BLOCK.\n\nMore prose."
                .Replace("{0}", blocked.Id.ToString()));
        // A non-blocked ticket carrying the same marker (e.g. historical repair-round comment on a
        // ticket that has since moved on) must not surface a reason — only Blocked tickets do.
        await tickets.AddCommentAsync(
            slug,
            inReview.Id,
            "GIGACLAW-GATE v1 ticket-{0} repair round — returning the work to programmer."
                .Replace("{0}", inReview.Id.ToString()));

        var summaries = await tickets.ListTicketsAsync(slug);
        var blockedSummary = summaries.Single(t => t.Id == blocked.Id);
        var otherSummary = summaries.Single(t => t.Id == inReview.Id);

        Assert.Equal("qa-tester returned BLOCK", blockedSummary.BlockedReason);
        Assert.Null(otherSummary.BlockedReason);
    }

    [Fact]
    public async Task ListTicketsAsync_UsesNewestGateCommentWhenSeveralExist()
    {
        using var tmp = new TempDir();
        var (tickets, slug) = await BuildSutAsync(tmp, "blocked-reason-newest");
        var blocked = await tickets.CreateTicketAsync(slug, "Escalated twice", status: "Blocked");

        await tickets.AddCommentAsync(
            slug,
            blocked.Id,
            $"GIGACLAW-GATE v1 ticket-{blocked.Id} blocked — the reviewer returned BLOCK.");
        await tickets.AddCommentAsync(
            slug,
            blocked.Id,
            $"GIGACLAW-GATE v1 ticket-{blocked.Id} blocked — the reviewer was asked again and still produced no usable verdict.");

        var summaries = await tickets.ListTicketsAsync(slug);
        var summary = summaries.Single(t => t.Id == blocked.Id);

        Assert.StartsWith("the reviewer was asked again", summary.BlockedReason);
    }
}

using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

public sealed class TicketTransitionTests
{
    private static async Task<(TicketService Tickets, string Slug)> BuildSutAsync(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("transition-test");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Blog Writer");
        await members.CreateMemberAsync(project.Slug, "Blog Reviewer");
        return (new TicketService(projects, members), project.Slug);
    }

    [Fact]
    public async Task TransitionTicket_ChangesAssigneeAndStatusTogether()
    {
        using var tmp = new TempDir();
        var (tickets, slug) = await BuildSutAsync(tmp);
        var ticket = await tickets.CreateTicketAsync(slug, "Draft", status: "InProgress", assignedTo: "blog-writer");

        var transitioned = await tickets.TransitionTicketAsync(
            slug,
            ticket.Id,
            newStatus: "Todo",
            assignedTo: "blog-reviewer",
            author: "blog-writer",
            expectedStatus: "InProgress");

        Assert.NotNull(transitioned);
        Assert.Equal("Todo", transitioned.Status);
        Assert.Equal("blog-reviewer", transitioned.AssignedTo);
    }

    [Fact]
    public async Task TransitionTicket_RejectsStaleExpectedStatusWithoutPartialAssignment()
    {
        using var tmp = new TempDir();
        var (tickets, slug) = await BuildSutAsync(tmp);
        var ticket = await tickets.CreateTicketAsync(slug, "Draft", status: "Review", assignedTo: "blog-writer");

        await Assert.ThrowsAsync<TicketTransitionConflictException>(() =>
            tickets.TransitionTicketAsync(
                slug,
                ticket.Id,
                newStatus: "Todo",
                assignedTo: "blog-reviewer",
                author: "blog-writer",
                expectedStatus: "InProgress"));

        var unchanged = await tickets.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(unchanged);
        Assert.Equal("Review", unchanged.Status);
        Assert.Equal("blog-writer", unchanged.AssignedTo);
    }
}

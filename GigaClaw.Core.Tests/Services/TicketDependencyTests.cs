using GigaClaw.Core.Data;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace GigaClaw.Core.Tests.Services;

public sealed class TicketDependencyTests
{
    private static async Task<(ProjectService Projects, TicketService Tickets, string Slug)> BuildSutAsync(
        TempDir tmp,
        string name)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(name);
        var tickets = new TicketService(projects, new MemberService(projects));
        return (projects, tickets, project.Slug);
    }

    [Fact]
    public async Task DependencyCrud_ProjectsBlockedByAndBlocksOnDetailAndSummary()
    {
        using var tmp = new TempDir();
        var (_, tickets, slug) = await BuildSutAsync(tmp, "dependency-crud");
        var blocked = await tickets.CreateTicketAsync(slug, "Ship feature", status: "Todo");
        var blocker = await tickets.CreateTicketAsync(slug, "Finish design", status: "Done");

        var created = await tickets.AddTicketDependencyAsync(slug, blocked.Id, blocker.Id);

        Assert.Equal(blocker.Id, created.Id);
        var blockedDependencies = await tickets.GetTicketDependenciesAsync(slug, blocked.Id);
        Assert.NotNull(blockedDependencies);
        Assert.Equal(blocker.Id, Assert.Single(blockedDependencies.BlockedBy).Id);
        Assert.Empty(blockedDependencies.Blocks);

        var blockerDependencies = await tickets.GetTicketDependenciesAsync(slug, blocker.Id);
        Assert.NotNull(blockerDependencies);
        Assert.Equal(blocked.Id, Assert.Single(blockerDependencies.Blocks).Id);
        Assert.Empty(blockerDependencies.BlockedBy);

        var details = await tickets.GetTicketAsync(slug, blocked.Id);
        Assert.NotNull(details);
        Assert.Equal(blocker.Id, Assert.Single(details.BlockedBy).Id);

        var summaries = await tickets.ListTicketsAsync(slug, statusFilter: "Todo");
        var summary = Assert.Single(summaries);
        Assert.Equal(blocker.Id, Assert.Single(summary.BlockedBy).Id);
        Assert.Equal("Done", summary.BlockedBy[0].Status);

        ITicketDependencyQuery query = tickets;
        var blockers = await query.ListBlockingTicketsAsync(slug, blocked.Id);
        Assert.NotNull(blockers);
        Assert.Equal(blocker.Id, Assert.Single(blockers).Id);

        Assert.True(await tickets.RemoveTicketDependencyAsync(slug, blocked.Id, blocker.Id));
        Assert.False(await tickets.RemoveTicketDependencyAsync(slug, blocked.Id, blocker.Id));
        Assert.Empty((await tickets.GetTicketDependenciesAsync(slug, blocked.Id))!.BlockedBy);
    }

    [Fact]
    public async Task AddDependency_RejectsSelfDuplicateMissingAndCycles()
    {
        using var tmp = new TempDir();
        var (_, tickets, slug) = await BuildSutAsync(tmp, "dependency-validation");
        var a = await tickets.CreateTicketAsync(slug, "A");
        var b = await tickets.CreateTicketAsync(slug, "B");
        var c = await tickets.CreateTicketAsync(slug, "C");

        await AssertDependencyErrorAsync(
            "dependency_self",
            () => tickets.AddTicketDependencyAsync(slug, a.Id, a.Id));
        await AssertDependencyErrorAsync(
            "blocking_ticket_not_found",
            () => tickets.AddTicketDependencyAsync(slug, a.Id, 999_999));
        await AssertDependencyErrorAsync(
            "ticket_not_found",
            () => tickets.AddTicketDependencyAsync(slug, 999_999, a.Id));

        await tickets.AddTicketDependencyAsync(slug, a.Id, b.Id);
        await AssertDependencyErrorAsync(
            "dependency_duplicate",
            () => tickets.AddTicketDependencyAsync(slug, a.Id, b.Id));
        await AssertDependencyErrorAsync(
            "dependency_cycle",
            () => tickets.AddTicketDependencyAsync(slug, b.Id, a.Id));

        await tickets.AddTicketDependencyAsync(slug, b.Id, c.Id);
        await AssertDependencyErrorAsync(
            "dependency_cycle",
            () => tickets.AddTicketDependencyAsync(slug, c.Id, a.Id));
    }

    [Fact]
    public async Task DeleteTicket_RemovesIncomingAndOutgoingDependencyEdges()
    {
        using var tmp = new TempDir();
        var (projects, tickets, slug) = await BuildSutAsync(tmp, "dependency-delete");
        var middle = await tickets.CreateTicketAsync(slug, "Middle");
        var blocker = await tickets.CreateTicketAsync(slug, "Blocker");
        var blocked = await tickets.CreateTicketAsync(slug, "Blocked");
        await tickets.AddTicketDependencyAsync(slug, middle.Id, blocker.Id);
        await tickets.AddTicketDependencyAsync(slug, blocked.Id, middle.Id);

        Assert.True(await tickets.DeleteTicketAsync(slug, middle.Id));

        Assert.Empty((await tickets.GetTicketDependenciesAsync(slug, blocker.Id))!.Blocks);
        Assert.Empty((await tickets.GetTicketDependenciesAsync(slug, blocked.Id))!.BlockedBy);
        await using var db = projects.GetProjectDb(slug);
        Assert.Equal(0, await db.TicketDependencies.CountAsync());
    }

    [Fact]
    public async Task ExistingDatabase_MigratesDependencyTableWithoutTicketDataLoss()
    {
        using var tmp = new TempDir();
        var (projects, tickets, slug) = await BuildSutAsync(tmp, "dependency-migration");
        var ticket = await tickets.CreateTicketAsync(slug, "Keep me");

        await using (var legacy = projects.GetProjectDb(slug))
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE TicketDependencies");

        var dependencies = await tickets.GetTicketDependenciesAsync(slug, ticket.Id);

        Assert.NotNull(dependencies);
        await using var migrated = projects.GetProjectDb(slug);
        Assert.Equal("Keep me", (await migrated.Tickets.SingleAsync()).Title);
        Assert.Equal(0, await migrated.TicketDependencies.CountAsync());
        var indexes = await migrated.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'TicketDependencies'")
            .ToListAsync();
        Assert.Contains("IX_TicketDependencies_BlockingTicketId", indexes);
    }

    [Fact]
    public async Task ConcurrentOppositeEdges_CannotBothCommit()
    {
        using var tmp = new TempDir();
        var (_, tickets, slug) = await BuildSutAsync(tmp, "dependency-concurrency");
        var a = await tickets.CreateTicketAsync(slug, "A");
        var b = await tickets.CreateTicketAsync(slug, "B");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> AttemptAsync(int blockedId, int blockingId)
        {
            await release.Task;
            try
            {
                await tickets.AddTicketDependencyAsync(slug, blockedId, blockingId);
                return "success";
            }
            catch (TicketDependencyException exception)
            {
                return exception.Code;
            }
        }

        var first = Task.Run(() => AttemptAsync(a.Id, b.Id));
        var second = Task.Run(() => AttemptAsync(b.Id, a.Id));
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result == "success"));
        Assert.Equal(1, results.Count(result => result == "dependency_cycle"));
        var aDependencies = await tickets.GetTicketDependenciesAsync(slug, a.Id);
        var bDependencies = await tickets.GetTicketDependenciesAsync(slug, b.Id);
        Assert.Equal(1, aDependencies!.BlockedBy.Count + bDependencies!.BlockedBy.Count);
    }

    private static async Task AssertDependencyErrorAsync(
        string expectedCode,
        Func<Task<TicketDependencyInfo>> action)
    {
        var exception = await Assert.ThrowsAsync<TicketDependencyException>(action);
        Assert.Equal(expectedCode, exception.Code);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }
}

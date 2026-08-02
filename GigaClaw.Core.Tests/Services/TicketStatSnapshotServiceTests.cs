using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Plan 4.1 (i-asked-gemini-to-swirling-hejlsberg.md): daily ticket-stat snapshots — the write-once
/// -per-day guard, the delta and series reads, and the deliberately partial backfill.
/// </summary>
public sealed class TicketStatSnapshotServiceTests
{
    private static async Task<(TicketStatSnapshotService Snapshots, TicketService Tickets, string Slug)>
        BuildSutAsync(TempDir tmp, string name)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(name);
        var tickets = new TicketService(projects, new MemberService(projects));
        return (new TicketStatSnapshotService(projects), tickets, project.Slug);
    }

    /// <summary>A tick shortly after midnight — the only moment live counts still describe the day
    /// that just ended, and therefore the only moment a snapshot may be written. Tests that used a
    /// bare UtcNow would pass or fail depending on the hour the suite happened to run.</summary>
    private static DateTime JustAfterMidnight() => DateTime.UtcNow.Date.AddHours(1);

    [Fact]
    public async Task CaptureProjectAsync_WritesYesterdaysColumnCounts_ThenGuardsAgainstASecondWrite()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-guard");

        await tickets.CreateTicketAsync(slug, "A", status: "Backlog");
        await tickets.CreateTicketAsync(slug, "B", status: "Backlog");
        await tickets.CreateTicketAsync(slug, "C", status: "Review");

        var now = JustAfterMidnight();
        Assert.True(await snapshots.CaptureProjectAsync(slug, now));
        // Same day again: the row-set already exists, so nothing is rewritten.
        Assert.False(await snapshots.CaptureProjectAsync(slug, now));

        var yesterday = DateOnly.FromDateTime(now.Date.AddDays(-1));
        var counts = await snapshots.GetColumnCountsAsync(slug, yesterday);
        Assert.NotNull(counts);
        Assert.Equal(2, counts!.Counts["Backlog"]);
        Assert.Equal(1, counts.Counts["Review"]);
        // CapturedAt is the observation time, and it is now readable — that is what lets a consumer
        // re-check that this row really describes the day it is stamped with.
        Assert.True((counts.CapturedAtUtc - now).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetColumnCountsAsync_ExposesOnlyRealColumns_NeverTheReservedDayTotalsRow()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("snap-keys");
        var tickets = new TicketService(projects, new MemberService(projects));
        var snapshots = new TicketStatSnapshotService(projects);

        var now = JustAfterMidnight();
        var yesterday = DateOnly.FromDateTime(now.Date.AddDays(-1));

        // Backdating a ticket into the backfill window forces a reserved '*' day-totals row onto the
        // very day the delta baseline is read from — the one row that must never leak out as if it
        // were a board column named "*".
        var old = await tickets.CreateTicketAsync(project.Slug, "Old", status: "Todo");
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            var row = db.Tickets.Single(t => t.Id == old.Id);
            row.CreatedAt = yesterday.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
            row.UpdatedAt = row.CreatedAt;
            await db.SaveChangesAsync();
        }
        await tickets.CreateTicketAsync(project.Slug, "In review", status: "Review");

        Assert.True(await snapshots.CaptureProjectAsync(project.Slug, now));

        var counts = await snapshots.GetColumnCountsAsync(project.Slug, yesterday);
        Assert.NotNull(counts);
        Assert.Equal(
            ["Review", "Todo"],
            counts!.Counts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(TicketStatSnapshotService.DayTotalsColumn, counts.Counts.Keys);
    }

    [Fact]
    public async Task CaptureProjectAsync_WritesNothingWhenTheHostWasOffAcrossMidnight()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-host-off");
        await tickets.CreateTicketAsync(slug, "A", status: "Todo");

        // Laptop booted at 18:00 the next day. Live counts now describe today, not yesterday's
        // close; stamping them with yesterday's date would make every delta read as a calm ~0.
        var bootedLate = new DateTime(2026, 8, 2, 18, 0, 0, DateTimeKind.Utc);
        Assert.False(await snapshots.CaptureProjectAsync(slug, bootedLate));
        Assert.Null(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public async Task CaptureProjectAsync_LeavesAHoleForEveryDayTheHostMissed()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-gap");
        await tickets.CreateTicketAsync(slug, "A", status: "Todo");

        Assert.True(await snapshots.CaptureProjectAsync(
            slug, new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc)));

        // Host off for three days, then back shortly after midnight: only the night just passed can
        // still be described. The days in between stay absent rather than being invented.
        Assert.True(await snapshots.CaptureProjectAsync(
            slug, new DateTime(2026, 8, 6, 1, 0, 0, DateTimeKind.Utc)));

        Assert.NotNull(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 1)));
        Assert.Null(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 2)));
        Assert.Null(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 3)));
        Assert.Null(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 4)));
        Assert.NotNull(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 5)));
    }

    [Fact]
    public async Task CaptureProjectAsync_WritesAgainOnceTheDateRollsOver()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-rollover");
        await tickets.CreateTicketAsync(slug, "A", status: "Todo");

        // Continuous operation: a tick shortly after each midnight, which is the normal case.
        var day1 = new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc);
        Assert.True(await snapshots.CaptureProjectAsync(slug, day1));
        Assert.False(await snapshots.CaptureProjectAsync(slug, day1.AddHours(10)));
        Assert.True(await snapshots.CaptureProjectAsync(slug, day1.AddDays(1)));

        Assert.NotNull(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 1)));
        Assert.NotNull(await snapshots.GetColumnCountsAsync(slug, new DateOnly(2026, 8, 2)));
    }

    [Fact]
    public async Task GetColumnCountsAsync_ReturnsNullForADayNeverSnapshotted()
    {
        using var tmp = new TempDir();
        var (snapshots, _, slug) = await BuildSutAsync(tmp, "snap-no-baseline");

        // Day one: nothing has been captured, so the delta baseline is genuinely absent and the
        // page must be able to tell that apart from "yesterday there were zero tickets".
        Assert.Null(await snapshots.GetColumnCountsAsync(slug, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
    }

    [Fact]
    public async Task GetSeriesAsync_CoversEveryDayInTheWindow_AndLeavesBlockedNullWithoutASnapshot()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-series");
        await tickets.CreateTicketAsync(slug, "Created today", status: "Todo");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var series = await snapshots.GetSeriesAsync(slug, today.AddDays(-6), today);

        Assert.Equal(7, series.Count);
        Assert.Equal(today.AddDays(-6), series[0].Date);
        Assert.Equal(today, series[^1].Date);
        // Today was never snapshotted (a snapshot only lands after the day ends), so it is derived
        // live: the creation is visible, the blocked column count is not reconstructable.
        Assert.Equal(1, series[^1].Created);
        Assert.All(series, day => Assert.Null(day.Blocked));
    }

    [Fact]
    public async Task GetSeriesAsync_PrefersTheSnapshotOverTheLiveDerivation()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-series-frozen");
        await tickets.CreateTicketAsync(slug, "Blocked one", status: "Blocked");

        var now = JustAfterMidnight();
        await snapshots.CaptureProjectAsync(slug, now);

        var yesterday = DateOnly.FromDateTime(now.Date.AddDays(-1));
        var series = await snapshots.GetSeriesAsync(slug, yesterday, yesterday);

        var day = Assert.Single(series);
        Assert.Equal(1, day.Blocked); // only a snapshot can supply a past column count
    }

    [Fact]
    public async Task Backfill_SeedsCreationsPerDay_ButNeverInventsColumnHistory()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("snap-backfill");
        var tickets = new TicketService(projects, new MemberService(projects));
        var snapshots = new TicketStatSnapshotService(projects);

        var old = await tickets.CreateTicketAsync(project.Slug, "Old ticket", status: "Todo");
        // Rewrite CreatedAt/UpdatedAt to five days ago so the backfill has real history to find.
        var fiveDaysAgo = DateTime.UtcNow.Date.AddDays(-5).AddHours(9);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            var row = db.Tickets.Single(t => t.Id == old.Id);
            row.CreatedAt = fiveDaysAgo;
            row.UpdatedAt = fiveDaysAgo;
            await db.SaveChangesAsync();
        }

        await snapshots.CaptureProjectAsync(project.Slug, JustAfterMidnight());

        var backfilledDay = DateOnly.FromDateTime(fiveDaysAgo);
        var series = await snapshots.GetSeriesAsync(project.Slug, backfilledDay, backfilledDay);
        var day = Assert.Single(series);
        Assert.Equal(1, day.Created);
        // Honest backfill: the creation is derivable, where the ticket sat that day is not.
        Assert.Null(day.Blocked);
        Assert.Null(await snapshots.GetColumnCountsAsync(project.Slug, backfilledDay));
    }

    [Fact]
    public async Task GetFirstSnapshotDateAsync_IsNullBeforeAnythingIsCaptured()
    {
        using var tmp = new TempDir();
        var (snapshots, tickets, slug) = await BuildSutAsync(tmp, "snap-first-date");

        Assert.Null(await snapshots.GetFirstSnapshotDateAsync(slug));

        await tickets.CreateTicketAsync(slug, "A", status: "Todo");
        var now = new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc);
        await snapshots.CaptureProjectAsync(slug, now);

        Assert.Equal(new DateOnly(2026, 8, 1), await snapshots.GetFirstSnapshotDateAsync(slug));
    }

    [Fact]
    public async Task CaptureDueAsync_CoversEveryProjectIncludingPausedOnes()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var active = await projects.CreateProjectAsync("snap-active");
        var paused = await projects.CreateProjectAsync("snap-paused");
        await projects.TogglePauseAsync(paused.Slug);
        var tickets = new TicketService(projects, new MemberService(projects));
        await tickets.CreateTicketAsync(active.Slug, "A", status: "Todo");
        await tickets.CreateTicketAsync(paused.Slug, "B", status: "Todo");
        var snapshots = new TicketStatSnapshotService(projects);

        Assert.Equal(2, await snapshots.CaptureDueAsync(JustAfterMidnight()));

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        // A paused project keeps accruing history; pausing must not punch a permanent hole in it.
        Assert.NotNull(await snapshots.GetColumnCountsAsync(paused.Slug, yesterday));
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Services;

/// <summary>One day of ticket flow for one project. <see cref="Blocked"/> is null when that day has
/// no per-column snapshot (backfilled or pre-snapshot days): a column count cannot be reconstructed
/// after the fact, and guessing one would be a lie the velocity chart then draws as a line.</summary>
public sealed record DailyTicketStats(DateOnly Date, int Created, int Resolved, int? Blocked);

/// <summary>One day's per-column counts together with the moment they were actually observed.
/// <see cref="CapturedAtUtc"/> is the honesty check a consumer needs: a row stamped with day D is
/// only a faithful description of D's closing state if it was written shortly after D ended.</summary>
public sealed record ColumnCountSnapshot(IReadOnlyDictionary<string, int> Counts, DateTime CapturedAtUtc);

/// <summary>
/// Plan 4.1 — daily ticket statistics for Mission Control's vs-yesterday deltas and velocity chart.
///
/// <para>GigaClaw stores no ticket history: a board row only knows its <i>current</i> column, so
/// "how many tickets were in Review yesterday" is unanswerable from the tickets table alone. This
/// service closes that gap with one small per-project table, <c>ticket_stat_snapshots</c>, written
/// once per day.</para>
///
/// <para><b>Rows are stamped with the day they describe.</b> The row-set for day D is written on the
/// first tick after D ended — i.e. it records D's closing state. That is what makes
/// <see cref="GetColumnCountsAsync"/>(yesterday) exactly the baseline a "vs yesterday" delta needs.
/// <c>CapturedAt</c> records when the observation actually happened and is returned to callers on
/// <see cref="ColumnCountSnapshot"/>, so a consumer can re-check the same honesty rule the writer
/// applied.</para>
///
/// <para><b>A hole beats a lie.</b> The counts written are the counts read <i>now</i> — the tickets
/// table has no history. That is faithful only while "now" is close to the end of the day being
/// described. A laptop booted at 18:00 would otherwise stamp yesterday's row with a full extra day
/// of movement, and every delta computed against it would silently read ~0. So a day is written only
/// inside <see cref="CaptureGrace"/> of its end; outside it, nothing is written and the day stays
/// absent. <see cref="GetSeriesAsync"/> already renders holes honestly, and
/// <see cref="GetColumnCountsAsync"/> already returns null for a day never captured.</para>
///
/// <para><b>Backfill is deliberately partial.</b> Creations per day are derivable from
/// <c>Ticket.CreatedAt</c> and resolutions approximately from <c>UpdatedAt</c>, so both are seeded
/// for the last <see cref="BackfillDays"/> days under the reserved column key
/// <see cref="DayTotalsColumn"/>. Per-column history is not derivable and is left absent.</para>
///
/// <para><b>Why a hosted service and not the AutomationEngine tick.</b> The engine's tick is the
/// dispatch hot path and skips paused projects; snapshots must keep accruing for a paused project
/// or its history gets holes that can never be filled. This mirrors
/// <see cref="ScheduledPromotionService"/> — the repo's existing shape for "cheap periodic work over
/// every project" — and keeps the engine untouched.</para>
/// </summary>
public sealed class TicketStatSnapshotService : BackgroundService
{
    /// <summary>Reserved <c>Column</c> value for a row that carries only day totals (created/resolved)
    /// and no meaningful per-column count. Written by the backfill.</summary>
    public const string DayTotalsColumn = "*";

    /// <summary>Statuses counted as "resolved" for the day totals.</summary>
    private static readonly string[] ResolvedStatuses = ["Done"];

    private const int BackfillDays = 30;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long after a day ends its closing state may still be recorded from live counts.
    /// Generous enough to survive a restart, a paused laptop lid or a slow tick; far too short for a
    /// full working day of movement to sneak into the previous day's row.</summary>
    public static readonly TimeSpan CaptureGrace = TimeSpan.FromHours(3);

    private readonly ProjectService _projects;
    private readonly ILogger<TicketStatSnapshotService>? _logger;
    // Plan 4.2 hits this service three times per project per Mission Control render. The DDL is
    // idempotent but not free (a connection, a GetProjectDb EnsureCreated); one run per slug per
    // process is enough, and the table cannot vanish underneath a running host.
    private readonly HashSet<string> _tablesEnsured = new(StringComparer.OrdinalIgnoreCase);

    public TicketStatSnapshotService(ProjectService projects, ILogger<TicketStatSnapshotService>? logger = null)
    {
        _projects = projects;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("TicketStatSnapshotService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CaptureDueAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger?.LogError(ex, "TicketStatSnapshotService tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Writes the missing snapshot row-set for every project, if any. Paused projects are included
    /// on purpose — see the class remarks. Returns the number of projects whose history advanced.
    /// Exposed for deterministic testing (pass a fixed <paramref name="nowUtc"/>).
    /// </summary>
    internal async Task<int> CaptureDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var captured = 0;
        foreach (var project in await _projects.ListProjectsAsync())
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await CaptureProjectAsync(project.Slug, nowUtc, ct)) captured++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Snapshot capture failed for {Project}", project.Slug);
            }
        }
        return captured;
    }

    /// <summary>
    /// Captures the closing state of every day still missing a per-column row-set, from the day
    /// after the newest one already recorded through the day before <paramref name="nowUtc"/>,
    /// backfilling first if the table has never been written. Days whose end is further back than
    /// <see cref="CaptureGrace"/> are skipped rather than stamped with today's counts — a hole is
    /// the honest record of a host that was off. Returns true when anything was written.
    /// </summary>
    internal async Task<bool> CaptureProjectAsync(string projectSlug, DateTime nowUtc, CancellationToken ct = default)
    {
        await EnsureTableAsync(projectSlug, ct);
        await BackfillIfEmptyAsync(projectSlug, nowUtc, ct);

        var yesterday = DateOnly.FromDateTime(nowUtc.Date.AddDays(-1));
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);

        // Probe every day since the newest real row-set, not just yesterday: after a multi-day gap
        // the older days are still evaluated (and then, correctly, skipped by the grace rule) rather
        // than being unreachable forever. Backfilled day-total rows are ignored — they carry no
        // column counts to satisfy a delta.
        var newest = await NewestColumnSnapshotDateAsync(connection, ct);
        var oldestProbe = yesterday.AddDays(-(BackfillDays - 1));
        var from = newest is DateOnly last && last.AddDays(1) > oldestProbe ? last.AddDays(1) : oldestProbe;

        var wrote = false;
        for (var day = from; day <= yesterday; day = day.AddDays(1))
        {
            if (!IsWithinCaptureGrace(day, nowUtc)) continue;
            if (await HasColumnSnapshotAsync(connection, day, ct)) continue;

            var counts = await ReadColumnCountsAsync(connection, ct);
            var created = await CountByDayAsync(connection, "CreatedAt", day, ct);
            var resolved = await CountResolvedByDayAsync(connection, day, ct);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var (column, count) in counts)
                await UpsertAsync(connection, (SqliteTransaction)transaction, day, column, count, created, resolved, nowUtc, ct);
            await transaction.CommitAsync(ct);
            wrote = true;
        }
        return wrote;
    }

    /// <summary>True when live counts read at <paramref name="nowUtc"/> still describe
    /// <paramref name="day"/>'s closing state closely enough to record. Also the rule a consumer
    /// re-applies to a stored row via <see cref="ColumnCountSnapshot.CapturedAtUtc"/>.</summary>
    public static bool IsWithinCaptureGrace(DateOnly day, DateTime nowUtc)
    {
        var dayEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var observed = nowUtc.ToUniversalTime();
        return observed >= dayEnd && observed - dayEnd <= CaptureGrace;
    }

    /// <summary>
    /// Per-column ticket counts recorded for <paramref name="date"/>, or null when that day was never
    /// snapshotted. Null is the signal Mission Control uses to hide a delta rather than print a
    /// fabricated zero.
    /// </summary>
    public async Task<ColumnCountSnapshot?> GetColumnCountsAsync(
        string projectSlug, DateOnly date, CancellationToken ct = default)
    {
        await EnsureTableAsync(projectSlug, ct);
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "Column", "Count", CapturedAt FROM ticket_stat_snapshots
             WHERE Date = $d AND "Column" <> $day
            """;
        command.Parameters.AddWithValue("$d", Key(date));
        command.Parameters.AddWithValue("$day", DayTotalsColumn);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        DateTime? capturedAt = null;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
            // Every row of a day's set is written by one transaction, so any of them carries the
            // observation time; the newest wins if an older set was ever partially overwritten.
            var stamp = ParseUtc(reader.GetString(2));
            if (stamp is DateTime value && (capturedAt is null || value > capturedAt)) capturedAt = value;
        }
        return result.Count == 0 || capturedAt is null
            ? null
            : new ColumnCountSnapshot(result, capturedAt.Value);
    }

    /// <summary>
    /// Created / resolved / blocked per day over <c>[from, to]</c>. Snapshot rows win where they
    /// exist (they froze the number on the day it was true); days without one fall back to a live
    /// derivation from <c>CreatedAt</c>/<c>UpdatedAt</c>, which is honest for creations and
    /// approximate for resolutions — and leaves <see cref="DailyTicketStats.Blocked"/> null, since a
    /// past column count cannot be reconstructed at all.
    /// </summary>
    public async Task<IReadOnlyList<DailyTicketStats>> GetSeriesAsync(
        string projectSlug, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await EnsureTableAsync(projectSlug, ct);
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);

        var snapshots = new Dictionary<string, (int Created, int Resolved, int? Blocked)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Date,
                       MAX(CreatedToday),
                       MAX(ResolvedToday),
                       MAX(CASE WHEN "Column" = 'Blocked' THEN "Count" END)
                  FROM ticket_stat_snapshots
                 WHERE Date >= $from AND Date <= $to
                 GROUP BY Date
                """;
            command.Parameters.AddWithValue("$from", Key(from));
            command.Parameters.AddWithValue("$to", Key(to));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                snapshots[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3));
            }
        }

        var liveCreated = await CountByDayRangeAsync(connection, "CreatedAt", from, to, resolvedOnly: false, ct);
        var liveResolved = await CountByDayRangeAsync(connection, "UpdatedAt", from, to, resolvedOnly: true, ct);

        var series = new List<DailyTicketStats>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var key = Key(day);
            if (snapshots.TryGetValue(key, out var snapshot))
            {
                series.Add(new DailyTicketStats(day, snapshot.Created, snapshot.Resolved, snapshot.Blocked));
                continue;
            }
            series.Add(new DailyTicketStats(
                day,
                liveCreated.GetValueOrDefault(key),
                liveResolved.GetValueOrDefault(key),
                Blocked: null));
        }
        return series;
    }

    /// <summary>Earliest day this project has a snapshot for — the "collecting daily snapshots since
    /// …" date. Null when nothing has been captured yet.</summary>
    public async Task<DateOnly?> GetFirstSnapshotDateAsync(string projectSlug, CancellationToken ct = default)
    {
        await EnsureTableAsync(projectSlug, ct);
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(Date) FROM ticket_stat_snapshots";
        var value = await command.ExecuteScalarAsync(ct);
        return value is string text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    // ---------------------------------------------------------------- internals

    internal async Task EnsureTableAsync(string projectSlug, CancellationToken ct = default)
    {
        lock (_tablesEnsured)
        {
            if (_tablesEnsured.Contains(projectSlug)) return;
        }

        // The project db file has to exist and carry the Tickets schema before this table is added.
        // GetProjectDb runs the one-time EnsureCreated; disposing it immediately is fine, the file stays.
        _projects.GetProjectDb(projectSlug).Dispose();
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ticket_stat_snapshots (
                Date TEXT NOT NULL,
                "Column" TEXT NOT NULL,
                "Count" INTEGER NOT NULL DEFAULT 0,
                CreatedToday INTEGER NOT NULL DEFAULT 0,
                ResolvedToday INTEGER NOT NULL DEFAULT 0,
                CapturedAt TEXT NOT NULL,
                PRIMARY KEY (Date, "Column")
            );
            CREATE INDEX IF NOT EXISTS IX_ticket_stat_snapshots_Date
                ON ticket_stat_snapshots(Date);
            """;
        await command.ExecuteNonQueryAsync(ct);

        lock (_tablesEnsured) _tablesEnsured.Add(projectSlug);
    }

    /// <summary>Newest day that carries a real per-column row-set. Backfilled day-total rows are
    /// excluded: they say nothing about where tickets sat, so they must not make a day look
    /// captured.</summary>
    private static async Task<DateOnly?> NewestColumnSnapshotDateAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT MAX(Date) FROM ticket_stat_snapshots WHERE "Column" <> $day""";
        command.Parameters.AddWithValue("$day", DayTotalsColumn);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string text && DateOnly.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static async Task<bool> HasColumnSnapshotAsync(
        SqliteConnection connection, DateOnly day, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT COUNT(*) FROM ticket_stat_snapshots WHERE Date = $d AND "Column" <> $day""";
        command.Parameters.AddWithValue("$d", Key(day));
        command.Parameters.AddWithValue("$day", DayTotalsColumn);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    private static DateTime? ParseUtc(string raw) =>
        DateTime.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private async Task BackfillIfEmptyAsync(string projectSlug, DateTime nowUtc, CancellationToken ct)
    {
        await using var connection = Open(projectSlug);
        await connection.OpenAsync(ct);
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM ticket_stat_snapshots";
            if (Convert.ToInt32(await probe.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0) return;
        }

        var to = DateOnly.FromDateTime(nowUtc.Date.AddDays(-1));
        var from = to.AddDays(-(BackfillDays - 1));
        var created = await CountByDayRangeAsync(connection, "CreatedAt", from, to, resolvedOnly: false, ct);
        var resolved = await CountByDayRangeAsync(connection, "UpdatedAt", from, to, resolvedOnly: true, ct);
        if (created.Count == 0 && resolved.Count == 0) return;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var key = Key(day);
            var c = created.GetValueOrDefault(key);
            var r = resolved.GetValueOrDefault(key);
            if (c == 0 && r == 0) continue;
            // Column counts are NOT backfilled: nothing in the tickets table records where a ticket
            // sat on a past day. Only what is derivable is written.
            await UpsertAsync(connection, (SqliteTransaction)transaction, day, DayTotalsColumn, 0, c, r, nowUtc, ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly day,
        string column,
        int count,
        int created,
        int resolved,
        DateTime capturedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ticket_stat_snapshots (Date, "Column", "Count", CreatedToday, ResolvedToday, CapturedAt)
            VALUES ($date, $column, $count, $created, $resolved, $capturedAt)
            ON CONFLICT(Date, "Column") DO UPDATE SET
                "Count" = excluded."Count",
                CreatedToday = excluded.CreatedToday,
                ResolvedToday = excluded.ResolvedToday,
                CapturedAt = excluded.CapturedAt
            """;
        command.Parameters.AddWithValue("$date", Key(day));
        command.Parameters.AddWithValue("$column", column);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$resolved", resolved);
        command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<string, int>> ReadColumnCountsAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, COUNT(*) FROM Tickets GROUP BY Status";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static async Task<int> CountByDayAsync(
        SqliteConnection connection, string field, DateOnly day, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM Tickets WHERE substr({field}, 1, 10) = $d";
        command.Parameters.AddWithValue("$d", Key(day));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountResolvedByDayAsync(
        SqliteConnection connection, DateOnly day, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM Tickets WHERE Status = $done AND substr(UpdatedAt, 1, 10) = $d";
        command.Parameters.AddWithValue("$done", ResolvedStatuses[0]);
        command.Parameters.AddWithValue("$d", Key(day));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, int>> CountByDayRangeAsync(
        SqliteConnection connection,
        string field,
        DateOnly from,
        DateOnly to,
        bool resolvedOnly,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var filter = resolvedOnly ? " AND Status = $done" : "";
        command.CommandText =
            $"SELECT substr({field}, 1, 10) AS d, COUNT(*) FROM Tickets " +
            $"WHERE d >= $from AND d <= $to{filter} GROUP BY d";
        command.Parameters.AddWithValue("$from", Key(from));
        command.Parameters.AddWithValue("$to", Key(to));
        if (resolvedOnly) command.Parameters.AddWithValue("$done", ResolvedStatuses[0]);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    private SqliteConnection Open(string projectSlug) =>
        new($"Data Source={_projects.GetProjectDbPath(projectSlug)}");

    private static string Key(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

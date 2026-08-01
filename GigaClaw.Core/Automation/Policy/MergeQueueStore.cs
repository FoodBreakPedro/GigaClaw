using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation.Policy;

/*
 * R6 — Merge queue + integration gate (doc/roadmap/lane-codex-runtime.md, Task R6).
 *
 * A durable, ordered, per-project queue of R5 worktree branches waiting to land, owned by the
 * `committer` role's `enqueueMerge` automation action (ActionExecutor.ExecuteEnqueueMergeActionAsync)
 * and drained one entry at a time by `MergeQueueProcessor` (a hosted BackgroundService, the same
 * shape as `FileLeaseReaper`). One SQLite table per project DB, alongside `file_leases` — a raw
 * `SqliteConnection` against `ProjectService.GetProjectDbPath`, not the EF `TodoDbContext`.
 *
 * Every state transition happens inside a `BEGIN IMMEDIATE` transaction so a second connection
 * racing the same project's queue either sees the first's committed row or blocks (bounded by the
 * busy_timeout set on every connection) — the same concurrency-safety idiom FileLeaseStore uses for
 * its acquire/reap race.
 */

public enum MergeQueueState { Queued, Held, Merging, Merged, Bounced }

/// <summary>One candidate on a project's durable merge queue.</summary>
public sealed record MergeQueueEntry(
    long Id,
    string Slug,
    int TicketId,
    string Branch,
    string? IntegrationCommand,
    MergeQueueState State,
    string? Reason,
    DateTime EnqueuedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Result of <see cref="MergeQueueStore.EnqueueAsync"/>. <see cref="IsNew"/> is false when the
/// ticket already had an active (queued/held/merging) entry — enqueue is idempotent per ticket, the
/// same "a retried dispatch must not be told it conflicts with itself" shape as
/// <see cref="FileLeaseStore.AcquireAsync"/>'s idempotent re-acquire.
/// </summary>
public sealed record MergeEnqueueResult(MergeQueueEntry Entry, bool IsNew);

/// <summary>
/// Durable SQLite queue store for R6 merge candidates. See the file-level remarks for the
/// concurrency model, which mirrors <see cref="FileLeaseStore"/> exactly.
/// </summary>
public sealed class MergeQueueStore
{
    private const string SelectColumns =
        "SELECT Id, TicketId, Branch, IntegrationCommand, State, Reason, EnqueuedAtUtc, UpdatedAtUtc FROM merge_queue";

    private static readonly string[] ActiveStates = ["queued", "held", "merging"];

    private readonly ProjectService _projects;

    public MergeQueueStore(ProjectService projects)
    {
        _projects = projects;
    }

    /// <summary>
    /// Enqueues the ticket's worktree branch. Idempotent per (slug, ticketId): a ticket that
    /// already has an active entry returns it unchanged rather than enqueuing a duplicate, so a
    /// repeating <c>ticketInColumn</c> trigger firing <c>enqueueMerge</c> again is a no-op. A fresh
    /// entry starts <c>Held</c> unless <paramref name="approved"/> — see <see cref="MergeApprovalGate"/>.
    /// </summary>
    public async Task<MergeEnqueueResult> EnqueueAsync(
        string slug,
        int ticketId,
        string branch,
        string? integrationCommand,
        bool approved,
        DateTime now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        await using var conn = await OpenReadyAsync(slug, ct);
        await ExecAsync(conn, "BEGIN IMMEDIATE;", ct);
        try
        {
            var existing = await ReadActiveForTicketAsync(conn, slug, ticketId, ct);
            if (existing is not null)
            {
                await ExecAsync(conn, "COMMIT;", ct);
                return new MergeEnqueueResult(existing, IsNew: false);
            }

            var state = approved ? MergeQueueState.Queued : MergeQueueState.Held;
            var id = await InsertAsync(conn, ticketId, branch, integrationCommand, state, now, ct);
            var entry = new MergeQueueEntry(id, slug, ticketId, branch, integrationCommand, state, null, now, now);
            await ExecAsync(conn, "COMMIT;", ct);
            return new MergeEnqueueResult(entry, IsNew: true);
        }
        catch
        {
            await TryExecAsync(conn, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>
    /// Atomically: (1) recovers any entry stuck <c>Merging</c> back to <c>Queued</c> — the crash-
    /// resume path that makes "queue state survives engine restart" true without a separate
    /// heartbeat/reaper, since a merge attempt is fully synchronous within one
    /// <see cref="Services.MergeQueueProcessor"/> call and never itself persists a partial state;
    /// (2) promotes every <c>Held</c> entry to <c>Queued</c> when <paramref name="approved"/> — the
    /// hot-reload path: an owner flipping approval on takes effect on the very next poll with no
    /// re-enqueue; (3) claims the oldest <c>Queued</c> entry (FIFO by <c>EnqueuedAtUtc</c>, ties
    /// broken by <c>Id</c>) by marking it <c>Merging</c> and returning it, or null when the queue is
    /// empty. This is what makes the queue itself the serializer (design requirement 6): only one
    /// entry per project is ever <c>Merging</c> at a time, and a caller that never completes it
    /// (crash) yields it back to the next claim rather than blocking the project's queue forever.
    /// </summary>
    public async Task<MergeQueueEntry?> ClaimNextAsync(
        string slug, bool approved, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await OpenReadyAsync(slug, ct);
        await ExecAsync(conn, "BEGIN IMMEDIATE;", ct);
        try
        {
            await UpdateStateAsync(conn, "merging", "queued", now, ct);
            if (approved)
                await UpdateStateAsync(conn, "held", "queued", now, ct);

            MergeQueueEntry? claimed = null;
            using (var select = conn.CreateCommand())
            {
                select.CommandText = SelectColumns + " WHERE State = 'queued' ORDER BY EnqueuedAtUtc ASC, Id ASC LIMIT 1";
                await using var reader = await select.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) claimed = ReadEntry(reader, slug);
            }

            if (claimed is null)
            {
                await ExecAsync(conn, "COMMIT;", ct);
                return null;
            }

            using (var update = conn.CreateCommand())
            {
                update.CommandText = "UPDATE merge_queue SET State = 'merging', UpdatedAtUtc = @now WHERE Id = @id";
                update.Parameters.AddWithValue("@now", Iso(now));
                update.Parameters.AddWithValue("@id", claimed.Id);
                await update.ExecuteNonQueryAsync(ct);
            }
            await ExecAsync(conn, "COMMIT;", ct);
            return claimed with { State = MergeQueueState.Merging, UpdatedAtUtc = now };
        }
        catch
        {
            await TryExecAsync(conn, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>Records the final outcome of a claimed entry — <see cref="MergeQueueState.Merged"/>
    /// or <see cref="MergeQueueState.Bounced"/>. <paramref name="reason"/> is the human-readable
    /// bounce cause (null for a successful merge).</summary>
    public async Task CompleteAsync(
        string slug, long id, MergeQueueState finalState, string? reason, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await OpenReadyAsync(slug, ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE merge_queue SET State = @state, Reason = @reason, UpdatedAtUtc = @now WHERE Id = @id";
        cmd.Parameters.AddWithValue("@state", ToDb(finalState));
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", Iso(now));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns a claimed entry to <see cref="MergeQueueState.Held"/> with the reason it is waiting —
    /// the SP-3 F1 hold, as distinct from <see cref="CompleteAsync"/>'s terminal bounce. A held entry
    /// is picked up again by the very next <see cref="ClaimNextAsync"/> on an approved project (the
    /// same held→queued promotion the owner-approval hot-reload path uses), so a hold is a retry, not
    /// a park: nothing has to re-enqueue it and no timer has to remember it.
    /// <para>
    /// <paramref name="reason"/> is durable and is the dedup key for the hold's receipt — an
    /// identical reason on a later pass means "still the same hold", which is why the receipt is
    /// written once per reason rather than once per pass, and why that survives a restart for free.
    /// </para>
    /// </summary>
    public async Task HoldAsync(string slug, long id, string reason, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await OpenReadyAsync(slug, ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE merge_queue SET State = 'held', Reason = @reason, UpdatedAtUtc = @now WHERE Id = @id";
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.Parameters.AddWithValue("@now", Iso(now));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>All entries for a project, oldest first — for tests and any future queue UI.</summary>
    public async Task<IReadOnlyList<MergeQueueEntry>> ListAsync(string slug, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return [];

        await using var conn = await OpenReadyAsync(slug, ct);
        var result = new List<MergeQueueEntry>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " ORDER BY EnqueuedAtUtc ASC, Id ASC";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadEntry(reader, slug));
        return result;
    }

    // ── Connection / schema ─────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenReadyAsync(string slug, CancellationToken ct)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;", ct);
        EnsureTable(conn);
        return conn;
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS merge_queue (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    Branch TEXT NOT NULL,
                    IntegrationCommand TEXT NULL,
                    State TEXT NOT NULL,
                    Reason TEXT NULL,
                    EnqueuedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
        }
        // Repo convention (TriggerStateStore, FileLeaseStore): CREATE TABLE IF NOT EXISTS covers a
        // fresh install; ALTER TABLE ADD COLUMN wrapped in try/catch is the hook for a column added
        // after merge_queue already shipped. Nothing to migrate yet — R6 is new.
        using (var index1 = conn.CreateCommand())
        {
            index1.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_merge_queue_state ON merge_queue (State, EnqueuedAtUtc)";
            index1.ExecuteNonQuery();
        }
        using (var index2 = conn.CreateCommand())
        {
            // Partial unique index: at most one ACTIVE (queued/held/merging) entry per ticket. This
            // is what makes EnqueueAsync's idempotent re-enqueue correct rather than merely
            // convenient — two concurrent enqueues for the same ticket cannot both insert.
            index2.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_merge_queue_ticket_active ON merge_queue (TicketId) " +
                "WHERE State IN ('queued', 'held', 'merging')";
            index2.ExecuteNonQuery();
        }
    }

    // ── Row helpers ──────────────────────────────────────────────────────────

    private static async Task<MergeQueueEntry?> ReadActiveForTicketAsync(
        SqliteConnection conn, string slug, int ticketId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE TicketId = @ticket AND State IN ('queued', 'held', 'merging')";
        cmd.Parameters.AddWithValue("@ticket", ticketId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader, slug) : null;
    }

    private static async Task UpdateStateAsync(
        SqliteConnection conn, string fromState, string toState, DateTime now, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE merge_queue SET State = @to, UpdatedAtUtc = @now WHERE State = @from";
        cmd.Parameters.AddWithValue("@to", toState);
        cmd.Parameters.AddWithValue("@from", fromState);
        cmd.Parameters.AddWithValue("@now", Iso(now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> InsertAsync(
        SqliteConnection conn, int ticketId, string branch, string? integrationCommand,
        MergeQueueState state, DateTime now, CancellationToken ct)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO merge_queue (TicketId, Branch, IntegrationCommand, State, Reason, EnqueuedAtUtc, UpdatedAtUtc)
                VALUES (@ticket, @branch, @cmd, @state, NULL, @now, @now)
                """;
            cmd.Parameters.AddWithValue("@ticket", ticketId);
            cmd.Parameters.AddWithValue("@branch", branch);
            cmd.Parameters.AddWithValue("@cmd", (object?)integrationCommand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@state", ToDb(state));
            cmd.Parameters.AddWithValue("@now", Iso(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var idCmd = conn.CreateCommand())
        {
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var result = await idCmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result);
        }
    }

    private static MergeQueueEntry ReadEntry(SqliteDataReader reader, string slug) => new(
        reader.GetInt64(0),
        slug,
        reader.GetInt32(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        FromDb(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        ParseIso(reader.GetString(6)),
        ParseIso(reader.GetString(7)));

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TryExecAsync(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort rollback on an already-broken connection */ }
    }

    private static string ToDb(MergeQueueState state) => state switch
    {
        MergeQueueState.Queued => "queued",
        MergeQueueState.Held => "held",
        MergeQueueState.Merging => "merging",
        MergeQueueState.Merged => "merged",
        MergeQueueState.Bounced => "bounced",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static MergeQueueState FromDb(string state) => state switch
    {
        "queued" => MergeQueueState.Queued,
        "held" => MergeQueueState.Held,
        "merging" => MergeQueueState.Merging,
        "merged" => MergeQueueState.Merged,
        "bounced" => MergeQueueState.Bounced,
        _ => throw new InvalidOperationException($"Unknown merge_queue.State value '{state}'."),
    };

    private static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("O");

    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>Structured JSON receipts for R6, mirroring the policy-violation/v1, outbound-denial/v1,
/// file-lease-denial/v1 and worktree-cleanup-blocked/v1 idiom: every denial/hold/completion the
/// merge queue produces is a queryable ticket comment naming ticket, branch, and cause.</summary>
internal static class MergeReceipts
{
    public static string Held(int ticketId, string branch) => JsonSerializer.Serialize(new
    {
        schema = "merge-held/v1",
        ticketId,
        branch,
        rule = "merge-approval",
        reason = "No trusted owner approval for this project's merge queue — the entry is held. " +
                 "Approve the project in the owner's settings to let it merge.",
    });

    /// <summary>
    /// SP-3 F1: the merge is held because a run other than the branch's author holds a live R4 lease
    /// over paths this merge would rewrite. Same <c>merge-held/v1</c> family as the approval hold —
    /// a hold is a hold, whatever gate produced it — distinguished by <c>rule</c>, and naming the
    /// lease precisely enough that an operator can see whose run to wait for.
    /// </summary>
    public static string HeldForFileLease(
        int ticketId, string branch, FileLease conflict, IReadOnlyList<string> overlappingFiles) =>
        JsonSerializer.Serialize(new
        {
            schema = "merge-held/v1",
            ticketId,
            branch,
            rule = "file-lease-interlock",
            conflictingLeaseId = conflict.LeaseId,
            conflictingRunId = conflict.RunId,
            conflictingAgent = conflict.Agent,
            conflictingTicketId = conflict.TicketId,
            conflictingScope = conflict.Scope,
            overlappingFiles,
            reason = $"This merge would rewrite {overlappingFiles.Count} file(s) covered by an active " +
                     $"file lease held by '{conflict.Agent}' (run {conflict.RunId}) on ticket #{conflict.TicketId}. " +
                     "The merge is held and retried on later polls — it lands once that lease is released or expires.",
        });

    /// <summary>
    /// SP-3 F1, fail-closed: git could not tell the queue which files the branch would rewrite, so
    /// the interlock cannot prove the merge is clear of every live lease and holds instead of
    /// guessing. Unlike a bounce this is retried — a transient git fault must not cost the candidate
    /// its place in the queue.
    /// </summary>
    public static string HeldForUnknownDiff(int ticketId, string branch, string? error) => JsonSerializer.Serialize(new
    {
        schema = "merge-held/v1",
        ticketId,
        branch,
        rule = "file-lease-interlock",
        reason = "The queue could not compute which files this branch would rewrite, so it cannot rule out " +
                 "an overlap with an active file lease. The merge is held and retried on later polls.",
        error,
    });

    public static string Bounced(
        int ticketId, string? branch, string cause, string reason,
        IReadOnlyList<string>? conflictingFiles = null, string? outputExcerpt = null) => JsonSerializer.Serialize(new
    {
        schema = "merge-bounced/v1",
        ticketId,
        branch,
        cause,
        reason,
        conflictingFiles,
        outputExcerpt,
    });

    public static string Completed(int ticketId, string branch, bool integrationRan) => JsonSerializer.Serialize(new
    {
        schema = "merge-completed/v1",
        ticketId,
        branch,
        integration = integrationRan ? "passed" : "skipped",
    });
}

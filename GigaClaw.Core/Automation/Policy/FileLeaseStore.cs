using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation.Policy;

/*
 * R4 — File-ownership leases for parallel runs (doc/roadmap/lane-codex-runtime.md, Task R4).
 *
 * When an automation dispatches an agent against a ticket, the ticket's declared file scope is
 * leased in a durable SQLite table (one per project, alongside TriggerStateStore's
 * automation_trigger_state table). A second concurrent dispatch whose scope intersects an active
 * lease does not proceed concurrently with it — see ActionExecutor.TryAcquireDispatchLeaseAsync,
 * which is the only place this store is consulted.
 *
 * This is a *durable* lease store, not a wrapper around the in-memory concurrency-group lock that
 * ClaudeRunner/AgentRunRegistry already maintain: that lock lives in a ConcurrentDictionary and is
 * gone the instant the process restarts, which is fine for the thing it protects (one claude
 * subprocess at a time per concurrency group, on this process). A file lease protects an
 * eventual-merge invariant across runs that may span a restart, so it has to outlive the process —
 * hence its own SQLite table and its own reaper (FileLeaseReaper), independent of
 * ConcurrencyLockReaper.
 */

/// <summary>A durable file-ownership lease held by one dispatched run against one ticket.</summary>
public sealed record FileLease(
    string LeaseId,
    string Slug,
    int TicketId,
    string RunId,
    string Agent,
    IReadOnlyList<string> Scope,
    DateTime AcquiredAtUtc,
    DateTime HeartbeatAtUtc,
    DateTime ExpiresAtUtc);

public enum FileLeaseAcquireOutcome { Acquired, Conflict }

/// <summary>
/// Result of <see cref="FileLeaseStore.AcquireAsync"/>. <see cref="ConflictingLease"/> is populated
/// only on <see cref="FileLeaseAcquireOutcome.Conflict"/> and names the lease that blocked
/// acquisition, so a caller can write a receipt that explains *why* without a second query.
/// </summary>
public sealed record FileLeaseAcquireResult(
    FileLeaseAcquireOutcome Outcome,
    FileLease? Lease,
    FileLease? ConflictingLease)
{
    public bool IsAcquired => Outcome == FileLeaseAcquireOutcome.Acquired;

    internal static FileLeaseAcquireResult Acquired(FileLease lease) =>
        new(FileLeaseAcquireOutcome.Acquired, lease, null);

    internal static FileLeaseAcquireResult Conflict(FileLease conflicting) =>
        new(FileLeaseAcquireOutcome.Conflict, null, conflicting);
}

/// <summary>
/// Sound, conservative approximation of glob-set intersection for file-ownership leases.
/// <para>
/// Exact glob-vs-glob intersection is undecidable in general for arbitrary wildcard patterns (R4's
/// "Validated implementation boundaries" note in doc/roadmap/lane-codex-runtime.md is explicit that
/// this store must not pretend otherwise): deciding whether some concrete path could match both of
/// two patterns would require reasoning about the full set each pattern can generate, for
/// arbitrarily nested <c>**</c>/<c>*</c>/<c>?</c>/character-class combinations. Rather than attempt
/// that, two globs are compared by their <b>literal prefix</b> — the substring before the first
/// wildcard character (<c>*</c>, <c>?</c>, <c>[</c>) — after the same negation/backslash/leading-
/// slash normalization <see cref="GitIgnoreGlobSet"/> applies before it compiles a glob into a
/// path-matching regex (kept as an independent, much smaller routine here rather than reused
/// verbatim, because this compares two <i>patterns</i> to each other; <see cref="GitIgnoreGlobSet"/>
/// exists to compare one compiled pattern to a <i>concrete path</i>, which is a different
/// question with no shared implementation to call into).
/// </para>
/// <para>
/// <b>The rule:</b> two globs are declared to intersect unless their literal prefixes diverge
/// before either one reaches a wildcard.
/// </para>
/// <para>
/// <b>Documented false-positive class:</b> this is deliberately over-inclusive. Two globs that
/// only ever differ <i>after</i> a wildcard — <c>src/*.cs</c> vs <c>src/*.md</c>, or
/// <c>src/**</c> vs <c>src/api/**</c> — are treated as intersecting even when, for a specific
/// pair, no concrete path could actually match both. That costs an unnecessary serialization. It
/// never produces a false negative: a literal-prefix divergence found before any wildcard in
/// either pattern is real, and no wildcard appearing later in either pattern can undo a difference
/// that already occurred earlier in the comparison. That asymmetry — over-serialize, never
/// under-lease — is the safe direction for a lock: a spurious conflict costs a retry; a missed one
/// corrupts a file two agents wrote at once.
/// </para>
/// </summary>
internal static class GlobIntersection
{
    public static bool MayIntersect(
        IReadOnlyList<string> scopeA,
        IReadOnlyList<string> scopeB,
        PathCaseSensitivity caseSensitivity)
    {
        foreach (var a in scopeA)
            foreach (var b in scopeB)
                if (MayIntersect(a, b, caseSensitivity))
                    return true;
        return false;
    }

    public static bool MayIntersect(string globA, string globB, PathCaseSensitivity caseSensitivity)
    {
        var comparison = caseSensitivity == PathCaseSensitivity.Insensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var prefixA = LiteralPrefix(Normalize(globA));
        var prefixB = LiteralPrefix(Normalize(globB));
        var shorter = Math.Min(prefixA.Length, prefixB.Length);
        return string.Equals(prefixA[..shorter], prefixB[..shorter], comparison);
    }

    // Mirrors GlobRule.Parse's negation/backslash/leading-slash handling in ContractPolicy.cs, so
    // `!src/**` and `src/**` — and `src\api\**` and `src/api/**` — compare identically here.
    private static string Normalize(string rawPattern)
    {
        var pattern = rawPattern.StartsWith('!') ? rawPattern[1..] : rawPattern;
        pattern = pattern.Replace('\\', '/');
        return pattern.StartsWith('/') ? pattern[1..] : pattern;
    }

    private static readonly char[] WildcardChars = ['*', '?', '['];

    private static string LiteralPrefix(string pattern)
    {
        var index = pattern.IndexOfAny(WildcardChars);
        return index < 0 ? pattern : pattern[..index];
    }
}

/// <summary>
/// Resolves the file scope a dispatch should lease (R4). Primary source is the newest readable
/// handoff's <c>ownedFiles</c> — "the interface R4 leases consume" per doc/handoff-contract.md and
/// doc/roadmap/index.md. Falls back to the dispatched agent's own <c>allowedWriteGlobs</c> from
/// contracts.json (loaded through the real <see cref="ContractPolicyLoader"/>, not a re-parse) when
/// the ticket has no handoff artifact yet — a first dispatch on a ticket still gets a lease instead
/// of leasing nothing.
/// </summary>
internal static class FileLeaseScopeResolver
{
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        string workspacePath,
        IReadOnlyList<string> ticketCommentBodiesOldestFirst,
        string agentName,
        CancellationToken ct)
    {
        var handoff = HandoffReader.Latest(ticketCommentBodiesOldestFirst);
        if (handoff is not null && handoff.OwnedFiles.Count > 0)
            return handoff.OwnedFiles;

        if (string.IsNullOrWhiteSpace(workspacePath))
            return Array.Empty<string>();

        var policy = await ContractPolicyLoader.LoadAsync(workspacePath, agentName, ct);
        return policy.IsValid ? policy.AllowedWriteGlobs : Array.Empty<string>();
    }
}

/// <summary>
/// Durable SQLite lease store for R4 file-ownership leases, one table per project DB (same
/// pattern as <see cref="TriggerStateStore"/>'s <c>automation_trigger_state</c>: a raw
/// <see cref="SqliteConnection"/> against <see cref="ProjectService.GetProjectDbPath"/>, not the
/// EF <c>TodoDbContext</c>). Acquire is atomic under a <c>BEGIN IMMEDIATE</c> transaction: expiry
/// reaping, the intersection scan, and the insert all happen inside one write-locked transaction,
/// so a second connection racing the same acquire either sees the first's committed row or blocks
/// (bounded by the busy_timeout set on every connection) until it commits or rolls back — there is
/// no window where two connections can both pass the intersection check for overlapping scope.
/// <para>
/// <b>Keyed on logical scope, not on checkout path.</b> A <see cref="FileLease"/> names a glob
/// scope (<see cref="FileLease.Scope"/>) and a run/ticket/agent — nothing here is, or should ever
/// become, a path into a particular git worktree. There is no worktree execution yet (that is R5),
/// but when it lands, two runs checked out into two different worktrees can still declare
/// overlapping <c>ownedFiles</c>/<c>allowedWriteGlobs</c> against the same eventual merge target;
/// a lease keyed on checkout path would let them both acquire because the paths differ, silently
/// defeating the whole point of this store. Any future caller — R5's worktree runner included —
/// must resolve a run's scope the same workspace-relative way <see cref="FileLeaseScopeResolver"/>
/// does today and lease that, never a worktree-local path.
/// </para>
/// </summary>
public sealed class FileLeaseStore
{
    /// <summary>
    /// Default lease lifetime used when a run does not specify one. Deliberately longer than
    /// <c>ClaudeRunContext.MaxRunDuration</c> (30 minutes, see ActionExecutor/ClaudeRunner): the
    /// TTL is a crash-safety backstop for a run that never reaches its own completion path (and
    /// therefore never calls <see cref="ReleaseAsync"/>), not the expected lease lifetime — an
    /// orderly run releases its lease explicitly long before the TTL matters.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(45);

    private const string SelectColumns =
        "SELECT LeaseId, TicketId, RunId, Agent, ScopeJson, AcquiredAtUtc, HeartbeatAtUtc, ExpiresAtUtc FROM file_leases";

    private readonly ProjectService _projects;

    public FileLeaseStore(ProjectService projects)
    {
        _projects = projects;
    }

    /// <summary>
    /// Atomically reaps expired leases for this project, then either grants the new lease (no
    /// active lease's scope intersects <paramref name="scope"/>) or reports the conflicting one.
    /// Re-acquiring for a <paramref name="runId"/> that already holds an active lease is idempotent
    /// and returns that lease rather than a conflict — a retried dispatch must not be told it
    /// conflicts with itself.
    /// </summary>
    public async Task<FileLeaseAcquireResult> AcquireAsync(
        string slug,
        int ticketId,
        string runId,
        string agent,
        IReadOnlyList<string> scope,
        DateTime now,
        TimeSpan ttl,
        PathCaseSensitivity caseSensitivity = PathCaseSensitivity.Sensitive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        if (scope.Count == 0)
            throw new ArgumentException("Lease scope must not be empty.", nameof(scope));

        await using var conn = await OpenReadyAsync(slug, ct);
        await ExecAsync(conn, "BEGIN IMMEDIATE;", ct);
        try
        {
            // Reaping inside the same transaction as the intersection scan is what makes the
            // acquire/reap race safe: a lease whose TTL just elapsed is either fully released here
            // before the scan below can see it, or the scan sees it still active and correctly
            // treats it as live. BEGIN IMMEDIATE plus the busy_timeout set in OpenReadyAsync
            // serializes every writer against this project's lease table, so there is no window
            // where a concurrent acquirer observes a half-expired row.
            await ReapExpiredAsync(conn, now, ct);

            var existing = await ReadActiveLeaseForRunAsync(conn, slug, runId, ct);
            if (existing is not null)
            {
                await ExecAsync(conn, "COMMIT;", ct);
                return FileLeaseAcquireResult.Acquired(existing);
            }

            foreach (var active in await ReadActiveLeasesAsync(conn, slug, ct))
            {
                if (GlobIntersection.MayIntersect(scope, active.Scope, caseSensitivity))
                {
                    await ExecAsync(conn, "ROLLBACK;", ct);
                    return FileLeaseAcquireResult.Conflict(active);
                }
            }

            var lease = new FileLease(
                Guid.NewGuid().ToString("N"), slug, ticketId, runId, agent,
                scope.ToArray(), now, now, now + ttl);
            await InsertLeaseAsync(conn, lease, ct);
            await ExecAsync(conn, "COMMIT;", ct);
            return FileLeaseAcquireResult.Acquired(lease);
        }
        catch
        {
            await TryExecAsync(conn, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>Releases every active lease held by <paramref name="runId"/>. A no-op (zero rows
    /// affected) when the run never held one, e.g. because its declared scope was empty.</summary>
    public async Task ReleaseAsync(string slug, string runId, DateTime now, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return;

        await using var conn = await OpenReadyAsync(slug, ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE file_leases SET ReleasedAtUtc = @now WHERE RunId = @runId AND ReleasedAtUtc IS NULL";
        cmd.Parameters.AddWithValue("@now", Iso(now));
        cmd.Parameters.AddWithValue("@runId", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reaps every lease past its <c>ExpiresAtUtc</c> — the durable counterpart to
    /// <see cref="Services.ConcurrencyLockReaper.Reap"/>, called by <see cref="Services.FileLeaseReaper"/>
    /// on its poll cadence for every project. Returns the leases it reaped so callers (and tests)
    /// can assert on exactly what became reassignable.
    /// </summary>
    public async Task<IReadOnlyList<FileLease>> ReapStaleAsync(string slug, DateTime now, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return Array.Empty<FileLease>();

        await using var conn = await OpenReadyAsync(slug, ct);
        await ExecAsync(conn, "BEGIN IMMEDIATE;", ct);
        try
        {
            var stale = new List<FileLease>();
            using (var select = conn.CreateCommand())
            {
                select.CommandText = SelectColumns + " WHERE ReleasedAtUtc IS NULL AND ExpiresAtUtc <= @now";
                select.Parameters.AddWithValue("@now", Iso(now));
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) stale.Add(ReadLease(reader, slug));
            }
            if (stale.Count > 0)
                await ReapExpiredAsync(conn, now, ct);
            await ExecAsync(conn, "COMMIT;", ct);
            return stale;
        }
        catch
        {
            await TryExecAsync(conn, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>All currently active (unreleased, unexpired-as-of-last-reap) leases for a project.
    /// Does not itself reap — callers that need a reap-then-list view should call
    /// <see cref="ReapStaleAsync"/> first.</summary>
    public async Task<IReadOnlyList<FileLease>> ListActiveAsync(string slug, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return Array.Empty<FileLease>();

        await using var conn = await OpenReadyAsync(slug, ct);
        return await ReadActiveLeasesAsync(conn, slug, ct);
    }

    // ── Connection / schema ─────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenReadyAsync(string slug, CancellationToken ct)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        // WAL + a busy timeout is what turns a second connection's concurrent BEGIN IMMEDIATE into
        // a bounded wait instead of an immediate SQLITE_BUSY — the acquire/reap race test relies on
        // this to prove genuinely concurrent connections serialize rather than one simply failing.
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
                CREATE TABLE IF NOT EXISTS file_leases (
                    LeaseId TEXT NOT NULL PRIMARY KEY,
                    TicketId INTEGER NOT NULL,
                    RunId TEXT NOT NULL,
                    Agent TEXT NOT NULL,
                    ScopeJson TEXT NOT NULL,
                    AcquiredAtUtc TEXT NOT NULL,
                    HeartbeatAtUtc TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    ReleasedAtUtc TEXT NULL
                )
                """;
            cmd.ExecuteNonQuery();
        }
        // Repo convention (ProjectService.EnsureRegistryInitializedAsync, TriggerStateStore):
        // CREATE TABLE IF NOT EXISTS covers a fresh install; ALTER TABLE ADD COLUMN wrapped in a
        // try/catch is how a column added after this table already shipped would reach existing
        // installs. file_leases is new in R4, so there is nothing to migrate yet — this comment is
        // the hook for the next column, not dead scaffolding.
        using (var index1 = conn.CreateCommand())
        {
            index1.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_file_leases_active ON file_leases (ReleasedAtUtc, ExpiresAtUtc)";
            index1.ExecuteNonQuery();
        }
        using (var index2 = conn.CreateCommand())
        {
            // Partial unique index: at most one *active* lease per run. This is what makes the
            // idempotent re-acquire path (ReadActiveLeaseForRunAsync) correct rather than merely
            // convenient — two concurrent acquires for the same runId cannot both insert.
            index2.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_file_leases_run_active ON file_leases (RunId) WHERE ReleasedAtUtc IS NULL";
            index2.ExecuteNonQuery();
        }
    }

    // ── Row helpers ──────────────────────────────────────────────────────────

    private static async Task ReapExpiredAsync(SqliteConnection conn, DateTime now, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE file_leases SET ReleasedAtUtc = @now WHERE ReleasedAtUtc IS NULL AND ExpiresAtUtc <= @now";
        cmd.Parameters.AddWithValue("@now", Iso(now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<FileLease?> ReadActiveLeaseForRunAsync(
        SqliteConnection conn, string slug, string runId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE RunId = @runId AND ReleasedAtUtc IS NULL";
        cmd.Parameters.AddWithValue("@runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader, slug) : null;
    }

    private static async Task<List<FileLease>> ReadActiveLeasesAsync(
        SqliteConnection conn, string slug, CancellationToken ct)
    {
        var result = new List<FileLease>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE ReleasedAtUtc IS NULL";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadLease(reader, slug));
        return result;
    }

    private static async Task InsertLeaseAsync(SqliteConnection conn, FileLease lease, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_leases
                (LeaseId, TicketId, RunId, Agent, ScopeJson, AcquiredAtUtc, HeartbeatAtUtc, ExpiresAtUtc, ReleasedAtUtc)
            VALUES
                (@id, @ticket, @run, @agent, @scope, @acquired, @heartbeat, @expires, NULL)
            """;
        cmd.Parameters.AddWithValue("@id", lease.LeaseId);
        cmd.Parameters.AddWithValue("@ticket", lease.TicketId);
        cmd.Parameters.AddWithValue("@run", lease.RunId);
        cmd.Parameters.AddWithValue("@agent", lease.Agent);
        cmd.Parameters.AddWithValue("@scope", JsonSerializer.Serialize(lease.Scope));
        cmd.Parameters.AddWithValue("@acquired", Iso(lease.AcquiredAtUtc));
        cmd.Parameters.AddWithValue("@heartbeat", Iso(lease.HeartbeatAtUtc));
        cmd.Parameters.AddWithValue("@expires", Iso(lease.ExpiresAtUtc));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FileLease ReadLease(SqliteDataReader reader, string slug) => new(
        reader.GetString(0),
        slug,
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
        ParseIso(reader.GetString(5)),
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

    private static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("O");

    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

using GigaClaw.Core.Data;
using GigaClaw.Core.Services;
using Microsoft.Data.Sqlite;

namespace GigaClaw.Core.Github;

/// <summary>One issue ↔ ticket binding. The row is what makes a re-sync an update instead of a copy.</summary>
public sealed record GitHubIssueLink(
    string Repository,
    int IssueNumber,
    int TicketId,
    string IssueState,
    DateTime? IssueUpdatedAtUtc,
    DateTime LastSyncedAtUtc,
    bool RoundTripDone);

/// <summary>
/// The idempotency table for issue import, in the per-project SQLite DB next to the tickets it
/// points at (same reasoning as <c>TeamStore</c>: the ticket ids only mean anything in that file).
/// <para>
/// <b>Why a table and not a marker in the ticket.</b> The mapping has to be authoritative before
/// the ticket exists — the sync consults it to decide whether to create one at all — and it has to
/// survive a restart, a re-sync and an owner editing the ticket body. A provenance line in the
/// description satisfies none of those: an agent can rewrite it, and a half-written ticket would
/// re-import as a duplicate on the next poll.
/// </para>
/// The primary key is (Repository, IssueNumber), not IssueNumber alone, so a project pointed at a
/// second repository cannot have issue #12 of one collide with issue #12 of the other.
/// Inline migration in the repo's established shape: <c>CREATE TABLE IF NOT EXISTS</c>, no column
/// added to any existing table, so no pre-existing row is touched.
/// </summary>
public sealed class GitHubIssueLinkStore
{
    private readonly ProjectService _projects;

    public GitHubIssueLinkStore(ProjectService projects)
    {
        _projects = projects;
    }

    private async Task<SqliteConnection> OpenAsync(string slug)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        await MigrationGate.RunOnceAsync(path, "github-issue-links", async () =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS GitHubIssueLinks (
                    Repository TEXT NOT NULL,
                    IssueNumber INTEGER NOT NULL,
                    TicketId INTEGER NOT NULL,
                    IssueState TEXT NOT NULL DEFAULT 'open',
                    IssueUpdatedAtUtc TEXT NULL,
                    LastSyncedAtUtc TEXT NOT NULL,
                    RoundTripDone INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (Repository, IssueNumber)
                )
                """;
            await cmd.ExecuteNonQueryAsync();
        });
        return conn;
    }

    public async Task<GitHubIssueLink?> GetAsync(string slug, string repository, int issueNumber)
    {
        await using var conn = await OpenAsync(slug);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Repository, IssueNumber, TicketId, IssueState, IssueUpdatedAtUtc, LastSyncedAtUtc, RoundTripDone
            FROM GitHubIssueLinks WHERE Repository = @repo AND IssueNumber = @number
            """;
        cmd.Parameters.AddWithValue("@repo", repository);
        cmd.Parameters.AddWithValue("@number", issueNumber);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<GitHubIssueLink>> ListAsync(string slug, string? repository = null)
    {
        await using var conn = await OpenAsync(slug);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = repository is null
            ? """
                SELECT Repository, IssueNumber, TicketId, IssueState, IssueUpdatedAtUtc, LastSyncedAtUtc, RoundTripDone
                FROM GitHubIssueLinks ORDER BY Repository, IssueNumber
                """
            : """
                SELECT Repository, IssueNumber, TicketId, IssueState, IssueUpdatedAtUtc, LastSyncedAtUtc, RoundTripDone
                FROM GitHubIssueLinks WHERE Repository = @repo ORDER BY IssueNumber
                """;
        if (repository is not null) cmd.Parameters.AddWithValue("@repo", repository);
        var links = new List<GitHubIssueLink>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) links.Add(Read(reader));
        return links;
    }

    /// <summary>Finds the link a ticket belongs to, if any. Used by the PR-feedback resolver.</summary>
    public async Task<GitHubIssueLink?> FindByTicketAsync(string slug, int ticketId)
    {
        await using var conn = await OpenAsync(slug);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Repository, IssueNumber, TicketId, IssueState, IssueUpdatedAtUtc, LastSyncedAtUtc, RoundTripDone
            FROM GitHubIssueLinks WHERE TicketId = @ticket LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@ticket", ticketId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    /// <summary>
    /// Records or refreshes a binding. Upsert rather than insert: two syncs racing on the same
    /// issue must converge on one row, never fail and never make a second ticket.
    /// <see cref="GitHubIssueLink.RoundTripDone"/> is deliberately preserved on conflict — a
    /// re-import must not un-remember that the issue was already commented on and closed.
    /// </summary>
    public async Task UpsertAsync(string slug, GitHubIssueLink link)
    {
        await using var conn = await OpenAsync(slug);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO GitHubIssueLinks
                (Repository, IssueNumber, TicketId, IssueState, IssueUpdatedAtUtc, LastSyncedAtUtc, RoundTripDone)
            VALUES (@repo, @number, @ticket, @state, @updated, @synced, @roundTrip)
            ON CONFLICT(Repository, IssueNumber) DO UPDATE SET
                TicketId = excluded.TicketId,
                IssueState = excluded.IssueState,
                IssueUpdatedAtUtc = excluded.IssueUpdatedAtUtc,
                LastSyncedAtUtc = excluded.LastSyncedAtUtc
            """;
        cmd.Parameters.AddWithValue("@repo", link.Repository);
        cmd.Parameters.AddWithValue("@number", link.IssueNumber);
        cmd.Parameters.AddWithValue("@ticket", link.TicketId);
        cmd.Parameters.AddWithValue("@state", link.IssueState);
        cmd.Parameters.AddWithValue("@updated", (object?)link.IssueUpdatedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@synced", link.LastSyncedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@roundTrip", link.RoundTripDone ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Marks the close-side round trip as spent, so it happens exactly once per issue.</summary>
    public async Task MarkRoundTripDoneAsync(string slug, string repository, int issueNumber)
    {
        await using var conn = await OpenAsync(slug);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE GitHubIssueLinks SET RoundTripDone = 1
            WHERE Repository = @repo AND IssueNumber = @number
            """;
        cmd.Parameters.AddWithValue("@repo", repository);
        cmd.Parameters.AddWithValue("@number", issueNumber);
        await cmd.ExecuteNonQueryAsync();
    }

    private static GitHubIssueLink Read(SqliteDataReader reader) => new(
        Repository: reader.GetString(0),
        IssueNumber: reader.GetInt32(1),
        TicketId: reader.GetInt32(2),
        IssueState: reader.GetString(3),
        IssueUpdatedAtUtc: reader.IsDBNull(4)
            ? null
            : DateTime.TryParse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated) ? updated : null,
        LastSyncedAtUtc: DateTime.TryParse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind, out var synced) ? synced : DateTime.MinValue,
        RoundTripDone: reader.GetInt32(6) != 0);
}

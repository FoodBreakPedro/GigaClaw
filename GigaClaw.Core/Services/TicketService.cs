using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Data;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

public sealed class TicketTransitionConflictException(string message) : InvalidOperationException(message);

public sealed class TicketDependencyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Read-only dependency edge contract consumed by automation without coupling the
/// persistence layer to any particular condition vocabulary.
/// </summary>
public interface ITicketDependencyQuery
{
    Task<IReadOnlyList<TicketDependencyInfo>?> ListBlockingTicketsAsync(
        string projectSlug,
        int ticketId);
}

public class TicketService : ITicketDependencyQuery
{
    private readonly ProjectService _projectService;
    private readonly MemberService _memberService;
    private readonly ILogger? _logger;

    /// <summary>
    /// Raised after a ticket's status has been persisted.
    /// Parameters: (projectSlug, ticketId, fromStatus, toStatus)
    /// </summary>
    public event Action<string, int, string, string>? TicketStatusChanged;

    /// <summary>
    /// Raised immediately after a comment is persisted.
    /// Parameters: (projectSlug, ticketId, author, content)
    /// </summary>
    public event Action<string, int, string, string>? TicketCommentAdded;

    public TicketService(ProjectService projectService, MemberService memberService, ILogger<TicketService>? logger = null)
    {
        _projectService = projectService;
        _memberService = memberService;
        _logger = logger;
    }

    // Ensures the ActivityEntries table exists (for databases created before this feature)
    private static Task EnsureActivityTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "activity-table", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ActivityEntries (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    Author TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                )
            """);
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_ActivityEntries_TicketId ON ActivityEntries(TicketId)");
        });

    private static Task EnsureLabelTablesAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "label-tables", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS Labels (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#6366f1'
                )
            """);
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TicketLabels (
                    TicketsId INTEGER NOT NULL,
                    LabelsId INTEGER NOT NULL,
                    PRIMARY KEY (TicketsId, LabelsId)
                )
            """);
        });

    private static Task EnsureSortOrderColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-sortorder", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0"));

    private static Task EnsureAssignedToColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-assignedto", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AssignedTo TEXT NULL"));

    private static Task EnsureParentIdColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-parentid", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN ParentId INTEGER NULL"));

    // Adds the Scheduled-status columns (feature #99) to databases created before this feature.
    private static Task EnsureScheduleColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-schedule", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN FireAt TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN ScheduleTarget TEXT NULL");
        });

    // Adds the cumulative agent token-usage columns to databases created before this feature.
    private static Task EnsureAgentUsageColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-agent-usage", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentTokens INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentCostUsd REAL NOT NULL DEFAULT 0");
        });

    // Normalized dependency edges for databases created before P4. CREATE TABLE IF NOT
    // EXISTS is the table equivalent of the existing ALTER TABLE try/catch migrations:
    // it is idempotent and preserves every pre-existing ticket row.
    private static Task EnsureTicketDependenciesTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "ticket-dependencies", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TicketDependencies (
                    BlockedTicketId INTEGER NOT NULL,
                    BlockingTicketId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT PK_TicketDependencies PRIMARY KEY (BlockedTicketId, BlockingTicketId),
                    CONSTRAINT CK_TicketDependencies_NotSelf CHECK (BlockedTicketId <> BlockingTicketId),
                    CONSTRAINT FK_TicketDependencies_BlockedTicket
                        FOREIGN KEY (BlockedTicketId) REFERENCES Tickets (Id) ON DELETE CASCADE,
                    CONSTRAINT FK_TicketDependencies_BlockingTicket
                        FOREIGN KEY (BlockingTicketId) REFERENCES Tickets (Id) ON DELETE CASCADE
                )
            """);
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_TicketDependencies_BlockingTicketId ON TicketDependencies(BlockingTicketId)");
        });

    // R5 (doc/roadmap/lane-codex-runtime.md): worktree/branch state for databases created
    // before per-ticket worktree isolation shipped.
    private static Task EnsureWorktreeColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-worktree", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN WorktreeBranch TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN WorktreePath TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN WorktreeStatus TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN WorktreeUpdatedAt TEXT NULL");
        });

    private static Task EnsureDeliverableTypeColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-deliverable-type", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN DeliverableType TEXT NULL"));

    private static async Task EnsureTicketEntityColumnsAsync(TodoDbContext db)
    {
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        await EnsureWorktreeColumnsAsync(db);
        await EnsureDeliverableTypeColumnAsync(db);
    }

    // Hot-path indexes: status/parent filters run on every board render, and the activity
    // subquery in ListTicketsAsync scans per ticket. Must run after the column migrations.
    private static Task EnsureTicketIndexesAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "ticket-indexes", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Tickets_Status ON Tickets(Status)");
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Tickets_ParentId ON Tickets(ParentId)");
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Comments_TicketId ON Comments(TicketId)");
        });

    public async Task<List<TicketSummary>> ListTicketsAsync(string projectSlug, string? statusFilter = null, TicketPriority? priorityFilter = null, string? assignedTo = null, string? createdBy = null, string? search = null, int? parentId = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        await EnsureDeliverableTypeColumnAsync(db);
        await EnsureTicketIndexesAsync(db);
        await EnsureTicketDependenciesTableAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var query = db.Tickets.Include(t => t.Labels).AsQueryable();
        if (statusFilter is not null)
            query = query.Where(t => t.Status == statusFilter);
        if (priorityFilter.HasValue)
            query = query.Where(t => t.Priority == priorityFilter.Value);
        if (assignedTo is not null)
            query = query.Where(t => t.AssignedTo == assignedTo);
        if (createdBy is not null)
            query = query.Where(t => t.CreatedBy == createdBy);
        if (parentId is not null)
            query = query.Where(t => t.ParentId == parentId.Value);
        if (search is not null)
            query = query.Where(t => t.Title.Contains(search) || t.Description.Contains(search) || t.Comments.Any(c => c.Content.Contains(search)));

        var allTickets = await query
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new TicketSummary(
                t.Id, t.Title, t.Description, t.Status, t.Priority, t.SortOrder,
                t.AssignedTo, t.CreatedBy, t.CreatedAt, t.UpdatedAt,
                t.Labels,
                t.Comments.Count,
                t.Activities.Max(a => (DateTime?)a.CreatedAt),
                t.ParentId,
                new List<SubTicketInfo>())
                {
                    FireAt = t.FireAt,
                    ScheduleTarget = t.ScheduleTarget,
                    AgentTokens = t.AgentTokens,
                    AgentCostUsd = t.AgentCostUsd,
                    DeliverableType = t.DeliverableType
                })
            .ToListAsync();

        // Load children for ALL returned parents, ignoring the status filter so that
        // parents filtered by their own status still see children in other statuses.
        var parentIds = allTickets.Select(t => t.Id).ToHashSet();
        var childRows = parentIds.Count > 0
            ? await db.Tickets
                .Where(t => t.ParentId != null && parentIds.Contains(t.ParentId!.Value))
                .Select(t => new { t.ParentId, Info = new SubTicketInfo(t.Id, t.Title, t.Status, t.AssignedTo) })
                .ToListAsync()
            : [];
        var subsByParent = childRows
            .GroupBy(x => x.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Info).ToList());

        // Phase 3.1: blocked-reason chip. One cheap comments query scoped to the (typically
        // few) Blocked tickets — never per-ticket — so the board list stays a couple of
        // round trips regardless of board size.
        // Only the marker's first line is ever parsed and the rendered chip is ~60 chars, so the
        // query pulls a truncated prefix instead of whole comment bodies — a receipt can carry a
        // full diff, and the board has no use for any of it. The row cap bounds a ticket that
        // collected hundreds of gate receipts; the newest one wins regardless.
        var blockedIds = allTickets.Where(t => t.Status == "Blocked").Select(t => t.Id).ToList();
        var blockedReasons = blockedIds.Count > 0
            ? await db.Comments.AsNoTracking()
                .Where(c => blockedIds.Contains(c.TicketId)
                    && (c.Content.Contains("GIGACLAW-GATE v1") || c.Content.Contains("GIGACLAW-REPAIR v1")))
                .OrderByDescending(c => c.CreatedAt)
                .Take(MaxBlockedReasonComments)
                .Select(c => new { c.TicketId, Content = c.Content.Substring(0, BlockedReasonPrefixLength) })
                .ToListAsync()
            : [];
        var blockedReasonByTicket = blockedReasons
            .GroupBy(c => c.TicketId)
            .ToDictionary(g => g.Key, g => ExtractBlockedReason(g.First().Content)); // First = newest (query is ordered desc)

        var ticketIds = allTickets.Select(t => t.Id).ToList();
        var dependencyRows = ticketIds.Count > 0
            ? await (
                from edge in db.TicketDependencies.AsNoTracking()
                join blocked in db.Tickets.AsNoTracking() on edge.BlockedTicketId equals blocked.Id
                join blocking in db.Tickets.AsNoTracking() on edge.BlockingTicketId equals blocking.Id
                where ticketIds.Contains(edge.BlockedTicketId) || ticketIds.Contains(edge.BlockingTicketId)
                select new
                {
                    edge.BlockedTicketId,
                    edge.BlockingTicketId,
                    Blocked = new TicketDependencyInfo(blocked.Id, blocked.Title, blocked.Status, blocked.AssignedTo),
                    Blocking = new TicketDependencyInfo(blocking.Id, blocking.Title, blocking.Status, blocking.AssignedTo)
                })
                .ToListAsync()
            : [];

        return allTickets.Select(t => t with
        {
            SubTickets = subsByParent.GetValueOrDefault(t.Id) ?? [],
            BlockedBy = dependencyRows
                .Where(edge => edge.BlockedTicketId == t.Id)
                .Select(edge => edge.Blocking)
                .OrderBy(edge => edge.Id)
                .ToList(),
            Blocks = dependencyRows
                .Where(edge => edge.BlockingTicketId == t.Id)
                .Select(edge => edge.Blocked)
                .OrderBy(edge => edge.Id)
                .ToList(),
            BlockedReason = blockedReasonByTicket.GetValueOrDefault(t.Id)
        }).ToList();
    }

    // Phase 3.1: matches the marker's first line, e.g.
    //   "GIGACLAW-GATE v1 ticket-42 blocked — the reviewer returned BLOCK."
    //   "GIGACLAW-REPAIR v1 ticket-42 escalated 2/2"
    // See doc/verdict-contract.md (Transport, The bounded repair loop) for the marker family.
    //
    // Neither regex below is built from a shared marker constant, because the two markers do not
    // share an origin. GIGACLAW-GATE has no C# emitter at all — every instance is addComment
    // content authored in ProjectTemplate/Agents/automations.json, so there is no constant to
    // reference and the shape can drift per workspace. GIGACLAW-REPAIR is emitted by
    // Automation/Verdicts/RepairLoop.RenderEscalation (RepairLoop.EscalationMarkerPrefix).
    //
    // Both markers are also read by the automation side — RepairLoop.EscalationRegex and
    // ReviewerRetry.GateReceiptRegex — to decide episode boundaries: those are anchored, Multiline,
    // and run over a whole comment body. The two here read one already-split line to render a chip
    // and are deliberately looser, so a drifted marker degrades the display instead of mis-scoring
    // a budget. Keep the strictness difference when either side changes.
    /// <summary>How much of a receipt comment the blocked-reason chip reads. Only the first line is
    /// parsed and the chip truncates to ~60 chars, so the rest is dead weight in the query.</summary>
    private const int BlockedReasonPrefixLength = 240;

    /// <summary>Row cap on the blocked-reason lookup — the newest receipt per ticket is all that is
    /// used, and Blocked tickets are few.</summary>
    private const int MaxBlockedReasonComments = 200;

    // The nested quantifier `(?:\s+\S+)*?` before a literal can backtrack badly on a hostile line,
    // and the marker text is written by agents. The timeout turns the pathological case into a
    // caught RegexMatchTimeoutException-free fallback rather than a hung board render.
    private static readonly Regex GateMarkerLineRegex = new(
        @"^GIGACLAW-GATE\s+v1\s+ticket-\d+\s+\S+(?:\s+\S+)*?\s+—\s+(?<reason>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex RepairMarkerLineRegex = new(
        @"^GIGACLAW-REPAIR\s+v1\s+ticket-\d+\s+escalated\s+(?<used>\d+)/(?<max>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Derives a short (~60 char) human-readable reason from the newest gate/repair receipt on
    /// a Blocked ticket. Best-effort: the marker text is owned by <c>automations.json</c>, so this
    /// degrades to a truncated first line rather than throwing when the shape drifts.
    /// </summary>
    internal static string? ExtractBlockedReason(string commentContent)
    {
        if (string.IsNullOrWhiteSpace(commentContent)) return null;
        var firstLine = commentContent.Split('\n', 2)[0].Trim();

        try
        {
            var gateMatch = GateMarkerLineRegex.Match(firstLine);
            if (gateMatch.Success)
            {
                var reason = gateMatch.Groups["reason"].Value.Trim();
                var firstSentence = reason.Split('.', 2)[0].Trim();
                return TruncateReason(firstSentence);
            }

            var repairMatch = RepairMarkerLineRegex.Match(firstLine);
            if (repairMatch.Success)
                return $"Repair budget exhausted ({repairMatch.Groups["used"].Value}/{repairMatch.Groups["max"].Value})";
        }
        catch (RegexMatchTimeoutException)
        {
            // A marker line crafted (or corrupted) into catastrophic backtracking degrades to the
            // same truncated-first-line fallback the shape-drift path already uses.
            return TruncateReason(firstLine);
        }

        if (firstLine.Contains("GIGACLAW-GATE", StringComparison.Ordinal)
            || firstLine.Contains("GIGACLAW-REPAIR", StringComparison.Ordinal))
            return TruncateReason(firstLine);

        return null;
    }

    private static string TruncateReason(string reason)
    {
        const int maxLen = 60;
        return reason.Length <= maxLen ? reason : reason[..(maxLen - 1)].TrimEnd() + "…";
    }

    public async Task<Ticket?> GetTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await EnsureTicketDependenciesTableAsync(db);
        var ticket = await db.Tickets
            .Include(t => t.Comments.OrderBy(c => c.CreatedAt))
            .Include(t => t.Activities.OrderBy(a => a.CreatedAt))
            .Include(t => t.Labels)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return null;
        ticket.SubTickets = await db.Tickets
            .Where(t => t.ParentId == ticketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new SubTicketInfo(t.Id, t.Title, t.Status, t.AssignedTo))
            .ToListAsync();
        var dependencies = await LoadTicketDependenciesAsync(db, ticketId);
        ticket.BlockedBy = dependencies.BlockedBy.ToList();
        ticket.Blocks = dependencies.Blocks.ToList();
        return ticket;
    }

    public async Task<TicketDependencies?> GetTicketDependenciesAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureAssignedToColumnAsync(db);
        await EnsureDeliverableTypeColumnAsync(db);
        await EnsureTicketDependenciesTableAsync(db);
        if (!await db.Tickets.AsNoTracking().AnyAsync(ticket => ticket.Id == ticketId))
            return null;
        return await LoadTicketDependenciesAsync(db, ticketId);
    }

    public async Task<IReadOnlyList<TicketDependencyInfo>?> ListBlockingTicketsAsync(
        string projectSlug,
        int ticketId)
    {
        var dependencies = await GetTicketDependenciesAsync(projectSlug, ticketId);
        return dependencies?.BlockedBy;
    }

    /// <summary>
    /// Adds a "blocked by" edge. The immediate SQLite transaction acquires the writer
    /// reservation before validation, serializing the recursive cycle check with every
    /// competing edge insert. This prevents a check/write race from admitting opposite edges.
    /// </summary>
    public async Task<TicketDependencyInfo> AddTicketDependencyAsync(
        string projectSlug,
        int blockedTicketId,
        int blockingTicketId)
    {
        if (blockedTicketId == blockingTicketId)
            throw new TicketDependencyException(
                "dependency_self",
                "A ticket cannot depend on itself.");

        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureAssignedToColumnAsync(db);
        await EnsureTicketDependenciesTableAsync(db);
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            var existingIds = new HashSet<int>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT Id FROM Tickets WHERE Id IN ($blocked, $blocking)";
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    existingIds.Add(reader.GetInt32(0));
            }

            if (!existingIds.Contains(blockedTicketId))
                throw new TicketDependencyException(
                    "ticket_not_found",
                    $"Ticket #{blockedTicketId} does not exist.");
            if (!existingIds.Contains(blockingTicketId))
                throw new TicketDependencyException(
                    "blocking_ticket_not_found",
                    $"Blocking ticket #{blockingTicketId} does not exist.");

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1
                        FROM TicketDependencies
                        WHERE BlockedTicketId = $blocked AND BlockingTicketId = $blocking
                    )
                    """;
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
                    throw new TicketDependencyException(
                        "dependency_duplicate",
                        $"Ticket #{blockedTicketId} is already blocked by ticket #{blockingTicketId}.");
            }

            // An edge X -> Y means "X is blocked by Y". Adding it is cyclic when Y can
            // already reach X by following existing blocked -> blocker edges.
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    WITH RECURSIVE blockers(TicketId) AS (
                        SELECT BlockingTicketId
                        FROM TicketDependencies
                        WHERE BlockedTicketId = $blocking
                        UNION
                        SELECT edge.BlockingTicketId
                        FROM TicketDependencies AS edge
                        JOIN blockers ON edge.BlockedTicketId = blockers.TicketId
                    )
                    SELECT EXISTS (
                        SELECT 1 FROM blockers WHERE TicketId = $blocked
                    )
                    """;
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
                    throw new TicketDependencyException(
                        "dependency_cycle",
                        $"Adding ticket #{blockingTicketId} as a blocker of ticket #{blockedTicketId} would create a dependency cycle.");
            }

            TicketDependencyInfo blocker;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT Id, Title, Status, AssignedTo
                    FROM Tickets
                    WHERE Id = $blocking
                    """;
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                blocker = new TicketDependencyInfo(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO TicketDependencies (BlockedTicketId, BlockingTicketId, CreatedAt)
                    VALUES ($blocked, $blocking, $createdAt);
                    UPDATE Tickets SET UpdatedAt = $createdAt WHERE Id = $blocked;
                    """;
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return blocker;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> RemoveTicketDependencyAsync(
        string projectSlug,
        int blockedTicketId,
        int blockingTicketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTicketDependenciesTableAsync(db);
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            int affected;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM TicketDependencies
                    WHERE BlockedTicketId = $blocked AND BlockingTicketId = $blocking
                    """;
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$blocking", blockingTicketId);
                affected = await command.ExecuteNonQueryAsync();
            }

            if (affected > 0)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Tickets SET UpdatedAt = $updatedAt WHERE Id = $blocked";
                command.Parameters.AddWithValue("$blocked", blockedTicketId);
                command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return affected > 0;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<TicketDependencies> LoadTicketDependenciesAsync(
        TodoDbContext db,
        int ticketId)
    {
        var blockedBy = await (
            from edge in db.TicketDependencies.AsNoTracking()
            join blocking in db.Tickets.AsNoTracking() on edge.BlockingTicketId equals blocking.Id
            where edge.BlockedTicketId == ticketId
            orderby blocking.Id
            select new TicketDependencyInfo(
                blocking.Id,
                blocking.Title,
                blocking.Status,
                blocking.AssignedTo))
            .ToListAsync();

        var blocks = await (
            from edge in db.TicketDependencies.AsNoTracking()
            join blocked in db.Tickets.AsNoTracking() on edge.BlockedTicketId equals blocked.Id
            where edge.BlockingTicketId == ticketId
            orderby blocked.Id
            select new TicketDependencyInfo(
                blocked.Id,
                blocked.Title,
                blocked.Status,
                blocked.AssignedTo))
            .ToListAsync();

        return new TicketDependencies(blockedBy, blocks);
    }

    /// <summary>
    /// Accumulates a completed agent run's token usage onto the ticket. Durable counterpart of
    /// the in-memory run registry (whose runs are purged after 24h) — called by RunCostRecorder.
    /// </summary>
    public async Task AddAgentUsageAsync(string projectSlug, int ticketId, long tokens, double costUsd)
    {
        if (tokens <= 0 && costUsd <= 0) return;
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        await EnsureDeliverableTypeColumnAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;
        ticket.AgentTokens += tokens;
        ticket.AgentCostUsd += costUsd;
        await db.SaveChangesAsync();
    }

    public async Task<Ticket> CreateTicketAsync(string projectSlug, string title, string description = "", string createdBy = "owner", string status = "Backlog", List<int>? labelIds = null, TicketPriority priority = TicketPriority.NiceToHave, string? assignedTo = null, int? parentId = null, string? deliverableType = null)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new InvalidOperationException("Le champ 'createdBy' est requis.");
        var deliverable = ResolveDeliverable(deliverableType);
        var resolvedAssignee = string.IsNullOrEmpty(assignedTo) ? deliverable?.EntryAgent : assignedTo;
        if (!string.IsNullOrEmpty(resolvedAssignee) && !await _memberService.MemberExistsAsync(projectSlug, resolvedAssignee))
            throw new InvalidOperationException($"Le membre '{resolvedAssignee}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        if (parentId is not null)
        {
            var parentExists = await db.Tickets.AnyAsync(t => t.Id == parentId.Value);
            if (!parentExists)
                throw new InvalidOperationException($"Le ticket parent #{parentId} n'existe pas.");
        }
        var maxSort = await db.Tickets.Where(t => t.Status == status).Select(t => (int?)t.SortOrder).MaxAsync() ?? -1;
        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            Status = status,
            Priority = priority,
            SortOrder = maxSort + 1,
            AssignedTo = resolvedAssignee,
            ParentId = parentId,
            DeliverableType = deliverable?.Slug
        };
        if (labelIds is { Count: > 0 })
        {
            var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToListAsync();
            ticket.Labels = labels;
        }
        // Two SaveChanges (the entry needs the generated ticket id) — keep them atomic so a
        // crash can't produce a ticket without its creation activity.
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = createdBy,
            Text = "created the ticket"
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ticket;
    }

    public async Task<Ticket?> MoveTicketAsync(string projectSlug, int ticketId, string newStatus, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var columnExists = await db.BoardColumns.AnyAsync(c => c.Name == newStatus);
        if (!columnExists)
            throw new InvalidOperationException($"Column '{newStatus}' does not exist.");
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase))
            return ticket; // already in target status — no-op
        ticket.Status = newStatus;
        if (string.Equals(oldStatus, "Scheduled", StringComparison.OrdinalIgnoreCase))
        {
            // Leaving Scheduled by hand cancels the pending promotion — otherwise the stale
            // FireAt keeps showing a countdown badge and would fire instantly if re-scheduled.
            ticket.FireAt = null;
            ticket.ScheduleTarget = null;
        }
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"moved the ticket: {oldStatus} → {newStatus}"
        });
        await db.SaveChangesAsync();
        await TryCleanupWorktreeOnDoneAsync(projectSlug, ticket);
        TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, newStatus);
        return ticket;
    }

    /// <summary>
    /// R6 (doc/roadmap/SESSION-HANDOFF.md, PLAN-remaining.md §1 item 3): worktree cleanup used to
    /// run only from ActionExecutor's moveTicketStatus action, so a user dragging a ticket to Done
    /// on the Board UI (which goes through <see cref="ReorderTicketAsync"/>, never through
    /// ActionExecutor) silently orphaned the worktree. This is now the SINGLE shared implementation,
    /// called from every method below that can land a ticket on "Done" — the automation
    /// moveTicketStatus action (<see cref="MoveTicketAsync"/>), the Board drag-and-drop path
    /// (<see cref="ReorderTicketAsync"/>), agent hand-offs (<see cref="TransitionTicketAsync"/>),
    /// and scheduled promotions (<see cref="PromoteScheduledAsync"/>) — so any route into a
    /// terminal status runs the exact same cleanup. Semantics are unchanged from R5: only a clean,
    /// merged worktree is ever removed; a dirty checkout, an unmerged branch, or a git failure is
    /// flagged (WorktreeStatus="dirty" + a worktree-cleanup-blocked/v1 comment receipt), never
    /// silently deleted. Guarded by WorktreeStatus != "cleaned" so re-entering Done — from either
    /// path, or twice in the same chain — never re-attempts cleanup: single ownership, no
    /// double-cleanup, by construction (there is exactly one call site per status transition).
    /// </summary>
    private async Task TryCleanupWorktreeOnDoneAsync(string projectSlug, Ticket ticket, CancellationToken ct = default)
    {
        if (!string.Equals(ticket.Status, "Done", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(ticket.WorktreeBranch)) return;
        if (string.Equals(ticket.WorktreeStatus, "cleaned", StringComparison.OrdinalIgnoreCase)) return;

        var project = await _projectService.GetProjectAsync(projectSlug);
        if (project is null) return;
        var workspace = _projectService.ResolveWorkspacePath(project);
        var branch = ticket.WorktreeBranch!;
        var path = ticket.WorktreePath!;

        WorktreeCleanupResult result;
        try
        {
            result = await WorktreeManager.TryCleanupAsync(workspace, branch, path, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "worktree cleanup: failed for ticket #{Id}", ticket.Id);
            return;
        }

        if (result.Outcome is WorktreeCleanupOutcome.Cleaned or WorktreeCleanupOutcome.AlreadyGone)
        {
            try { await SetWorktreeStateAsync(projectSlug, ticket.Id, branch, path, "cleaned"); }
            catch (Exception ex) { _logger?.LogWarning(ex, "worktree cleanup: failed to persist cleaned state for ticket #{Id}", ticket.Id); }
            _logger?.LogInformation(
                "worktree cleanup: ticket #{Id} branch {Branch} — {Outcome}", ticket.Id, branch, result.Outcome);
            return;
        }

        try { await SetWorktreeStateAsync(projectSlug, ticket.Id, branch, path, "dirty"); }
        catch (Exception ex) { _logger?.LogWarning(ex, "worktree cleanup: failed to persist dirty state for ticket #{Id}", ticket.Id); }

        var receipt = JsonSerializer.Serialize(new
        {
            schema = "worktree-cleanup-blocked/v1",
            ticketId = ticket.Id,
            branch,
            path,
            rule = "worktree-cleanup",
            outcome = result.Outcome.ToString(),
            reason = result.Reason ?? "worktree cleanup was blocked",
        });
        try { await AddCommentAsync(projectSlug, ticket.Id, receipt, "automation"); }
        catch (Exception ex) { _logger?.LogWarning(ex, "worktree cleanup: failed to write blocked receipt for ticket #{Id}", ticket.Id); }
        _logger?.LogWarning(
            "worktree cleanup: ticket #{Id} reached Done but the worktree was not cleaned ({Outcome}): {Reason}",
            ticket.Id, result.Outcome, result.Reason);
    }

    /// <summary>
    /// R5: durably records the ticket's git worktree state (branch, checkout path, status). Called
    /// by <c>ActionExecutor</c> right after a worktree is created/reused for a dispatch, and again
    /// when the ticket reaches Done and the worktree is cleaned up (or flagged instead — see
    /// <c>WorktreeManager.TryCleanupAsync</c>). Does not touch <see cref="Ticket.Status"/> or raise
    /// <see cref="TicketStatusChanged"/> — this is orchestration metadata, not a board transition.
    /// No-op (returns null) if the ticket does not exist.
    /// </summary>
    public async Task<Ticket?> SetWorktreeStateAsync(
        string projectSlug, int ticketId, string branch, string path, string status)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        ticket.WorktreeBranch = branch;
        ticket.WorktreePath = path;
        ticket.WorktreeStatus = status;
        ticket.WorktreeUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ticket;
    }

    /// <summary>
    /// Atomically changes status and, optionally, assignee. Agent hand-offs should
    /// use this method instead of two independent PATCH requests so the dispatcher
    /// never observes a new status with the old worker.
    /// </summary>
    public async Task<Ticket?> TransitionTicketAsync(
        string projectSlug,
        int ticketId,
        string newStatus,
        string? assignedTo,
        string author,
        string? expectedStatus = null)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Member '{assignedTo}' does not exist.");

        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);

        if (!await db.BoardColumns.AnyAsync(column => column.Name == newStatus))
            throw new InvalidOperationException($"Column '{newStatus}' does not exist.");

        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        if (expectedStatus is not null &&
            !string.Equals(ticket.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            throw new TicketTransitionConflictException(
                $"Ticket status changed concurrently: expected '{expectedStatus}', found '{ticket.Status}'.");
        }

        var oldStatus = ticket.Status;
        var oldAssignee = ticket.AssignedTo;
        var newAssignee = assignedTo is null ? oldAssignee : assignedTo.Length == 0 ? null : assignedTo;
        var statusChanged = !string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase);
        var assigneeChanged = !string.Equals(oldAssignee, newAssignee, StringComparison.OrdinalIgnoreCase);
        if (!statusChanged && !assigneeChanged) return ticket;

        ticket.Status = newStatus;
        ticket.AssignedTo = newAssignee;
        if (string.Equals(oldStatus, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(newStatus, "Scheduled", StringComparison.OrdinalIgnoreCase))
        {
            ticket.FireAt = null;
            ticket.ScheduleTarget = null;
        }
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"transitioned ticket: {oldStatus}/{oldAssignee ?? "unassigned"} → {newStatus}/{newAssignee ?? "unassigned"}"
        });
        await db.SaveChangesAsync();

        if (statusChanged)
        {
            await TryCleanupWorktreeOnDoneAsync(projectSlug, ticket);
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, newStatus);
        }
        return ticket;
    }

    /// <summary>
    /// Moves a ticket into the "Scheduled" column with a future <paramref name="fireAt"/> instant.
    /// The <see cref="ScheduledPromotionService"/> promotes it to <paramref name="targetStatus"/> once
    /// <paramref name="fireAt"/> is reached. This keeps calendar-dated work out of "Blocked".
    /// </summary>
    public async Task<Ticket?> ScheduleTicketAsync(string projectSlug, int ticketId, DateTime fireAt, string targetStatus = "Todo", string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (string.IsNullOrWhiteSpace(targetStatus))
            targetStatus = "Todo";
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var scheduledExists = await db.BoardColumns.AnyAsync(c => c.Name == "Scheduled");
        if (!scheduledExists)
            throw new InvalidOperationException("La colonne 'Scheduled' n'existe pas.");
        var targetExists = await db.BoardColumns.AnyAsync(c => c.Name == targetStatus);
        if (!targetExists)
            throw new InvalidOperationException($"La colonne cible '{targetStatus}' n'existe pas.");
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        ticket.Status = "Scheduled";
        ticket.FireAt = fireAt;
        ticket.ScheduleTarget = targetStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"scheduled the ticket for {fireAt:yyyy-MM-dd HH:mm} UTC → {targetStatus}"
        });
        await db.SaveChangesAsync();
        if (!string.Equals(oldStatus, "Scheduled", StringComparison.OrdinalIgnoreCase))
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, "Scheduled");
        return ticket;
    }

    /// <summary>
    /// Returns the ids of Scheduled tickets whose <c>FireAt</c> is due (&lt;= <paramref name="now"/>).
    /// </summary>
    public async Task<List<int>> ListDueScheduledTicketIdsAsync(string projectSlug, DateTime now)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureScheduleColumnsAsync(db);
        await EnsureDeliverableTypeColumnAsync(db);
        return await db.Tickets
            .Where(t => t.Status == "Scheduled" && t.FireAt != null && t.FireAt <= now)
            .OrderBy(t => t.FireAt)
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Promotes a Scheduled ticket to its <c>ScheduleTarget</c> (default "Todo"), clears
    /// <c>FireAt</c>, and fires <see cref="TicketStatusChanged"/> so automations (e.g. a
    /// <c>statusChange { from: "Scheduled" }</c> trigger) run. No-op if the ticket is not Scheduled.
    /// </summary>
    public async Task<Ticket?> PromoteScheduledAsync(string projectSlug, int ticketId, string author = "automation")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null || !string.Equals(ticket.Status, "Scheduled", StringComparison.OrdinalIgnoreCase))
            return null;
        var target = string.IsNullOrWhiteSpace(ticket.ScheduleTarget) ? "Todo" : ticket.ScheduleTarget!;
        var targetExists = await db.BoardColumns.AnyAsync(c => c.Name == target);
        if (!targetExists) target = "Todo";
        ticket.Status = target;
        ticket.FireAt = null;
        ticket.ScheduleTarget = null;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"schedule triggered: Scheduled → {target}"
        });
        await db.SaveChangesAsync();
        await TryCleanupWorktreeOnDoneAsync(projectSlug, ticket);
        TicketStatusChanged?.Invoke(projectSlug, ticketId, "Scheduled", target);
        return ticket;
    }

    public async Task<Ticket?> UpdateTicketAsync(string projectSlug, int ticketId, string? title = null, string? description = null, string author = "owner", TicketPriority? priority = null, string? assignedTo = null, string? deliverableType = null)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        var deliverable = deliverableType is null ? null : ResolveDeliverable(deliverableType);
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Member '{assignedTo}' does not exist.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;

        if (title is not null && title != ticket.Title)
        {
            var old = ticket.Title;
            ticket.Title = title;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"renamed the ticket: \"{old}\" → \"{title}\""
            });
        }
        if (description is not null && description != ticket.Description)
        {
            ticket.Description = description;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = "modified the description"
            });
        }
        if (priority is not null && priority != ticket.Priority)
        {
            var old = ticket.Priority;
            ticket.Priority = priority.Value;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"changed priority: {PriorityLabel(old)} → {PriorityLabel(priority.Value)}"
            });
        }
        if (assignedTo is not null && assignedTo != ticket.AssignedTo)
        {
            var old = ticket.AssignedTo ?? "nobody";
            ticket.AssignedTo = assignedTo.Length == 0 ? null : assignedTo;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"assigned the ticket: {old} → {ticket.AssignedTo ?? "nobody"}"
            });
        }
        if (deliverableType is not null)
        {
            var normalizedDeliverableType = deliverable?.Slug;
            if (!string.Equals(normalizedDeliverableType, ticket.DeliverableType, StringComparison.Ordinal))
            {
                var old = ticket.DeliverableType ?? "none";
                ticket.DeliverableType = normalizedDeliverableType;
                db.ActivityEntries.Add(new ActivityEntry
                {
                    TicketId = ticketId,
                    Author = author,
                    Text = $"changed deliverable type: {old} → {ticket.DeliverableType ?? "none"}"
                });
            }
        }
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ticket;
    }

    private static DeliverableDefinition? ResolveDeliverable(string? deliverableType)
    {
        if (string.IsNullOrWhiteSpace(deliverableType)) return null;
        if (DeliverableCatalog.TryGet(deliverableType, out var deliverable)) return deliverable;

        throw new InvalidOperationException($"Unknown deliverable type '{deliverableType.Trim()}'.");
    }

    public async Task<bool> DeleteTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        await EnsureTicketDependenciesTableAsync(db);
        var ticket = await db.Tickets
            .Include(t => t.Comments)
            .Include(t => t.Activities)
            .Include(t => t.BlockedByEdges)
            .Include(t => t.BlocksEdges)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return false;
        // Unparent any children before deleting
        var children = await db.Tickets.Where(t => t.ParentId == ticketId).ToListAsync();
        foreach (var child in children)
            child.ParentId = null;
        db.Comments.RemoveRange(ticket.Comments);
        db.ActivityEntries.RemoveRange(ticket.Activities);
        db.TicketDependencies.RemoveRange(ticket.BlockedByEdges);
        db.TicketDependencies.RemoveRange(ticket.BlocksEdges);
        db.Tickets.Remove(ticket);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetParentAsync(string projectSlug, int ticketId, int parentId, string author = "owner")
    {
        if (ticketId == parentId) return false;
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        var parent = await db.Tickets.FindAsync(parentId);
        if (ticket is null || parent is null) return false;
        // Prevent circular: parent must not itself be a child of ticketId
        if (parent.ParentId == ticketId) return false;
        ticket.ParentId = parentId;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"est devenu sous-ticket de #{parentId}"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UnparentAsync(string projectSlug, int ticketId, string author = "owner")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null || ticket.ParentId is null) return false;
        var oldParentId = ticket.ParentId.Value;
        ticket.ParentId = null;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"was unlinked from parent ticket #{oldParentId}"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Comment?> AddCommentAsync(string projectSlug, int ticketId, string content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var comment = new Comment
        {
            TicketId = ticketId,
            Content = content,
            Author = author
        };
        db.Comments.Add(comment);
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TicketCommentAdded?.Invoke(projectSlug, ticketId, author, content);
        return comment;
    }

    public async Task<bool> SetTicketLabelsAsync(string projectSlug, int ticketId, List<int> labelIds)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.Include(t => t.Labels).FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return false;
        var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToListAsync();
        ticket.Labels = labels;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Atomically adds and removes labels without requiring a read/replace cycle.
    /// This is the safe API for agents: concurrent label writers cannot erase labels
    /// they did not own. Unknown label ids requested for addition are rejected.
    /// </summary>
    public async Task<List<Label>?> PatchTicketLabelsAsync(
        string projectSlug,
        int ticketId,
        IReadOnlyCollection<int> addLabelIds,
        IReadOnlyCollection<int> removeLabelIds,
        string author)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");

        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);

        var ticket = await db.Tickets
            .Include(t => t.Labels)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return null;

        var remove = removeLabelIds.ToHashSet();
        ticket.Labels.RemoveAll(label => remove.Contains(label.Id));

        var current = ticket.Labels.Select(label => label.Id).ToHashSet();
        var requestedAdds = addLabelIds.Where(id => !current.Contains(id)).Distinct().ToList();
        if (requestedAdds.Count > 0)
        {
            var additions = await db.Labels.Where(label => requestedAdds.Contains(label.Id)).ToListAsync();
            var missing = requestedAdds.Except(additions.Select(label => label.Id)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Unknown label id(s): {string.Join(", ", missing)}.");
            ticket.Labels.AddRange(additions);
        }

        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"updated labels (+{string.Join(",", addLabelIds)}, -{string.Join(",", removeLabelIds)})"
        });
        await db.SaveChangesAsync();

        return ticket.Labels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> UpdateCommentAsync(string projectSlug, int ticketId, int commentId, string content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.TicketId != ticketId) return false;
        comment.Content = content;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = "modified a comment"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteCommentAsync(string projectSlug, int ticketId, int commentId, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("The 'author' field is required.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.TicketId != ticketId) return false;
        db.Comments.Remove(comment);
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = "deleted a comment"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task ReorderTicketAsync(string projectSlug, int ticketId, string newStatus, int targetIndex)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);

        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;

        var oldStatus = ticket.Status;
        var statusChanged = oldStatus != newStatus;
        ticket.Status = newStatus;
        ticket.UpdatedAt = DateTime.UtcNow;

        // Get all tickets in the target column (excluding the moved ticket)
        var columnTickets = await db.Tickets
            .Where(t => t.Status == newStatus && t.Id != ticketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .ToListAsync();

        // Clamp target index
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > columnTickets.Count) targetIndex = columnTickets.Count;

        // Insert ticket at target position and reassign sort orders
        columnTickets.Insert(targetIndex, ticket);
        for (int i = 0; i < columnTickets.Count; i++)
            columnTickets[i].SortOrder = i;

        if (statusChanged)
        {
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = "owner",
                Text = $"moved the ticket: {oldStatus} → {newStatus}"
            });
        }

        await db.SaveChangesAsync();
        if (statusChanged)
        {
            await TryCleanupWorktreeOnDoneAsync(projectSlug, ticket);
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, newStatus);
        }
    }

    private static string PriorityLabel(TicketPriority p) => p switch
    {
        TicketPriority.Idea => "Idea",
        TicketPriority.NiceToHave => "Nice to have",
        TicketPriority.Required => "Required",
        TicketPriority.Critical => "Critical",
        _ => p.ToString()
    };

    public async Task AddActivityAsync(string projectSlug, int ticketId, string text, string author = "automation")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;
        db.ActivityEntries.Add(new ActivityEntry { TicketId = ticketId, Author = author, Text = text });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns tickets where @handle appears in description or comments,
    /// optionally filtered by date range.
    /// </summary>
    public async Task<List<Ticket>> ListMentionedTicketsAsync(string projectSlug, string handle, DateTime? since = null, DateTime? until = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        await EnsureActivityTableAsync(db);
        await EnsureTicketEntityColumnsAsync(db);

        var mentionPattern = $"@{handle}";

        var tickets = await db.Tickets
            .Include(t => t.Labels)
            .Include(t => t.Comments)
            .Where(t => t.Description.Contains(mentionPattern)
                || t.Comments.Any(c => c.Content.Contains(mentionPattern)))
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        if (since.HasValue)
            tickets = tickets.Where(t => t.UpdatedAt >= since.Value).ToList();
        if (until.HasValue)
            tickets = tickets.Where(t => t.UpdatedAt <= until.Value).ToList();

        return tickets;
    }
}

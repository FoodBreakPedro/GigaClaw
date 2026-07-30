using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using GigaClaw.Core.Data;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

/// <summary>Raised for rejected team-store operations — carries a stable code for API/UI mapping.</summary>
public sealed class TeamStoreException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Everything needed to materialize one <see cref="TeamTask"/> from a graph node.</summary>
public sealed record TeamTaskDraft(string TemplateKey, string RoleId, string AgentSlug, int TicketId)
{
    /// <summary>Sibling template keys already added to the run that this task must wait for.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// Persistence for executable teams: definitions, runs and tasks.
/// <para>
/// Storage lives in the <b>per-project</b> SQLite database, next to the tickets, for two reasons.
/// A run is bound to a parent ticket and its tasks are sub-tickets, so run rows can carry real
/// foreign keys into <c>Tickets</c> and be cascaded away with them; and dependency edges between
/// tasks are ordinary <c>TicketDependencies</c> rows, which only exist in that same file. Keeping
/// runs anywhere else would mean a second, weaker copy of the board's own truth.
/// </para>
/// <para>
/// Resumability: every state change is a committed row write before any in-memory reaction. A
/// restarted engine can rebuild the world from <see cref="ListRunsAsync"/> (open runs), each run's
/// definition snapshot, <see cref="ListTasksAsync"/> and the live dependency edges — nothing about
/// a run exists only in memory.
/// </para>
/// The run lifecycle — fan-out, dispatch, the join, the synthesizer, cancellation — is deliberately
/// not here: this type only stores and reads the shapes <see cref="TeamRunService"/> operates on.
/// </summary>
public sealed class TeamStore
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ProjectService _projectService;
    private readonly TicketService _ticketService;
    private readonly AgentTeamService _roster;

    /// <param name="roster">
    /// Source of the built-in team definitions. Optional so every existing construction site keeps
    /// working; DI hands in the registered singleton.
    /// </param>
    public TeamStore(ProjectService projectService, TicketService ticketService, AgentTeamService? roster = null)
    {
        _projectService = projectService;
        _ticketService = ticketService;
        _roster = roster ?? new AgentTeamService();
    }

    // Inline migration in the repo's established shape: CREATE TABLE IF NOT EXISTS is idempotent,
    // adds no column to any existing table, and therefore cannot touch a single pre-existing row.
    // Databases created before C4 gain the three tables on first team-store call; databases created
    // after get them from EnsureCreated and this DDL is a no-op.
    private static Task EnsureTeamTablesAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "executable-teams", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TeamDefinitions (
                    Slug TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Icon TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    SeedHash TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """);
            // Databases created between C4 and the roster-as-data change have TeamDefinitions
            // without SeedHash. Adding a nullable column rewrites no row and loses nothing: every
            // pre-existing definition reads back as owner-authored, which is what it is.
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE TeamDefinitions ADD COLUMN SeedHash TEXT NULL");
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TeamRuns (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TeamSlug TEXT NOT NULL,
                    ParentTicketId INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    DefinitionSnapshot TEXT NOT NULL,
                    SynthesisTicketId INTEGER NULL,
                    FailureReason TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    CompletedAt TEXT NULL,
                    CONSTRAINT FK_TeamRuns_ParentTicket
                        FOREIGN KEY (ParentTicketId) REFERENCES Tickets (Id) ON DELETE CASCADE
                )
            """);
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_TeamRuns_ParentTicketId ON TeamRuns(ParentTicketId)");
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_TeamRuns_Status ON TeamRuns(Status)");
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TeamTasks (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TeamRunId INTEGER NOT NULL,
                    TemplateKey TEXT NOT NULL,
                    RoleId TEXT NOT NULL,
                    AgentSlug TEXT NOT NULL,
                    TicketId INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    DependsOn TEXT NOT NULL DEFAULT '[]',
                    ResultHandoffRef TEXT NULL,
                    FailureReason TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    CompletedAt TEXT NULL,
                    CONSTRAINT FK_TeamTasks_TeamRun
                        FOREIGN KEY (TeamRunId) REFERENCES TeamRuns (Id) ON DELETE CASCADE,
                    CONSTRAINT FK_TeamTasks_Ticket
                        FOREIGN KEY (TicketId) REFERENCES Tickets (Id) ON DELETE CASCADE
                )
            """);
            await d.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_TeamTasks_TeamRunId_TemplateKey ON TeamTasks(TeamRunId, TemplateKey)");
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_TeamTasks_TicketId ON TeamTasks(TicketId)");
        });

    // ---------------------------------------------------------------- seeding

    /// <summary>
    /// Ensures the roster's teams exist as rows of this project, once per database file per
    /// process. Runs on the first definition call rather than only at Initialize, so a project
    /// created before the roster became data migrates on its own.
    /// </summary>
    private Task EnsureDefinitionsSeededAsync(string projectSlug, TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db.DbPath, "team-definition-seed", () => SeedCoreAsync(projectSlug, db));

    /// <summary>
    /// Writes the workspace's team roster (see <see cref="AgentTeamService"/>) into this project's
    /// <c>TeamDefinitions</c>, and reports the slugs it touched.
    /// <para>
    /// <b>Nothing the owner authored is ever overwritten.</b> A slug with no row is inserted; a row
    /// that is still byte-identical to what a previous seed wrote is refreshed, so a change to a
    /// built-in team — or a pack contributing a member to one — reaches projects that already
    /// exist; every other row is left untouched, because a null or mismatched
    /// <see cref="TeamDefinitionRow.SeedHash"/> means a human edited it. Seeded rows are also what
    /// makes a data-added team resolvable per project through
    /// <see cref="TeamRunService.ResolveDefinitionAsync"/>.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> SeedDefinitionsAsync(string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        return await SeedCoreAsync(projectSlug, db);
    }

    private async Task<IReadOnlyList<string>> SeedCoreAsync(string projectSlug, TodoDbContext db)
    {
        var workspace = await ResolveWorkspaceAsync(projectSlug);
        var roster = _roster.GetDefinitions(workspace);
        if (roster.Count == 0) return [];

        var slugs = roster.Select(definition => definition.Slug).ToArray();
        var rows = await db.TeamDefinitions.Where(row => slugs.Contains(row.Slug)).ToListAsync();
        var bySlug = rows.ToDictionary(row => row.Slug, StringComparer.OrdinalIgnoreCase);

        var touched = new List<string>();
        var now = DateTime.UtcNow;
        foreach (var definition in roster)
        {
            // A structurally broken roster entry is skipped, not thrown on: one bad team must not
            // stop the other eight from reaching a project.
            if (definition.Validate().Count > 0) continue;

            var payload = JsonSerializer.Serialize(definition, PayloadJson);
            var hash = Sha256(payload);

            if (!bySlug.TryGetValue(definition.Slug, out var row))
            {
                db.TeamDefinitions.Add(new TeamDefinitionRow
                {
                    Slug = definition.Slug,
                    Name = definition.Name,
                    Description = definition.Description,
                    Icon = definition.Icon,
                    Payload = payload,
                    SeedHash = hash,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                touched.Add(definition.Slug);
                continue;
            }

            var isUntouchedSeed = row.SeedHash is not null && row.SeedHash == Sha256(row.Payload);
            if (!isUntouchedSeed || row.SeedHash == hash) continue;

            row.Name = definition.Name;
            row.Description = definition.Description;
            row.Icon = definition.Icon;
            row.Payload = payload;
            row.SeedHash = hash;
            row.UpdatedAt = now;
            touched.Add(definition.Slug);
        }

        if (touched.Count > 0) await db.SaveChangesAsync();
        return touched;
    }

    /// <summary>Workspace of a project, or null when it has none — then the roster is the built-in one.</summary>
    private async Task<string?> ResolveWorkspaceAsync(string projectSlug)
    {
        try
        {
            var project = await _projectService.GetProjectAsync(projectSlug);
            return project is null ? null : _projectService.ResolveWorkspacePath(project);
        }
        catch
        {
            // A project that is not in the registry (or a registry that cannot be opened) still gets
            // the built-in roster — team seeding must never be the thing that fails a project.
            return null;
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ---------------------------------------------------------------- definitions

    /// <summary>
    /// Inserts or replaces a project-scoped definition, keyed by slug. Structurally invalid
    /// definitions are refused here rather than at fan-out time, when a partial run would already exist.
    /// </summary>
    public async Task<TeamDefinition> SaveDefinitionAsync(string projectSlug, TeamDefinition definition)
    {
        var problems = definition.Validate();
        if (problems.Count > 0)
            throw new TeamDefinitionException(
                "definition_invalid",
                $"Team definition '{definition.Slug}' is invalid: {string.Join(" ", problems)}");

        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        await EnsureDefinitionsSeededAsync(projectSlug, db);
        var now = DateTime.UtcNow;
        var row = await db.TeamDefinitions.FirstOrDefaultAsync(d => d.Slug == definition.Slug);
        if (row is null)
        {
            row = new TeamDefinitionRow { Slug = definition.Slug, CreatedAt = now };
            db.TeamDefinitions.Add(row);
        }

        row.Name = definition.Name;
        row.Description = definition.Description;
        row.Icon = definition.Icon;
        row.Payload = JsonSerializer.Serialize(definition, PayloadJson);
        // This is an owner write, so the row stops being seed data: a later seed pass leaves it
        // alone even if the roster still ships the same slug.
        row.SeedHash = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
        return definition;
    }

    public async Task<TeamDefinition?> GetDefinitionAsync(string projectSlug, string teamSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        await EnsureDefinitionsSeededAsync(projectSlug, db);
        var row = await db.TeamDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Slug == teamSlug);
        return row is null ? null : ReadDefinition(row.Payload, row.Slug);
    }

    public async Task<IReadOnlyList<TeamDefinition>> ListDefinitionsAsync(string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        await EnsureDefinitionsSeededAsync(projectSlug, db);
        var rows = await db.TeamDefinitions.AsNoTracking().OrderBy(d => d.Slug).ToListAsync();
        return rows.Select(row => ReadDefinition(row.Payload, row.Slug)).ToArray();
    }

    public async Task<bool> DeleteDefinitionAsync(string projectSlug, string teamSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        await EnsureDefinitionsSeededAsync(projectSlug, db);
        var row = await db.TeamDefinitions.FirstOrDefaultAsync(d => d.Slug == teamSlug);
        if (row is null) return false;
        db.TeamDefinitions.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    // ---------------------------------------------------------------- runs

    /// <summary>
    /// Creates a run bound to <paramref name="parentTicketId"/>, snapshotting the definition so the
    /// run survives later edits to it. Filter-only definitions (empty task graph) are refused: they
    /// describe a member filter, not something to execute.
    /// </summary>
    public async Task<TeamRun> CreateRunAsync(string projectSlug, TeamDefinition definition, int parentTicketId)
    {
        var problems = definition.Validate();
        if (problems.Count > 0)
            throw new TeamDefinitionException(
                "definition_invalid",
                $"Team definition '{definition.Slug}' is invalid: {string.Join(" ", problems)}");
        if (!definition.IsExecutable)
            throw new TeamStoreException(
                "definition_not_executable",
                $"Team '{definition.Slug}' has an empty task graph and is a member filter, not a runnable team.");

        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        if (!await db.Tickets.AsNoTracking().AnyAsync(ticket => ticket.Id == parentTicketId))
            throw new TeamStoreException("parent_ticket_not_found", $"Ticket #{parentTicketId} does not exist.");

        var now = DateTime.UtcNow;
        var row = new TeamRunRow
        {
            TeamSlug = definition.Slug,
            ParentTicketId = parentTicketId,
            Status = TeamRunStatus.Pending,
            DefinitionSnapshot = JsonSerializer.Serialize(definition, PayloadJson),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TeamRuns.Add(row);
        await db.SaveChangesAsync();
        return ToRun(row);
    }

    public async Task<TeamRun?> GetRunAsync(string projectSlug, long runId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var row = await db.TeamRuns.AsNoTracking().FirstOrDefaultAsync(run => run.Id == runId);
        return row is null ? null : ToRun(row);
    }

    /// <summary>
    /// Lists runs, newest first. <paramref name="openOnly"/> yields exactly the set a restarting
    /// engine has to pick back up.
    /// </summary>
    public async Task<IReadOnlyList<TeamRun>> ListRunsAsync(
        string projectSlug,
        int? parentTicketId = null,
        bool openOnly = false)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var query = db.TeamRuns.AsNoTracking().AsQueryable();
        if (parentTicketId is not null)
            query = query.Where(run => run.ParentTicketId == parentTicketId.Value);
        if (openOnly)
            query = query.Where(run =>
                run.Status == TeamRunStatus.Pending ||
                run.Status == TeamRunStatus.Running ||
                run.Status == TeamRunStatus.Joining);
        var rows = await query.OrderByDescending(run => run.Id).ToListAsync();
        return rows.Select(ToRun).ToArray();
    }

    /// <summary>
    /// Moves a run to <paramref name="status"/>. Terminal states are final — a run that already
    /// completed, failed or was cancelled cannot be revived, so a late callback from a killed
    /// process cannot resurrect it after a restart.
    /// </summary>
    public async Task<TeamRun> UpdateRunStatusAsync(
        string projectSlug,
        long runId,
        TeamRunStatus status,
        string? failureReason = null,
        int? synthesisTicketId = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var row = await db.TeamRuns.FirstOrDefaultAsync(run => run.Id == runId)
            ?? throw new TeamStoreException("run_not_found", $"Team run #{runId} does not exist.");

        var wasOpen = row.Status is TeamRunStatus.Pending or TeamRunStatus.Running or TeamRunStatus.Joining;
        if (!wasOpen && row.Status != status)
            throw new TeamStoreException(
                "run_terminal",
                $"Team run #{runId} is already {row.Status} and cannot move to {status}.");

        var now = DateTime.UtcNow;
        row.Status = status;
        row.UpdatedAt = now;
        if (failureReason is not null) row.FailureReason = failureReason;
        if (synthesisTicketId is not null) row.SynthesisTicketId = synthesisTicketId;
        row.CompletedAt = status is TeamRunStatus.Completed or TeamRunStatus.Failed or TeamRunStatus.Cancelled
            ? row.CompletedAt ?? now
            : null;
        await db.SaveChangesAsync();
        return ToRun(row);
    }

    // ---------------------------------------------------------------- tasks

    /// <summary>
    /// Binds an existing sub-ticket to a run as a task and materializes its
    /// <see cref="TeamTaskDraft.DependsOn"/> edges through the ordinary ticket-dependency API, so
    /// blocking is expressed once, on the board. Dependencies must already be tasks of the same run,
    /// which means graph nodes are added in dependency order.
    /// </summary>
    public async Task<TeamTask> AddTaskAsync(string projectSlug, long runId, TeamTaskDraft draft)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);

        var run = await db.TeamRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId)
            ?? throw new TeamStoreException("run_not_found", $"Team run #{runId} does not exist.");
        if (run.Status is TeamRunStatus.Completed or TeamRunStatus.Failed or TeamRunStatus.Cancelled)
            throw new TeamStoreException("run_terminal", $"Team run #{runId} is {run.Status}; no task can be added.");
        if (!await db.Tickets.AsNoTracking().AnyAsync(ticket => ticket.Id == draft.TicketId))
            throw new TeamStoreException("task_ticket_not_found", $"Ticket #{draft.TicketId} does not exist.");

        var siblings = await db.TeamTasks.AsNoTracking().Where(task => task.TeamRunId == runId).ToListAsync();
        if (siblings.Any(task => string.Equals(task.TemplateKey, draft.TemplateKey, StringComparison.OrdinalIgnoreCase)))
            throw new TeamStoreException(
                "task_duplicate",
                $"Team run #{runId} already has a task for template key '{draft.TemplateKey}'.");

        var blockerTicketIds = new List<int>();
        foreach (var key in draft.DependsOn)
        {
            var sibling = siblings.FirstOrDefault(task =>
                string.Equals(task.TemplateKey, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new TeamStoreException(
                    "task_dependency_unknown",
                    $"Task '{draft.TemplateKey}' depends on '{key}', which is not a task of run #{runId}.");
            blockerTicketIds.Add(sibling.TicketId);
        }

        var now = DateTime.UtcNow;
        var row = new TeamTaskRow
        {
            TeamRunId = runId,
            TemplateKey = draft.TemplateKey,
            RoleId = draft.RoleId,
            AgentSlug = draft.AgentSlug,
            TicketId = draft.TicketId,
            Status = TeamTaskStatus.Pending,
            DependsOn = JsonSerializer.Serialize(draft.DependsOn, PayloadJson),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TeamTasks.Add(row);
        await db.SaveChangesAsync();

        // Edges go through TicketService so cycle detection, validation and the board's UpdatedAt
        // bookkeeping stay in one place. If an edge is refused, the half-built task is removed
        // rather than left behind as a node nothing waits on.
        var added = new List<int>();
        try
        {
            foreach (var blockerTicketId in blockerTicketIds.Distinct())
            {
                if (blockerTicketId == draft.TicketId) continue;
                try
                {
                    await _ticketService.AddTicketDependencyAsync(projectSlug, draft.TicketId, blockerTicketId);
                    added.Add(blockerTicketId);
                }
                catch (TicketDependencyException exception) when (exception.Code == "dependency_duplicate")
                {
                    // Edge already on the board (re-entrant fan-out): the intent is satisfied.
                }
            }
        }
        catch
        {
            foreach (var blockerTicketId in added)
                await _ticketService.RemoveTicketDependencyAsync(projectSlug, draft.TicketId, blockerTicketId);
            db.TeamTasks.Remove(row);
            await db.SaveChangesAsync();
            throw;
        }

        var blockedBy = await LoadBlockedByAsync(db, [draft.TicketId]);
        return ToTask(row, blockedBy.GetValueOrDefault(draft.TicketId) ?? []);
    }

    public async Task<IReadOnlyList<TeamTask>> ListTasksAsync(string projectSlug, long runId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var rows = await db.TeamTasks.AsNoTracking()
            .Where(task => task.TeamRunId == runId)
            .OrderBy(task => task.Id)
            .ToListAsync();
        var blockedBy = await LoadBlockedByAsync(db, rows.Select(row => row.TicketId).ToArray());
        return rows.Select(row => ToTask(row, blockedBy.GetValueOrDefault(row.TicketId) ?? [])).ToArray();
    }

    /// <summary>Maps a ticket back to the team task it carries, if any.</summary>
    public async Task<TeamTask?> GetTaskByTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var row = await db.TeamTasks.AsNoTracking().FirstOrDefaultAsync(task => task.TicketId == ticketId);
        if (row is null) return null;
        var blockedBy = await LoadBlockedByAsync(db, [ticketId]);
        return ToTask(row, blockedBy.GetValueOrDefault(ticketId) ?? []);
    }

    /// <summary>
    /// Records a task's outcome. <paramref name="resultHandoffRef"/> points at the handoff artifact
    /// the task produced (see doc/handoff-contract.md); the synthesizer reads them at join time.
    /// </summary>
    public async Task<TeamTask> UpdateTaskStatusAsync(
        string projectSlug,
        long taskId,
        TeamTaskStatus status,
        string? resultHandoffRef = null,
        string? failureReason = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTeamTablesAsync(db);
        var row = await db.TeamTasks.FirstOrDefaultAsync(task => task.Id == taskId)
            ?? throw new TeamStoreException("task_not_found", $"Team task #{taskId} does not exist.");

        var wasOpen = row.Status is TeamTaskStatus.Pending or TeamTaskStatus.Dispatched;
        if (!wasOpen && row.Status != status)
            throw new TeamStoreException(
                "task_terminal",
                $"Team task #{taskId} is already {row.Status} and cannot move to {status}.");

        var now = DateTime.UtcNow;
        row.Status = status;
        row.UpdatedAt = now;
        if (resultHandoffRef is not null) row.ResultHandoffRef = resultHandoffRef;
        if (failureReason is not null) row.FailureReason = failureReason;
        row.CompletedAt = status is TeamTaskStatus.Done or TeamTaskStatus.Failed or TeamTaskStatus.Cancelled
            ? row.CompletedAt ?? now
            : null;
        await db.SaveChangesAsync();

        var blockedBy = await LoadBlockedByAsync(db, [row.TicketId]);
        return ToTask(row, blockedBy.GetValueOrDefault(row.TicketId) ?? []);
    }

    // ---------------------------------------------------------------- mapping

    private static async Task<Dictionary<int, List<int>>> LoadBlockedByAsync(TodoDbContext db, IReadOnlyList<int> ticketIds)
    {
        if (ticketIds.Count == 0) return [];
        var edges = await db.TicketDependencies.AsNoTracking()
            .Where(edge => ticketIds.Contains(edge.BlockedTicketId))
            .Select(edge => new { edge.BlockedTicketId, edge.BlockingTicketId })
            .ToListAsync();
        return edges
            .GroupBy(edge => edge.BlockedTicketId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.BlockingTicketId).OrderBy(id => id).ToList());
    }

    private static TeamDefinition ReadDefinition(string payload, string slug)
    {
        try
        {
            return JsonSerializer.Deserialize<TeamDefinition>(payload, PayloadJson)
                ?? throw new TeamDefinitionException("definition_unreadable", $"Team definition '{slug}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new TeamDefinitionException(
                "definition_unreadable",
                $"Team definition '{slug}' could not be read: {exception.Message}");
        }
    }

    private static TeamRun ToRun(TeamRunRow row) =>
        new(
            row.Id,
            row.TeamSlug,
            row.ParentTicketId,
            row.Status,
            ReadDefinition(row.DefinitionSnapshot, row.TeamSlug),
            row.CreatedAt,
            row.UpdatedAt)
        {
            SynthesisTicketId = row.SynthesisTicketId,
            FailureReason = row.FailureReason,
            CompletedAt = row.CompletedAt
        };

    private static TeamTask ToTask(TeamTaskRow row, IReadOnlyList<int> blockedByTicketIds) =>
        new(
            row.Id,
            row.TeamRunId,
            row.TemplateKey,
            row.RoleId,
            row.AgentSlug,
            row.TicketId,
            row.Status,
            row.CreatedAt,
            row.UpdatedAt)
        {
            DependsOn = JsonSerializer.Deserialize<string[]>(row.DependsOn, PayloadJson) ?? [],
            BlockedByTicketIds = blockedByTicketIds,
            ResultHandoffRef = row.ResultHandoffRef,
            FailureReason = row.FailureReason,
            CompletedAt = row.CompletedAt
        };
}

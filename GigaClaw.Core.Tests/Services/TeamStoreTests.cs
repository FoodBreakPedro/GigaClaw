using GigaClaw.Core.Automation;
using GigaClaw.Core.Data;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace GigaClaw.Core.Tests.Services;

public sealed class TeamStoreTests
{
    private static async Task<(ProjectService Projects, TicketService Tickets, TeamStore Teams, string Slug)> BuildSutAsync(
        TempDir tmp,
        string name)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(name);
        var tickets = new TicketService(projects, new MemberService(projects));
        return (projects, tickets, new TeamStore(projects, tickets), project.Slug);
    }

    private static TeamDefinition ParallelReviewDefinition() =>
        new("parallel-review-fixture", "Parallel review", "Two lanes then a synthesis.", "🔍")
        {
            Roles =
            [
                new TeamRole("security", "programmer") { Description = "Security lane." },
                new TeamRole("performance", "qa-tester"),
                new TeamRole("lead", "producer")
            ],
            EntryConditions =
            [
                new TicketInColumnConditionSpec { Columns = ["Review"] },
                new PriorityConditionSpec { Negate = true }
            ],
            TaskGraph =
            [
                new TeamTaskTemplate("security-lane", "security", "Security review") { Prompt = "Audit the diff." },
                new TeamTaskTemplate("performance-lane", "performance", "Performance review"),
                new TeamTaskTemplate("dedup", "lead", "Deduplicate findings")
                {
                    DependsOn = ["security-lane", "performance-lane"]
                }
            ],
            JoinPolicy = TeamJoinPolicy.OfQuorum(2),
            SynthesizerRole = "lead"
        };

    [Fact]
    public async Task Definition_RoundTripsThroughTheStore()
    {
        using var tmp = new TempDir();
        var (_, _, teams, slug) = await BuildSutAsync(tmp, "team-definition-roundtrip");
        var definition = ParallelReviewDefinition();

        await teams.SaveDefinitionAsync(slug, definition);
        var loaded = await teams.GetDefinitionAsync(slug, definition.Slug);

        Assert.NotNull(loaded);
        Assert.Equal(definition.Slug, loaded.Slug);
        Assert.Equal(definition.Name, loaded.Name);
        Assert.Equal(definition.Description, loaded.Description);
        Assert.Equal(definition.Icon, loaded.Icon);
        Assert.Equal(["security", "performance", "lead"], loaded.Roles.Select(role => role.RoleId));
        Assert.Equal(["programmer", "qa-tester", "producer"], loaded.AgentSlugs);
        Assert.Equal("Security lane.", loaded.Roles[0].Description);
        Assert.Equal(TeamJoinMode.Quorum, loaded.JoinPolicy.Mode);
        Assert.Equal(2, loaded.JoinPolicy.Quorum);
        Assert.Equal("lead", loaded.SynthesizerRole);
        Assert.True(loaded.IsExecutable);

        // Entry conditions keep their automation-vocabulary types across the round trip.
        var column = Assert.IsType<TicketInColumnConditionSpec>(loaded.EntryConditions[0]);
        Assert.Equal(["Review"], column.Columns);
        Assert.True(Assert.IsType<PriorityConditionSpec>(loaded.EntryConditions[1]).Negate);

        var dedup = Assert.Single(loaded.TaskGraph, task => task.Key == "dedup");
        Assert.Equal(["security-lane", "performance-lane"], dedup.DependsOn);
        Assert.Equal("Audit the diff.", loaded.FindTask("security-lane")!.Prompt);

        var listed = Assert.Single(await teams.ListDefinitionsAsync(slug));
        Assert.Equal(definition.Slug, listed.Slug);
        Assert.True(await teams.DeleteDefinitionAsync(slug, definition.Slug));
        Assert.Null(await teams.GetDefinitionAsync(slug, definition.Slug));
    }

    [Fact]
    public async Task SaveDefinition_RejectsStructurallyBrokenGraphs()
    {
        using var tmp = new TempDir();
        var (_, _, teams, slug) = await BuildSutAsync(tmp, "team-definition-validation");

        var cyclic = new TeamDefinition("cyclic", "Cyclic", "", "🔁")
        {
            Roles = [new TeamRole("lead", "producer")],
            TaskGraph =
            [
                new TeamTaskTemplate("a", "lead", "A") { DependsOn = ["b"] },
                new TeamTaskTemplate("b", "lead", "B") { DependsOn = ["a"] }
            ]
        };
        Assert.Contains(cyclic.Validate(), problem => problem.Contains("dependency cycle"));

        var saveCyclic = await Assert.ThrowsAsync<TeamDefinitionException>(
            () => teams.SaveDefinitionAsync(slug, cyclic));
        Assert.Equal("definition_invalid", saveCyclic.Code);

        var danglingRole = new TeamDefinition("dangling", "Dangling", "", "❓")
        {
            Roles = [new TeamRole("lead", "producer")],
            TaskGraph = [new TeamTaskTemplate("a", "ghost", "A")],
            SynthesizerRole = "phantom"
        };
        var problems = danglingRole.Validate();
        Assert.Contains(problems, problem => problem.Contains("unknown role 'ghost'"));
        Assert.Contains(problems, problem => problem.Contains("Synthesizer role 'phantom'"));

        var badQuorum = new TeamDefinition("quorum", "Quorum", "", "🔢")
        {
            Roles = [new TeamRole("lead", "producer")],
            TaskGraph = [new TeamTaskTemplate("a", "lead", "A")],
            JoinPolicy = TeamJoinPolicy.OfQuorum(3)
        };
        Assert.Contains(badQuorum.Validate(), problem => problem.Contains("exceeds"));
    }

    [Fact]
    public async Task ExistingDatabase_MigratesTeamTablesWithoutTicketDataLoss()
    {
        using var tmp = new TempDir();
        var (projects, tickets, teams, slug) = await BuildSutAsync(tmp, "team-migration");
        var parent = await tickets.CreateTicketAsync(slug, "Keep me", status: "Review");
        await tickets.AddCommentAsync(slug, parent.Id, "Keep this comment too.");

        // Simulate a board created before C4: no team tables at all, and no memoized migration.
        var dbPath = projects.GetProjectDbPath(slug);
        await using (var legacy = projects.GetProjectDb(slug))
        {
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TeamTasks");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TeamRuns");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TeamDefinitions");
        }
        MigrationGate.Invalidate(dbPath);

        var definition = ParallelReviewDefinition();
        await teams.SaveDefinitionAsync(slug, definition);
        var run = await teams.CreateRunAsync(slug, definition, parent.Id);

        Assert.Equal(parent.Id, run.ParentTicketId);
        await using var migrated = projects.GetProjectDb(slug);
        var keptTicket = await migrated.Tickets.SingleAsync();
        Assert.Equal("Keep me", keptTicket.Title);
        Assert.Equal("Review", keptTicket.Status);
        Assert.Equal(1, await migrated.Comments.CountAsync());
        Assert.Equal(1, await migrated.TeamDefinitions.CountAsync());
        Assert.Equal(1, await migrated.TeamRuns.CountAsync());
        Assert.Equal(0, await migrated.TeamTasks.CountAsync());

        var indexes = await migrated.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name IN ('TeamRuns', 'TeamTasks')")
            .ToListAsync();
        Assert.Contains("IX_TeamRuns_ParentTicketId", indexes);
        Assert.Contains("IX_TeamTasks_TeamRunId_TemplateKey", indexes);
    }

    [Fact]
    public async Task Run_BoundToParentTicket_SurvivesStoreReload()
    {
        using var tmp = new TempDir();
        var (projects, tickets, teams, slug) = await BuildSutAsync(tmp, "team-run-reload");
        var parent = await tickets.CreateTicketAsync(slug, "Ship the release", status: "Review");
        var definition = ParallelReviewDefinition();
        var created = await teams.CreateRunAsync(slug, definition, parent.Id);
        await teams.UpdateRunStatusAsync(slug, created.Id, TeamRunStatus.Running);

        // A fresh process: new services over the same data directory, nothing carried in memory.
        var reloadedProjects = new ProjectService(tmp.Path);
        var reloadedTickets = new TicketService(reloadedProjects, new MemberService(reloadedProjects));
        var reloadedStore = new TeamStore(reloadedProjects, reloadedTickets);

        var reloaded = await reloadedStore.GetRunAsync(slug, created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(parent.Id, reloaded.ParentTicketId);
        Assert.Equal(definition.Slug, reloaded.TeamSlug);
        Assert.Equal(TeamRunStatus.Running, reloaded.Status);
        Assert.True(reloaded.IsOpen);

        // The definition snapshot travels with the run, so a resume needs nothing else.
        Assert.Equal(TeamJoinMode.Quorum, reloaded.JoinPolicy.Mode);
        Assert.Equal(2, reloaded.JoinPolicy.Quorum);
        Assert.Equal("lead", reloaded.SynthesizerRole);
        Assert.Equal(3, reloaded.Definition.TaskGraph.Count);

        var open = await reloadedStore.ListRunsAsync(slug, openOnly: true);
        Assert.Equal(created.Id, Assert.Single(open).Id);
        Assert.Equal(created.Id, Assert.Single(await reloadedStore.ListRunsAsync(slug, parentTicketId: parent.Id)).Id);

        // An edited definition does not rewrite a run already in flight.
        await reloadedStore.SaveDefinitionAsync(slug, definition with { JoinPolicy = TeamJoinPolicy.AllDone });
        Assert.Equal(TeamJoinMode.Quorum, (await reloadedStore.GetRunAsync(slug, created.Id))!.JoinPolicy.Mode);

        var completed = await reloadedStore.UpdateRunStatusAsync(slug, created.Id, TeamRunStatus.Completed);
        Assert.NotNull(completed.CompletedAt);
        Assert.False(completed.IsOpen);
        Assert.Empty(await reloadedStore.ListRunsAsync(slug, openOnly: true));
        var terminal = await Assert.ThrowsAsync<TeamStoreException>(
            () => reloadedStore.UpdateRunStatusAsync(slug, created.Id, TeamRunStatus.Running));
        Assert.Equal("run_terminal", terminal.Code);
    }

    [Fact]
    public async Task Task_CarriesRealTicketDependencyEdges()
    {
        using var tmp = new TempDir();
        var (_, tickets, teams, slug) = await BuildSutAsync(tmp, "team-task-edges");
        var parent = await tickets.CreateTicketAsync(slug, "Review the release", status: "Review");
        var definition = ParallelReviewDefinition();
        var run = await teams.CreateRunAsync(slug, definition, parent.Id);

        var securityTicket = await tickets.CreateTicketAsync(slug, "Security review");
        var performanceTicket = await tickets.CreateTicketAsync(slug, "Performance review");
        var dedupTicket = await tickets.CreateTicketAsync(slug, "Deduplicate findings");

        await teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", securityTicket.Id));
        await teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("performance-lane", "performance", "qa-tester", performanceTicket.Id));
        var dedup = await teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("dedup", "lead", "producer", dedupTicket.Id)
        {
            DependsOn = ["security-lane", "performance-lane"]
        });

        // The edges are ordinary board dependencies, not a private table.
        Assert.Equal([securityTicket.Id, performanceTicket.Id], dedup.BlockedByTicketIds.Order());
        Assert.Equal(["security-lane", "performance-lane"], dedup.DependsOn);
        var boardDependencies = await tickets.GetTicketDependenciesAsync(slug, dedupTicket.Id);
        Assert.NotNull(boardDependencies);
        Assert.Equal([securityTicket.Id, performanceTicket.Id], boardDependencies.BlockedBy.Select(edge => edge.Id).Order());
        Assert.Equal(dedupTicket.Id, Assert.Single(
            (await tickets.GetTicketDependenciesAsync(slug, securityTicket.Id))!.Blocks).Id);

        var tasks = await teams.ListTasksAsync(slug, run.Id);
        Assert.Equal(3, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(TeamTaskStatus.Pending, task.Status));
        Assert.Equal(dedup.Id, (await teams.GetTaskByTicketAsync(slug, dedupTicket.Id))!.Id);

        // Removing the edge on the board changes what the task waits on — the board stays the truth.
        Assert.True(await tickets.RemoveTicketDependencyAsync(slug, dedupTicket.Id, securityTicket.Id));
        var afterRemoval = await teams.GetTaskByTicketAsync(slug, dedupTicket.Id);
        Assert.Equal([performanceTicket.Id], afterRemoval!.BlockedByTicketIds);
        Assert.Equal(["security-lane", "performance-lane"], afterRemoval.DependsOn);

        var done = await teams.UpdateTaskStatusAsync(
            slug, dedup.Id, TeamTaskStatus.Done, resultHandoffRef: "runs/run-42/handoff.json");
        Assert.Equal("runs/run-42/handoff.json", done.ResultHandoffRef);
        Assert.NotNull(done.CompletedAt);
        Assert.False(done.IsOpen);
    }

    [Fact]
    public async Task AddTask_RejectsUnknownDependenciesDuplicatesAndMissingTickets()
    {
        using var tmp = new TempDir();
        var (_, tickets, teams, slug) = await BuildSutAsync(tmp, "team-task-validation");
        var parent = await tickets.CreateTicketAsync(slug, "Parent");
        var run = await teams.CreateRunAsync(slug, ParallelReviewDefinition(), parent.Id);
        var securityTicket = await tickets.CreateTicketAsync(slug, "Security review");

        var missingTicket = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", 999_999)));
        Assert.Equal("task_ticket_not_found", missingTicket.Code);

        var unknownDependency = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", securityTicket.Id)
            {
                DependsOn = ["performance-lane"]
            }));
        Assert.Equal("task_dependency_unknown", unknownDependency.Code);
        Assert.Empty(await teams.ListTasksAsync(slug, run.Id));

        await teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", securityTicket.Id));
        var otherTicket = await tickets.CreateTicketAsync(slug, "Duplicate key");
        var duplicate = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", otherTicket.Id)));
        Assert.Equal("task_duplicate", duplicate.Code);

        var missingRun = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.AddTaskAsync(slug, 999_999, new TeamTaskDraft("ghost", "security", "programmer", otherTicket.Id)));
        Assert.Equal("run_not_found", missingRun.Code);
    }

    [Fact]
    public async Task CreateRun_RefusesFilterOnlyDefinitionsAndMissingParents()
    {
        using var tmp = new TempDir();
        var (_, tickets, teams, slug) = await BuildSutAsync(tmp, "team-run-guards");
        var parent = await tickets.CreateTicketAsync(slug, "Parent");
        var filterOnly = new AgentTeamService().GetDefinitionBySlug(AgentTeamService.SoftwareEngineeringSlug)!;

        var notExecutable = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.CreateRunAsync(slug, filterOnly, parent.Id));
        Assert.Equal("definition_not_executable", notExecutable.Code);

        var missingParent = await Assert.ThrowsAsync<TeamStoreException>(
            () => teams.CreateRunAsync(slug, ParallelReviewDefinition(), 999_999));
        Assert.Equal("parent_ticket_not_found", missingParent.Code);
    }

    [Fact]
    public async Task DeletingTheParentTicket_RemovesRunAndTasks()
    {
        using var tmp = new TempDir();
        var (projects, tickets, teams, slug) = await BuildSutAsync(tmp, "team-run-cascade");
        var parent = await tickets.CreateTicketAsync(slug, "Parent");
        var run = await teams.CreateRunAsync(slug, ParallelReviewDefinition(), parent.Id);
        var childTicket = await tickets.CreateTicketAsync(slug, "Security review");
        await teams.AddTaskAsync(slug, run.Id, new TeamTaskDraft("security-lane", "security", "programmer", childTicket.Id));

        Assert.True(await tickets.DeleteTicketAsync(slug, parent.Id));

        Assert.Null(await teams.GetRunAsync(slug, run.Id));
        await using var db = projects.GetProjectDb(slug);
        Assert.Equal(0, await db.TeamRuns.CountAsync());
        Assert.Equal(0, await db.TeamTasks.CountAsync());
    }

    [Fact]
    public void BuiltInTeams_AreValidFilterOnlyDefinitions()
    {
        var service = new AgentTeamService();
        var definitions = service.GetDefinitions();

        Assert.Equal(9, definitions.Count);
        Assert.All(definitions, definition =>
        {
            Assert.Empty(definition.Validate());
            Assert.Empty(definition.TaskGraph);
            Assert.False(definition.IsExecutable);
            Assert.Null(definition.SynthesizerRole);
            Assert.Empty(definition.EntryConditions);
            Assert.Equal(TeamJoinMode.AllDone, definition.JoinPolicy.Mode);
        });

        // The filter surface is exactly what the definitions project: no behavior moved.
        var teams = service.GetTeams();
        Assert.Equal(definitions.Count, teams.Count);
        foreach (var (definition, team) in definitions.Zip(teams))
        {
            Assert.Equal(definition.Slug, team.Slug);
            Assert.Equal(definition.Name, team.Name);
            Assert.Equal(definition.Description, team.Description);
            Assert.Equal(definition.Icon, team.Icon);
            Assert.Equal(definition.AgentSlugs, team.AgentSlugs);
            Assert.Equal(definition.Roles.Select(role => role.AgentSlug), team.AgentSlugs);
        }
    }
}

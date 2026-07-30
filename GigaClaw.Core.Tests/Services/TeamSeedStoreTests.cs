using GigaClaw.Core.Data;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Seeding the data roster into a project's <c>TeamDefinitions</c>. Two properties matter: a
/// project that already exists gains the roster without losing a row, and a team that only ever
/// existed as data resolves per project — which is what a pack needs and what C# constants could
/// not give it.
/// </summary>
public sealed class TeamSeedStoreTests
{
    private sealed record Sut(
        ProjectService Projects,
        TicketService Tickets,
        MemberService Members,
        TeamStore Teams,
        string Slug,
        string Workspace);

    private static async Task<Sut> BuildAsync(TempDir tmp, string name)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(name);
        var workspace = Path.Combine(tmp.Path, "workspaces", project.Slug);
        Directory.CreateDirectory(workspace);
        await projects.UpdateProjectAsync(project.Slug, workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return new Sut(projects, tickets, members, new TeamStore(projects, tickets), project.Slug, workspace);
    }

    private static void WriteRoster(string workspace, string json)
    {
        var path = TeamSeed.WorkspacePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static TeamDefinition OwnerDefinition(string slug) =>
        new(slug, "Owner's team", "Hand-built.", "🔧")
        {
            Roles = [new TeamRole("lead", "producer")],
            TaskGraph = [new TeamTaskTemplate("only", "lead", "Do the thing")],
            SynthesizerRole = "lead"
        };

    [Fact]
    public async Task NewProject_SeedsTheBuiltInRosterOnFirstDefinitionCall()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-new");

        var listed = await sut.Teams.ListDefinitionsAsync(sut.Slug);

        var expected = new AgentTeamService().GetDefinitions().Select(definition => definition.Slug);
        Assert.All(expected, slug => Assert.Contains(listed, definition => definition.Slug == slug));
        Assert.Equal(9, listed.Count);

        // Seat-for-seat identical to the roster, so nothing about filtering or resolution changed.
        var seeded = listed.Single(definition => definition.Slug == AgentTeamService.SoftwareEngineeringSlug);
        Assert.Equal(
            new AgentTeamService().GetDefinitionBySlug(AgentTeamService.SoftwareEngineeringSlug)!.AgentSlugs,
            seeded.AgentSlugs);
        Assert.False(seeded.IsExecutable);
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-idempotent");

        var first = await sut.Teams.SeedDefinitionsAsync(sut.Slug);
        var second = await sut.Teams.SeedDefinitionsAsync(sut.Slug);

        Assert.Equal(9, first.Count);
        Assert.Empty(second);
        Assert.Equal(9, (await sut.Teams.ListDefinitionsAsync(sut.Slug)).Count);
    }

    [Fact]
    public async Task ExistingProjectDb_MigratesWithoutLosingDefinitionsRunsOrTickets()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-migration");

        // A project as it stood before the roster became data: its own definitions, one of them
        // deliberately claiming a built-in slug, plus a run in flight.
        var owned = OwnerDefinition("owner-only");
        var shadowing = OwnerDefinition(AgentTeamService.SoftwareEngineeringSlug);
        await sut.Teams.SaveDefinitionAsync(sut.Slug, owned);
        await sut.Teams.SaveDefinitionAsync(sut.Slug, shadowing);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Ship it", status: "Review");
        var run = await sut.Teams.CreateRunAsync(sut.Slug, owned, parent.Id);

        var dbPath = sut.Projects.GetProjectDbPath(sut.Slug);
        await using (var legacy = sut.Projects.GetProjectDb(sut.Slug))
        {
            // Strip the seed rows so the file looks like one this build has never touched.
            await legacy.Database.ExecuteSqlRawAsync("DELETE FROM TeamDefinitions WHERE SeedHash IS NOT NULL");
            Assert.Equal(2, await legacy.TeamDefinitions.CountAsync());
        }
        MigrationGate.Invalidate(dbPath);

        var touched = await sut.Teams.SeedDefinitionsAsync(sut.Slug);

        // Eight of the nine land; software-engineering is the owner's and is not one of them.
        Assert.Equal(8, touched.Count);
        Assert.DoesNotContain(AgentTeamService.SoftwareEngineeringSlug, touched);

        await using var migrated = sut.Projects.GetProjectDb(sut.Slug);
        Assert.Equal(10, await migrated.TeamDefinitions.CountAsync());
        Assert.Equal(2, await migrated.TeamDefinitions.CountAsync(row => row.SeedHash == null));

        // Every pre-existing row survived byte-for-byte.
        var keptShadow = await sut.Teams.GetDefinitionAsync(sut.Slug, AgentTeamService.SoftwareEngineeringSlug);
        Assert.NotNull(keptShadow);
        Assert.Equal("Owner's team", keptShadow.Name);
        Assert.True(keptShadow.IsExecutable);
        Assert.Equal("Owner's team", (await sut.Teams.GetDefinitionAsync(sut.Slug, "owner-only"))!.Name);

        // And so did the board and the run bound to it.
        Assert.Equal(1, await migrated.TeamRuns.CountAsync());
        Assert.Equal(run.Id, (await sut.Teams.GetRunAsync(sut.Slug, run.Id))!.Id);
        Assert.Equal("Ship it", (await migrated.Tickets.SingleAsync(ticket => ticket.Id == parent.Id)).Title);
    }

    [Fact]
    public async Task Seed_RefreshesAnUntouchedSeedRowButNeverAnOwnerEditedOne()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-refresh");
        await sut.Teams.SeedDefinitionsAsync(sut.Slug);

        // A pack joins one built-in team and the owner takes over another.
        await sut.Teams.SaveDefinitionAsync(sut.Slug, OwnerDefinition(AgentTeamService.GovernanceOpsSlug));
        WriteRoster(sut.Workspace, """
            {
              "schemaVersion": 1,
              "teams": [
                { "slug": "software-engineering", "name": "Software Engineering", "description": "d",
                  "icon": "💻", "agentSlugs": ["programmer", "producer"] },
                { "slug": "governance-ops", "name": "Governance & Ops", "description": "d",
                  "icon": "🛡️", "agentSlugs": ["approval-gatekeeper"] }
              ],
              "teamMembership": { "software-engineering": ["security-auditor"] }
            }
            """);

        var touched = await sut.Teams.SeedDefinitionsAsync(sut.Slug);

        // The untouched seed row picks up the new seat…
        Assert.Contains(AgentTeamService.SoftwareEngineeringSlug, touched);
        Assert.Equal(
            ["programmer", "producer", "security-auditor"],
            (await sut.Teams.GetDefinitionAsync(sut.Slug, AgentTeamService.SoftwareEngineeringSlug))!.AgentSlugs);

        // …and the owner-edited one is left exactly as the owner left it.
        Assert.DoesNotContain(AgentTeamService.GovernanceOpsSlug, touched);
        var owner = await sut.Teams.GetDefinitionAsync(sut.Slug, AgentTeamService.GovernanceOpsSlug);
        Assert.Equal("Owner's team", owner!.Name);
        Assert.True(owner.IsExecutable);
    }

    [Fact]
    public async Task DataAddedTeam_ResolvesThroughTheProjectWithoutRecompiling()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-pack-team");
        WriteRoster(sut.Workspace, """
            [
              { "slug": "all", "name": "All Teams", "description": "d", "icon": "👥", "agentSlugs": [] },
              { "slug": "security-review", "name": "Security Review", "description": "d", "icon": "🔐",
                "roles": [
                  { "roleId": "audit", "agentSlug": "programmer" },
                  { "roleId": "lead", "agentSlug": "producer" }
                ],
                "taskGraph": [{ "key": "audit-lane", "roleId": "audit", "title": "Audit the diff" }],
                "synthesizerRole": "lead" }
            ]
            """);

        var seeded = await sut.Teams.SeedDefinitionsAsync(sut.Slug);
        Assert.Contains("security-review", seeded);

        var runs = new TeamRunService(
            sut.Teams, sut.Tickets, sut.Members, new AgentTeamService(), NullLogger<TeamRunService>.Instance);

        // Resolution: a team that exists only as data is a first-class team of the project.
        var resolved = await runs.ResolveDefinitionAsync(sut.Slug, "security-review");
        Assert.NotNull(resolved);
        Assert.True(resolved.IsExecutable);
        Assert.Equal(["programmer", "producer"], resolved.AgentSlugs);

        // And it is runnable: fan-out produces its lane and its synthesis seat.
        await new AgentsTemplateService().EnsureAgentMembersAsync(sut.Slug, sut.Members);
        var parent = await sut.Tickets.CreateTicketAsync(sut.Slug, "Review the release", status: "Review");
        var run = await runs.StartRunAsync(sut.Slug, "security-review", parent.Id);
        var task = Assert.Single(await sut.Teams.ListTasksAsync(sut.Slug, run.Id));
        Assert.Equal("audit-lane", task.TemplateKey);
        Assert.Equal("programmer", task.AgentSlug);
    }

    [Fact]
    public async Task DataAddedMembership_ReachesAnAlreadySeededProject()
    {
        using var tmp = new TempDir();
        var sut = await BuildAsync(tmp, "team-seed-pack-membership");
        await sut.Teams.SeedDefinitionsAsync(sut.Slug);

        var before = await sut.Teams.GetDefinitionAsync(sut.Slug, AgentTeamService.DataIntelligenceSlug);
        Assert.DoesNotContain("security-auditor", before!.AgentSlugs);

        // The pack composes only a membership — it declares no team of its own.
        var core = AgentTeamService.ReadEmbeddedTeamsJson()!;
        WriteRoster(sut.Workspace, core.Replace(
            "\"teamMembership\": {}",
            "\"teamMembership\": { \"data-intelligence\": [\"security-auditor\"] }"));

        Assert.Contains(AgentTeamService.DataIntelligenceSlug, await sut.Teams.SeedDefinitionsAsync(sut.Slug));

        var after = await sut.Teams.GetDefinitionAsync(sut.Slug, AgentTeamService.DataIntelligenceSlug);
        Assert.Contains("security-auditor", after!.AgentSlugs);
        // Additive: the seats that were there are still there, in order.
        Assert.Equal(before.AgentSlugs, after.AgentSlugs.Take(before.AgentSlugs.Count));
    }
}

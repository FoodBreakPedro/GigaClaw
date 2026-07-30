using System.Text.Json;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Xunit;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// The team roster is data. These tests hold the line on the two things that makes true: the
/// shipped <c>ProjectTemplate/Agents/teams.json</c> still resolves to exactly the nine built-ins
/// that used to be C# constants, and a roster a pack composes into a workspace adds a team or a
/// membership with no recompilation of <c>GigaClaw.Core</c>.
/// </summary>
public sealed class TeamSeedTests
{
    private static readonly string TemplateTeamsFile =
        Path.Combine(PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents", TeamSeed.FileName);

    private static readonly JsonSerializerOptions Comparable = new() { WriteIndented = false };

    /// <summary>Structural comparison — records hold lists, so record equality is reference equality.</summary>
    private static string Shape(TeamDefinition definition) => JsonSerializer.Serialize(definition, Comparable);

    private static IReadOnlyList<string> Shapes(IEnumerable<TeamDefinition> definitions) =>
        [.. definitions.Select(Shape)];

    private static string WriteWorkspaceRoster(string workspace, string json)
    {
        var path = TeamSeed.WorkspacePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void EmbeddedRoster_IsByteIdenticalToTheTemplateFile()
    {
        var embedded = AgentTeamService.ReadEmbeddedTeamsJson();
        Assert.NotNull(embedded);
        Assert.Equal(
            File.ReadAllText(TemplateTeamsFile).ReplaceLineEndings("\n"),
            embedded.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmbeddedRoster_MatchesTheCompiledFallbackDefinitionsExactly()
    {
        // The drift anchor for the transition: teams.json and the pre-data C# list describe the
        // same nine teams, in the same order, seat for seat. Editing one without the other fails here.
        Assert.Equal(
            Shapes(AgentTeamService.CompiledFallbackDefinitions),
            Shapes(AgentTeamService.EmbeddedDefinitions));
    }

    [Fact]
    public void GetTeams_StillResolvesTheNineBuiltInsFromDataInOrder()
    {
        var teams = new AgentTeamService().GetTeams();

        Assert.Equal(9, teams.Count);
        Assert.Equal(
            [
                AgentTeamService.AllTeamsSlug,
                AgentTeamService.SoftwareEngineeringSlug,
                AgentTeamService.ContentEngineSlug,
                AgentTeamService.GrowthMarketingSlug,
                AgentTeamService.UxDesignSlug,
                AgentTeamService.DataIntelligenceSlug,
                AgentTeamService.GovernanceOpsSlug,
                AgentTeamService.HealthPerformanceSlug,
                AgentTeamService.LocalMediaCreationSlug
            ],
            teams.Select(team => team.Slug));
        // The no-filter sentinel stays first: GetTeamBySlug falls back to it.
        Assert.Equal(AgentTeamService.AllTeamsSlug, teams[0].Slug);
        Assert.Empty(teams[0].AgentSlugs);
        Assert.All(new AgentTeamService().GetDefinitions(), definition => Assert.False(definition.IsExecutable));
    }

    [Fact]
    public async Task TeamsJson_ShipsInTheTemplateAndIsWrittenToTheWorkspace()
    {
        var template = new AgentsTemplateService();
        Assert.Contains(TeamSeed.FileName, template.RelativePaths(), StringComparer.Ordinal);

        using var tmp = new TempDir();
        var result = await template.InitializeAsync(tmp.Path, overwriteConflicts: true);

        Assert.Contains(".agents/" + TeamSeed.FileName, result.Written, StringComparer.Ordinal);
        Assert.True(File.Exists(TeamSeed.WorkspacePath(tmp.Path)));
        // An initialized workspace resolves the same nine from its own file.
        Assert.Equal(
            Shapes(AgentTeamService.EmbeddedDefinitions),
            Shapes(new AgentTeamService().GetDefinitions(tmp.Path)));
    }

    [Fact]
    public void WorkspaceRoster_AddsATeamWithoutRecompiling()
    {
        using var tmp = new TempDir();
        // What a composer writes: core's teams plus the pack's, in one file.
        WriteWorkspaceRoster(tmp.Path, """
            {
              "schemaVersion": 1,
              "teams": [
                { "slug": "all", "name": "All Teams", "description": "", "icon": "👥", "agentSlugs": [] },
                {
                  "slug": "security-review",
                  "name": "Security Review",
                  "description": "Four-lane adversarial review.",
                  "icon": "🔐",
                  "agentSlugs": ["security-auditor", "threat-modeler", "producer"]
                }
              ]
            }
            """);

        var sut = new AgentTeamService();
        var teams = sut.GetTeams(tmp.Path);

        Assert.Contains(teams, team => team.Slug == "security-review");
        Assert.Equal(
            ["security-auditor", "threat-modeler", "producer"],
            sut.GetDefinitionBySlug("security-review", tmp.Path)!.AgentSlugs);

        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Security Auditor", Slug = "security-auditor" },
            new() { Id = 3, Name = "Programmer", Slug = "programmer" }
        };
        var filtered = sut.FilterMembersByTeam("security-review", members, tmp.Path);
        Assert.Contains(filtered, member => member.Slug == "security-auditor");
        Assert.Contains(filtered, member => member.Slug == "owner");
        Assert.DoesNotContain(filtered, member => member.Slug == "programmer");

        // The project-less overload is untouched by a workspace roster.
        Assert.DoesNotContain(sut.GetTeams(), team => team.Slug == "security-review");
    }

    [Fact]
    public void WorkspaceRoster_TeamMembershipAddsASeatToAnExistingTeam()
    {
        using var tmp = new TempDir();
        WriteWorkspaceRoster(tmp.Path, """
            {
              "schemaVersion": 1,
              "teams": [
                {
                  "slug": "software-engineering",
                  "name": "Software Engineering",
                  "description": "",
                  "icon": "💻",
                  "agentSlugs": ["programmer", "qa-tester"]
                }
              ],
              "teamMembership": {
                "software-engineering": ["security-auditor", "programmer"]
              }
            }
            """);

        var definition = new AgentTeamService()
            .GetDefinitionBySlug(AgentTeamService.SoftwareEngineeringSlug, tmp.Path)!;

        // Added at the end, and an agent already seated is not duplicated.
        Assert.Equal(["programmer", "qa-tester", "security-auditor"], definition.AgentSlugs);
    }

    [Fact]
    public void WorkspaceRoster_MalformedFileFallsBackToTheBuiltIns()
    {
        using var tmp = new TempDir();
        WriteWorkspaceRoster(tmp.Path, "{ not json");

        var sut = new AgentTeamService();
        Assert.Equal(Shapes(sut.GetDefinitions()), Shapes(sut.GetDefinitions(tmp.Path)));

        // A structurally invalid team is refused too — the roster is never partially applied.
        WriteWorkspaceRoster(tmp.Path, """
            [{ "slug": "broken", "name": "Broken", "description": "", "icon": "💥",
               "roles": [{ "roleId": "lead", "agentSlug": "producer" }],
               "taskGraph": [{ "key": "a", "roleId": "nobody", "title": "A" }] }]
            """);
        Assert.Equal(Shapes(sut.GetDefinitions()), Shapes(sut.GetDefinitions(tmp.Path)));
    }

    [Fact]
    public void WorkspaceRoster_CanDeclareAnExecutableTeamFromData()
    {
        using var tmp = new TempDir();
        WriteWorkspaceRoster(tmp.Path, """
            [{
              "slug": "parallel-review",
              "name": "Parallel review",
              "description": "Two lanes then a synthesis.",
              "icon": "🔍",
              "roles": [
                { "roleId": "security", "agentSlug": "programmer" },
                { "roleId": "lead", "agentSlug": "producer" }
              ],
              "taskGraph": [
                { "key": "security-lane", "roleId": "security", "title": "Security review", "prompt": "Audit the diff." },
                { "key": "dedup", "roleId": "lead", "title": "Deduplicate", "dependsOn": ["security-lane"] }
              ],
              "joinPolicy": { "mode": "Quorum", "quorum": 1 },
              "synthesizerRole": "lead"
            }]
            """);

        var definition = new AgentTeamService().GetDefinitionBySlug("parallel-review", tmp.Path)!;

        Assert.True(definition.IsExecutable);
        Assert.Equal(TeamJoinMode.Quorum, definition.JoinPolicy.Mode);
        Assert.Equal(1, definition.JoinPolicy.Quorum);
        Assert.Equal("lead", definition.SynthesizerRole);
        Assert.Equal(["security-lane"], definition.FindTask("dedup")!.DependsOn);
        Assert.Equal("Audit the diff.", definition.FindTask("security-lane")!.Prompt);
    }

    [Fact]
    public void Compose_RejectsADuplicateTeamSlugAcrossContributors()
    {
        var core = TeamSeed.Parse("""[{ "slug": "shared", "name": "Core", "description": "", "icon": "1" }]""", "core");
        var pack = TeamSeed.Parse("""[{ "slug": "SHARED", "name": "Pack", "description": "", "icon": "2" }]""", "pack");

        var exception = Assert.Throws<TeamDefinitionException>(() => TeamSeed.Compose(core, pack));
        Assert.Equal("team_duplicate", exception.Code);
    }

    [Fact]
    public void Compose_RejectsMembershipForATeamNoRosterDefines()
    {
        var document = TeamSeed.Parse("""
            { "schemaVersion": 1, "teams": [], "teamMembership": { "ghost-team": ["producer"] } }
            """, "pack");

        var exception = Assert.Throws<TeamDefinitionException>(() => TeamSeed.Compose(document));
        Assert.Equal("team_membership_unknown", exception.Code);
    }

    [Fact]
    public void Compose_AppliesMembershipDeclaredBeforeTheTeamItJoins()
    {
        var pack = TeamSeed.Parse("""
            { "schemaVersion": 1, "teams": [], "teamMembership": { "software-engineering": ["security-auditor"] } }
            """, "pack");
        var core = TeamSeed.Parse(AgentTeamService.ReadEmbeddedTeamsJson()!, "core");

        var composed = TeamSeed.Compose(pack, core);
        var team = composed.Single(definition => definition.Slug == AgentTeamService.SoftwareEngineeringSlug);

        Assert.Contains("security-auditor", team.AgentSlugs);
        Assert.Equal("programmer", team.AgentSlugs[0]);
    }

    [Fact]
    public void Parse_RefusesAnUnsupportedSchemaVersion()
    {
        var exception = Assert.Throws<TeamDefinitionException>(
            () => TeamSeed.Parse("""{ "schemaVersion": 2, "teams": [] }""", "future"));
        Assert.Equal("teams_schema_unsupported", exception.Code);
        Assert.Null(TeamSeed.TryParse("""{ "schemaVersion": 2, "teams": [] }""", "future"));
    }

    [Fact]
    public void Parse_AcceptsTheBareArrayFragmentForm()
    {
        var document = TeamSeed.Parse("""
            [{ "slug": "security-review", "name": "Security Review", "description": "d", "icon": "🔐",
               "agentSlugs": ["security-auditor"] }]
            """, "fragment");

        var definition = Assert.Single(TeamSeed.Compose(document));
        Assert.Equal("security-review", definition.Slug);
        Assert.Equal(["security-auditor"], definition.AgentSlugs);
        Assert.False(definition.IsExecutable);
    }

    [Fact]
    public void EveryTemplateAgent_IsSeatedByTheDataRoster()
    {
        // The C9 binding rule "every agent ships with a team membership", read off the data.
        var agents = new AgentsTemplateService().AgentSlugs();
        Assert.NotEmpty(agents);

        var seated = new AgentTeamService().GetDefinitions()
            .Where(definition => !definition.Slug.Equals(AgentTeamService.AllTeamsSlug, StringComparison.OrdinalIgnoreCase))
            .SelectMany(definition => definition.AgentSlugs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(agents.Where(slug => !seated.Contains(slug)));
    }
}

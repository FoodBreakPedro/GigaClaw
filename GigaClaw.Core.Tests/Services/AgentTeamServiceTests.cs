using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using Xunit;

namespace GigaClaw.Core.Tests.Services;

public sealed class AgentTeamServiceTests
{
    private readonly AgentTeamService _sut = new();

    [Fact]
    public void GetTeams_ReturnsDefaultTeams()
    {
        var teams = _sut.GetTeams();

        Assert.NotNull(teams);
        Assert.True(teams.Count >= 9);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.AllTeamsSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.SoftwareEngineeringSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.ContentEngineSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.GrowthMarketingSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.UxDesignSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.DataIntelligenceSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.GovernanceOpsSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.HealthPerformanceSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.LocalMediaCreationSlug);
    }

    [Fact]
    public void FilterMembersByTeam_AllTeams_ReturnsAllMembers()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Programmer", Slug = "programmer" },
            new() { Id = 3, Name = "Blog Writer", Slug = "blog-writer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.AllTeamsSlug, members);

        Assert.Equal(3, filtered.Count);
    }

    [Fact]
    public void FilterMembersByTeam_GrowthMarketing_IncludesGrowthAgentsAndOwner()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Growth Writer", Slug = "growth-writer" },
            new() { Id = 3, Name = "Lead Magnet Creator", Slug = "lead-magnet-creator" },
            new() { Id = 4, Name = "Programmer", Slug = "programmer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.GrowthMarketingSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "growth-writer");
        Assert.Contains(filtered, m => m.Slug == "lead-magnet-creator");
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }

    [Fact]
    public void FilterMembersByTeam_GovernanceOps_IncludesGovernanceAgents()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Approval Gatekeeper", Slug = "approval-gatekeeper" },
            new() { Id = 3, Name = "System Watchdog", Slug = "system-watchdog" },
            new() { Id = 4, Name = "Programmer", Slug = "programmer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.GovernanceOpsSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "approval-gatekeeper");
        Assert.Contains(filtered, m => m.Slug == "system-watchdog");
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }

    [Fact]
    public void FilterMembersByTeam_HealthPerformance_IncludesWellnessAgents()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Wellness Coach", Slug = "wellness-coach" },
            new() { Id = 3, Name = "Content Series Planner", Slug = "content-series-planner" },
            new() { Id = 4, Name = "Programmer", Slug = "programmer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.HealthPerformanceSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "wellness-coach");
        Assert.Contains(filtered, m => m.Slug == "content-series-planner");
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }

    [Fact]
    public void FilterMembersByTeam_LocalMediaCreation_IncludesMediaAgentsAndOwner()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Local Media Director", Slug = "local-media-director" },
            new() { Id = 3, Name = "Local Image Artist", Slug = "local-image-artist" },
            new() { Id = 4, Name = "Local Motion Artist", Slug = "local-motion-artist" },
            new() { Id = 5, Name = "Local Media Compositor", Slug = "local-media-compositor" },
            new() { Id = 6, Name = "Local Media Reviewer", Slug = "local-media-reviewer" },
            new() { Id = 7, Name = "Programmer", Slug = "programmer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.LocalMediaCreationSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "local-media-director");
        Assert.Contains(filtered, m => m.Slug == "local-image-artist");
        Assert.Contains(filtered, m => m.Slug == "local-motion-artist");
        Assert.Contains(filtered, m => m.Slug == "local-media-compositor");
        Assert.Contains(filtered, m => m.Slug == "local-media-reviewer");
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }
}

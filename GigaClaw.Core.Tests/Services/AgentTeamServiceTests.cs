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
        Assert.True(teams.Count >= 6);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.AllTeamsSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.SoftwareEngineeringSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.ContentEngineSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.GrowthMarketingSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.UxDesignSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.DataIntelligenceSlug);
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
    public void FilterMembersByTeam_UxDesign_IncludesDesignAgentsAndProgrammer()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "UI Designer", Slug = "ui-designer" },
            new() { Id = 3, Name = "UI Auditor", Slug = "ui-auditor" },
            new() { Id = 4, Name = "Programmer", Slug = "programmer" },
            new() { Id = 5, Name = "Blog Writer", Slug = "blog-writer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.UxDesignSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "ui-designer");
        Assert.Contains(filtered, m => m.Slug == "ui-auditor");
        Assert.Contains(filtered, m => m.Slug == "programmer");
        Assert.DoesNotContain(filtered, m => m.Slug == "blog-writer");
    }

    [Fact]
    public void FilterMembersByTeam_DataIntelligence_IncludesDataAgents()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Data Analyst", Slug = "data-analyst" },
            new() { Id = 3, Name = "Competitive Analyst", Slug = "competitive-analyst" },
            new() { Id = 4, Name = "Programmer", Slug = "programmer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.DataIntelligenceSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "data-analyst");
        Assert.Contains(filtered, m => m.Slug == "competitive-analyst");
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }
}

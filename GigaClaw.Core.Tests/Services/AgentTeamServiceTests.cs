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
        Assert.True(teams.Count >= 3);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.AllTeamsSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.SoftwareEngineeringSlug);
        Assert.Contains(teams, t => t.Slug == AgentTeamService.ContentEngineSlug);
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
    public void FilterMembersByTeam_SoftwareEngineering_IncludesSoftwareAgentsAndOwner()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Programmer", Slug = "programmer" },
            new() { Id = 3, Name = "Blog Writer", Slug = "blog-writer" },
            new() { Id = 4, Name = "Committer", Slug = "committer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.SoftwareEngineeringSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "programmer");
        Assert.Contains(filtered, m => m.Slug == "committer");
        Assert.DoesNotContain(filtered, m => m.Slug == "blog-writer");
    }

    [Fact]
    public void FilterMembersByTeam_ContentEngine_IncludesContentAgentsAndSharedAgents()
    {
        var members = new List<Member>
        {
            new() { Id = 1, Name = "Owner", Slug = "owner" },
            new() { Id = 2, Name = "Programmer", Slug = "programmer" },
            new() { Id = 3, Name = "Blog Writer", Slug = "blog-writer" },
            new() { Id = 4, Name = "Blog Reviewer", Slug = "blog-reviewer" },
            new() { Id = 5, Name = "Committer", Slug = "committer" }
        };

        var filtered = _sut.FilterMembersByTeam(AgentTeamService.ContentEngineSlug, members);

        Assert.Contains(filtered, m => m.Slug == "owner");
        Assert.Contains(filtered, m => m.Slug == "blog-writer");
        Assert.Contains(filtered, m => m.Slug == "blog-reviewer");
        Assert.Contains(filtered, m => m.Slug == "committer"); // Shared utility agent
        Assert.DoesNotContain(filtered, m => m.Slug == "programmer");
    }
}

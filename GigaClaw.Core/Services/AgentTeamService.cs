using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

public sealed class AgentTeamService
{
    public const string AllTeamsSlug = "all";
    public const string SoftwareEngineeringSlug = "software-engineering";
    public const string ContentEngineSlug = "content-engine";

    private static readonly IReadOnlyList<AgentTeam> DefaultTeams = new List<AgentTeam>
    {
        new(
            AllTeamsSlug,
            "All Teams",
            "Show all available members and agents across all specialties.",
            "👥",
            Array.Empty<string>()
        ),
        new(
            SoftwareEngineeringSlug,
            "Software Engineering",
            "Core software development, code refactoring, QA testing, and git operations.",
            "💻",
            new[] { "programmer", "groomer", "producer", "qa-tester", "committer", "code-janitor", "evaluator", "documentalist" }
        ),
        new(
            ContentEngineSlug,
            "Content Engine",
            "Blog writing, quality review, topic research, SEO & GEO auditing, and translation.",
            "✍️",
            new[] { "blog-writer", "blog-reviewer", "blog-researcher", "blog-seo", "blog-translator", "producer", "committer", "evaluator", "documentalist" }
        )
    };

    public IReadOnlyList<AgentTeam> GetTeams() => DefaultTeams;

    public AgentTeam? GetTeamBySlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return DefaultTeams[0];
        return DefaultTeams.FirstOrDefault(t => t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)) ?? DefaultTeams[0];
    }

    public List<Member> FilterMembersByTeam(string? teamSlug, IEnumerable<Member> members)
    {
        if (string.IsNullOrEmpty(teamSlug) || teamSlug.Equals(AllTeamsSlug, StringComparison.OrdinalIgnoreCase))
        {
            return members.ToList();
        }

        var team = GetTeamBySlug(teamSlug);
        if (team is null || team.AgentSlugs.Count == 0)
        {
            return members.ToList();
        }

        var allowedSlugs = new HashSet<string>(team.AgentSlugs, StringComparer.OrdinalIgnoreCase);
        // "owner" is the human user, always accessible across teams
        allowedSlugs.Add("owner");

        return members.Where(m => allowedSlugs.Contains(m.Slug)).ToList();
    }
}

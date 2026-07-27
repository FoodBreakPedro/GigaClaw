using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

public sealed class AgentTeamService
{
    public const string AllTeamsSlug = "all";
    public const string SoftwareEngineeringSlug = "software-engineering";
    public const string ContentEngineSlug = "content-engine";
    public const string GrowthMarketingSlug = "growth-marketing";
    public const string UxDesignSlug = "ux-design";
    public const string DataIntelligenceSlug = "data-intelligence";
    public const string GovernanceOpsSlug = "governance-ops";
    public const string HealthPerformanceSlug = "health-performance";
    public const string LocalMediaCreationSlug = "local-media-creation";

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
        ),
        new(
            GrowthMarketingSlug,
            "Growth Marketing",
            "LinkedIn & social ghostwriting, lead magnets, trend listening, and cold email copy.",
            "📢",
            new[] { "growth-writer", "lead-magnet-creator", "trend-researcher", "email-copywriter", "producer", "committer", "evaluator", "documentalist" }
        ),
        new(
            UxDesignSlug,
            "UX & Product Design",
            "Anti-slop web application UI design, multi-gate design audits, and design DNA research.",
            "🎨",
            new[] { "ui-designer", "ui-auditor", "design-researcher", "programmer", "producer", "committer", "evaluator", "documentalist" }
        ),
        new(
            DataIntelligenceSlug,
            "Data & Intelligence",
            "SQL query building, dataset analysis, Mermaid data charts, and competitive market research.",
            "📊",
            new[] { "data-analyst", "competitive-analyst", "producer", "evaluator", "documentalist" }
        ),
        new(
            GovernanceOpsSlug,
            "Governance & Ops",
            "Human-in-the-loop approval gates, runtime health probes, and decision receipts.",
            "🛡️",
            new[] { "approval-gatekeeper", "system-watchdog", "decision-engine", "producer", "committer", "evaluator", "documentalist" }
        ),
        new(
            HealthPerformanceSlug,
            "Health & Performance",
            "Health & fitness content vertical: sourced wellness guides, training and ergonomics articles, and multi-part content series planning.",
            "🏋️",
            new[] { "wellness-coach", "content-series-planner", "blog-writer", "producer", "evaluator", "documentalist" }
        ),
        new(
            LocalMediaCreationSlug,
            "Local Media Creation",
            "Governed local image candidates, generated motion assets, OpenMontage composition, and independent media review.",
            "🎬",
            new[]
            {
                "local-media-director", "local-image-artist", "local-motion-artist",
                "local-media-compositor", "local-media-reviewer", "producer",
                "approval-gatekeeper", "system-watchdog", "committer", "evaluator", "documentalist"
            }
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

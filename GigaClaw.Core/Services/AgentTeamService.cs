using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

/// <summary>
/// Resolves the team roster. Teams are <b>data</b>: the built-ins live in
/// <c>ProjectTemplate/Agents/teams.json</c>, embedded as
/// <c>GigaClaw.Core.AgentsTemplate/teams.json</c> and written to
/// <c>&lt;workspace&gt;/.agents/teams.json</c> by Initialize. Resolution has three levels, in order:
/// <list type="number">
///   <item>the workspace's own <c>.agents/teams.json</c> — what a pack composes into, so a team or
///     a membership it contributes is live without recompiling this assembly;</item>
///   <item>the embedded core roster — every workspace that has no file of its own, which is every
///     workspace initialized before the roster became data;</item>
///   <item><see cref="CompiledFallbackDefinitions"/> — the pre-data C# list, kept only so a broken
///     or missing resource cannot take the member filter down. A test asserts it is equal to the
///     embedded roster, so the two cannot drift.</item>
/// </list>
/// </summary>
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
    /// <summary>C8: parallel-review, the first built-in with a real task graph.</summary>
    public const string ParallelReviewSlug = "parallel-review";
    /// <summary>C8: hypothesis-debug, the second built-in with a real task graph.</summary>
    public const string HypothesisDebugSlug = "hypothesis-debug";

    private const string TeamsResourceName = "GigaClaw.Core.AgentsTemplate/" + TeamSeed.FileName;

    // The built-in teams are TeamDefinitions with an empty task graph — "pure member filter" in the
    // C4 model. They carry roles (one seat per agent), no entry conditions, no synthesizer and the
    // default join policy, so nothing about filtering changes; the executable machinery simply has
    // nothing to run for them.
    //
    // This list is no longer the source of truth — ProjectTemplate/Agents/teams.json is. It survives
    // as the last-resort fallback described on the type, and as the drift anchor the seed-data test
    // compares the file against. Editing a team here without editing the file (or the reverse) fails
    // that test on purpose.
    internal static readonly IReadOnlyList<TeamDefinition> CompiledFallbackDefinitions = new List<TeamDefinition>
    {
        TeamDefinition.FilterOnly(
            AllTeamsSlug,
            "All Teams",
            "Show all available members and agents across all specialties.",
            "👥",
            Array.Empty<string>()
        ),
        TeamDefinition.FilterOnly(
            SoftwareEngineeringSlug,
            "Software Engineering",
            "Core software development, code refactoring, QA testing, and git operations.",
            "💻",
            new[] { "programmer", "groomer", "producer", "qa-tester", "committer", "code-janitor", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            ContentEngineSlug,
            "Content Engine",
            "Blog writing, quality review, topic research, SEO & GEO auditing, and translation.",
            "✍️",
            new[] { "blog-writer", "blog-reviewer", "blog-researcher", "blog-seo", "blog-translator", "content-writer", "producer", "committer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            GrowthMarketingSlug,
            "Growth Marketing",
            "LinkedIn & social ghostwriting, lead magnets, trend listening, and cold email copy.",
            "📢",
            new[] { "growth-writer", "lead-magnet-creator", "trend-researcher", "email-copywriter", "producer", "committer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            UxDesignSlug,
            "UX & Product Design",
            "Anti-slop web application UI design, multi-gate design audits, and design DNA research.",
            "🎨",
            new[] { "ui-designer", "ui-auditor", "design-researcher", "programmer", "producer", "committer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            DataIntelligenceSlug,
            "Data & Intelligence",
            "SQL query building, dataset analysis, Mermaid data charts, and competitive market research.",
            "📊",
            new[] { "data-analyst", "competitive-analyst", "producer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            GovernanceOpsSlug,
            "Governance & Ops",
            "Human-in-the-loop approval gates, runtime health probes, and decision receipts.",
            "🛡️",
            new[] { "approval-gatekeeper", "system-watchdog", "decision-engine", "producer", "committer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
            HealthPerformanceSlug,
            "Health & Performance",
            "Health & fitness content vertical: sourced wellness guides, training and ergonomics articles, and multi-part content series planning.",
            "🏋️",
            new[] { "wellness-coach", "content-series-planner", "blog-writer", "producer", "evaluator", "documentalist" }
        ),
        TeamDefinition.FilterOnly(
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
        ),
        // C8: the first two built-ins with a real task graph. Kept byte-for-shape identical to
        // ProjectTemplate/Agents/teams.json's entries by TeamSeedTests.EmbeddedRoster_
        // MatchesTheCompiledFallbackDefinitionsExactly — the same drift anchor the nine filter-only
        // teams above already have to honor.
        new(ParallelReviewSlug, "Parallel Review",
            "Fans a ticket into independent accessibility and coverage review lanes, deduplicates overlapping findings across them with per-lane attribution, and hands a synthesizer a gate-consumable verdict (GIGACLAW-VERDICT, agent team-synthesis). Security, performance and architecture lane roles (security-reviewer, performance-reviewer, architecture-reviewer) are reserved: no core agent does that review today, and the dedicated agents ship with the Security and Architecture & Data packs (doc/roadmap/packs-and-later.md) — add a role plus a task template once they exist.",
            "🔍")
        {
            Roles =
            [
                new TeamRole("accessibility-reviewer", "ui-auditor")
                {
                    Description = "Accessibility and WCAG-adjacent findings — stand-in for the dedicated accessibility-reviewer role (pending GM G5)."
                },
                new TeamRole("coverage-reviewer", "qa-tester")
                {
                    Description = "Test coverage and adversarial verification findings — stand-in for the dedicated coverage-reviewer role (pending GM G5)."
                },
                new TeamRole("synthesizer", "producer")
                {
                    Description = "Reads the deduped findings and closes the run."
                }
            ],
            TaskGraph =
            [
                new TeamTaskTemplate("accessibility-lane", "accessibility-reviewer", "Accessibility review")
                {
                    Prompt = "Review this ticket's change for accessibility issues (contrast, focus order, keyboard reachability). Record each finding as an openLoops entry in your handoff, one per issue, naming the file or location it is in. Authored pending GM G5 — replace with the dedicated accessibility-reviewer prose."
                },
                new TeamTaskTemplate("coverage-lane", "coverage-reviewer", "Coverage review")
                {
                    Prompt = "Review this ticket's change for test coverage gaps and untested edge cases. Record each finding as an openLoops entry in your handoff, one per gap, naming the file or location it is in. Authored pending GM G5 — replace with the dedicated coverage-reviewer prose."
                }
            ],
            JoinPolicy = TeamJoinPolicy.AllDone,
            SynthesizerRole = "synthesizer",
            DedupeFindings = true
        },
        new(HypothesisDebugSlug, "Hypothesis Debug",
            "Fans a bug ticket into competing investigator lanes, one hypothesis each, then a lead arbitration that must cite evidence; the host closes the losing lane(s) once the lead names a winner (GIGACLAW-ARBITRATION marker). Dedicated hypothesis-investigator and debug-lead roles are reserved pending the Incident & Debug pack (doc/roadmap/packs-and-later.md) — qa-tester and producer stand in today.",
            "🕵️")
        {
            Roles =
            [
                new TeamRole("investigator-a", "qa-tester")
                {
                    Description = "Investigates one candidate hypothesis — stand-in for the dedicated hypothesis-investigator role (pending GM G5)."
                },
                new TeamRole("investigator-b", "qa-tester")
                {
                    Description = "Investigates a different candidate hypothesis — stand-in for the dedicated hypothesis-investigator role (pending GM G5)."
                },
                new TeamRole("debug-lead", "producer")
                {
                    Description = "Arbitrates between the investigators' hypotheses, citing evidence — stand-in for the dedicated debug-lead role (pending GM G5)."
                }
            ],
            TaskGraph =
            [
                new TeamTaskTemplate("investigator-a-lane", "investigator-a", "Investigate hypothesis A")
                {
                    Prompt = "Investigate one candidate root cause for this ticket's bug. Gather evidence (repro steps, logs, stack trace, code path) — do not implement a fix. State your hypothesis as the handoff summary and cite the evidence in outputs/openLoops. Authored pending GM G5 — replace with the dedicated hypothesis-investigator prose."
                },
                new TeamTaskTemplate("investigator-b-lane", "investigator-b", "Investigate hypothesis B")
                {
                    Prompt = "Investigate a DIFFERENT candidate root cause than any sibling investigator lane — do not repeat the same hypothesis. Gather evidence; do not implement a fix. State your hypothesis as the handoff summary and cite the evidence in outputs/openLoops. Authored pending GM G5 — replace with the dedicated hypothesis-investigator prose."
                }
            ],
            JoinPolicy = TeamJoinPolicy.AllDone,
            SynthesizerRole = "debug-lead",
            RequireEvidenceCitingArbitration = true
        }
    };

    private static readonly Lazy<IReadOnlyList<TeamDefinition>> BuiltIn = new(LoadEmbeddedDefinitions);

    // Keyed by workspace path. The file's write time and length are part of the entry so an edited
    // roster is picked up without a restart, and an unchanged one costs no parse per render.
    private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, long Length, IReadOnlyList<TeamDefinition> Definitions)>
        _workspaceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The composed roster the built-ins come from — the embedded core <c>teams.json</c>.</summary>
    internal static IReadOnlyList<TeamDefinition> EmbeddedDefinitions => BuiltIn.Value;

    /// <summary>Raw text of the embedded core roster, or null when the resource is missing.</summary>
    internal static string? ReadEmbeddedTeamsJson()
    {
        var assembly = typeof(AgentTeamService).Assembly;
        var name = ResolveResourceName(assembly);
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? ResolveResourceName(Assembly assembly)
    {
        // %(RecursiveDir) yields a different separator per build OS, so probe both spellings —
        // the same reason AgentsTemplateService.ReadAsset does.
        var names = assembly.GetManifestResourceNames();
        return names.FirstOrDefault(name => name == TeamsResourceName)
            ?? names.FirstOrDefault(name => name == TeamsResourceName.Replace('/', '\\'));
    }

    private static IReadOnlyList<TeamDefinition> LoadEmbeddedDefinitions()
    {
        var json = ReadEmbeddedTeamsJson();
        if (json is null) return CompiledFallbackDefinitions;

        var document = TeamSeed.TryParse(json, TeamsResourceName);
        if (document is null) return CompiledFallbackDefinitions;

        try { return TeamSeed.Compose(document); }
        catch (TeamDefinitionException) { return CompiledFallbackDefinitions; }
    }

    /// <summary>
    /// Roster for a workspace: its own <c>.agents/teams.json</c> when it has a readable one,
    /// otherwise the built-ins. A malformed file degrades to the built-ins rather than emptying the
    /// member filter — a bad roster must not be able to hide every agent on the board.
    /// </summary>
    public IReadOnlyList<TeamDefinition> GetDefinitions(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return BuiltIn.Value;

        var path = TeamSeed.WorkspacePath(workspacePath);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                _workspaceCache.TryRemove(path, out _);
                return BuiltIn.Value;
            }
        }
        catch (IOException) { return BuiltIn.Value; }
        catch (UnauthorizedAccessException) { return BuiltIn.Value; }

        if (_workspaceCache.TryGetValue(path, out var cached) &&
            cached.WriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            return cached.Definitions;
        }

        IReadOnlyList<TeamDefinition> definitions;
        try
        {
            var document = TeamSeed.TryParse(File.ReadAllText(path), path);
            definitions = document is null ? BuiltIn.Value : TeamSeed.Compose(document);
        }
        catch (TeamDefinitionException) { definitions = BuiltIn.Value; }
        catch (IOException) { return BuiltIn.Value; }
        catch (UnauthorizedAccessException) { return BuiltIn.Value; }

        _workspaceCache[path] = (info.LastWriteTimeUtc, info.Length, definitions);
        return definitions;
    }

    public IReadOnlyList<AgentTeam> GetTeams(string? workspacePath) =>
        [.. GetDefinitions(workspacePath).Select(definition => definition.ToAgentTeam())];

    public IReadOnlyList<AgentTeam> GetTeams() => BuiltInTeams.Value;

    private static readonly Lazy<IReadOnlyList<AgentTeam>> BuiltInTeams =
        new(() => BuiltIn.Value.Select(definition => definition.ToAgentTeam()).ToList());

    /// <summary>The built-in teams in their executable form. All nine have an empty task graph.</summary>
    public IReadOnlyList<TeamDefinition> GetDefinitions() => BuiltIn.Value;

    /// <summary>Built-in definition for a slug, or null. Unlike <see cref="GetTeamBySlug"/> there is no fallback.</summary>
    public TeamDefinition? GetDefinitionBySlug(string? slug) => GetDefinitionBySlug(slug, workspacePath: null);

    public TeamDefinition? GetDefinitionBySlug(string? slug, string? workspacePath) =>
        string.IsNullOrEmpty(slug)
            ? null
            : GetDefinitions(workspacePath).FirstOrDefault(definition =>
                definition.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public AgentTeam? GetTeamBySlug(string? slug) => GetTeamBySlug(slug, workspacePath: null);

    public AgentTeam? GetTeamBySlug(string? slug, string? workspacePath)
    {
        var teams = GetTeams(workspacePath);
        if (teams.Count == 0) return null;
        // "all" is the no-filter sentinel and the roster's first entry; naming it rather than
        // indexing keeps the fallback correct for a roster that orders its teams differently.
        var all = teams.FirstOrDefault(team => team.Slug.Equals(AllTeamsSlug, StringComparison.OrdinalIgnoreCase))
            ?? teams[0];
        if (string.IsNullOrEmpty(slug)) return all;
        return teams.FirstOrDefault(t => t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)) ?? all;
    }

    public List<Member> FilterMembersByTeam(string? teamSlug, IEnumerable<Member> members) =>
        FilterMembersByTeam(teamSlug, members, workspacePath: null);

    public List<Member> FilterMembersByTeam(string? teamSlug, IEnumerable<Member> members, string? workspacePath)
    {
        if (string.IsNullOrEmpty(teamSlug) || teamSlug.Equals(AllTeamsSlug, StringComparison.OrdinalIgnoreCase))
        {
            return members.ToList();
        }

        var team = GetTeamBySlug(teamSlug, workspacePath);
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

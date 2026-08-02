using GigaClaw.Catalog;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

public sealed class CatalogGeneratorTests
{
    [Fact]
    public void Generate_reports_the_current_template_inventory_and_known_team_gap()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        // 33 core + the 4 agents of the security-assurance pack. Asserted as two numbers rather
        // than one so a pack silently vanishing from the catalog fails here.
        Assert.Equal(37, catalog.Summary.Agents);
        Assert.Equal(33, catalog.Agents.Count(agent => agent.Pack == "core"));
        Assert.Equal(4, catalog.Agents.Count(agent => agent.Pack == "security-assurance"));
        Assert.Equal(37, catalog.Summary.Contracts);
        // 29 before C2 + the 14 verdict gate arms, all live now that blog-reviewer's AD-7 protocol
        // emits a typed verdict beside its CONTENT-REVIEW markers. Enabled trails total by one:
        // `weekly-ticket-example` is a shipped-off sample, not a gate.
        // 43 core + the pack's 7, plus the two C8 team-start automations (parallel-review-on-labeled,
        // hypothesis-debug-on-qa-block) = 45 core + 7 pack, plus the SP-3 worktree-isolated twins split
        // out of assignee-dispatch/assignee-resume/owner-feedback so isolation can be set on the
        // programmer/qa-tester binding without touching every other agent sharing that automation
        // (assignee-dispatch-code, assignee-resume-code, owner-feedback-code) = 48 core + 7 pack, plus
        // the U6 follow-up's three GitHub automations (github-ci-success-enqueues-merge,
        // github-ci-failure-records-check, verdict-gate-qa-ship-open-pull-request) = 51 core + 7 pack.
        // Enabled trails total by four: `weekly-ticket-example` is a shipped-off sample, not a gate,
        // and the three GitHub automations ship wired but disabled (owner decision 2026-08-01 — off
        // unless a project configures a GitHub remote/token) — every pack automation is still enabled
        // by the binding rule. Phase 1 (return-to-sender) then added 15 core arms, all enabled:
        // `backlog-intake` (unassigned Backlog tickets reach the groomer), five reviewer-retry arms
        // + three retry-exhaustion arms (an INVALID/STALE/MISSING verdict re-runs the reviewer once
        // before it blocks), and six `extended-repair` duplicates of the repair pairs = 66 core.
        // The engine-domain review then added 9 more core arms, all enabled: the shared
        // retry-exhaustion arm split per reviewer family so one reviewer's receipts cannot exhaust
        // another's first attempt (+2), a `verdict-gate-review-watchdog` that terminates a ticket
        // stranded in Review on a spent retry budget, and six `*-triaged` twins that cap the
        // groomer re-scoping loop at one funded lap = 75 core.
        Assert.Equal(82, catalog.Summary.Automations);
        Assert.Equal(78, catalog.Summary.EnabledAutomations);
        Assert.Equal(37, catalog.Summary.ExplicitModelMappings);
        // 9 filter-only core teams + the two C8 presets (parallel-review, hypothesis-debug) + the
        // pack's security-review.
        Assert.Equal(12, catalog.Summary.Teams);
        // 15 at T1 + the five contract files lane CL added (schema_check, verdict_contract,
        // handoff_contract and the two schemas) + sbom_diff.py, which the security pack contributes
        // and its supply-chain lane calls, + media_common.py, extracted as a shared sibling module
        // for load_object() (previously duplicated verbatim in media_generate.py/media_contract.py).
        // The catalog counts them because agents call them.
        Assert.Equal(23, catalog.Summary.Scripts);
        var contentWriter = Assert.Single(catalog.Agents, agent => agent.Slug == "content-writer");
        Assert.True(contentWriter.ContractPresent);
        Assert.Equal("content-write", contentWriter.RiskClass);
        Assert.Equal("claude-sonnet-4-6", contentWriter.ExplicitModelMapping);
        Assert.False(contentWriter.ProjectFallbackRequired);
        Assert.Contains("content-engine", contentWriter.Teams);
        Assert.NotEmpty(contentWriter.EnabledDispatchingAutomations);
        Assert.Contains("scripts/content_contract.py", catalog.Scripts);
        Assert.Equal(12, catalog.Teams.Count);
        // 75 core (51 + Phase 1's intake/reviewer-retry/extended-repair arms + the review's
        // per-family retry-exhaustion split, Review watchdog and `*-triaged` twins) + the pack's 7.
        Assert.Equal(82, catalog.Automations.Count);
        // Core only. A baseline is the *reviewed* static-check snapshot and §9 keeps it a
        // core-owned artifact about pack content, so a pack's baselines appear when someone
        // reviews them — not when the pack lands. The binding rule requires a fixture, which the
        // pack's four agents do have (see Eval_fixture_presence_tracks_fixtures_not_baselines).
        Assert.All(
            catalog.Agents.Where(agent => agent.Pack == "core"),
            agent => Assert.True(
                agent.EvalBaselinePresent,
                $"Missing committed eval baseline for {agent.Slug}."));
    }

    /// <summary>
    /// Binding 2 of the five-binding rule is "a model mapping <em>with a stated criterion</em>"
    /// (doc/pack-infrastructure.md §7.2). Before T6 the criterion was recorded nowhere, so nothing
    /// could tell a reviewed tier decision from an unexamined default. This fails the moment a core
    /// mapping is added or edited back to the bare-string form.
    /// </summary>
    [Fact]
    public void Every_core_model_mapping_states_a_criterion()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        var uncriteriaed = catalog.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.ExplicitModelMapping)
                && string.IsNullOrWhiteSpace(agent.ModelCriterion))
            .Select(agent => agent.Slug)
            .ToList();

        Assert.True(
            uncriteriaed.Count == 0,
            "models.json entries with a model but no stated criterion: " + string.Join(", ", uncriteriaed));
        Assert.Equal(33, catalog.Agents.Count(agent =>
            agent.Pack == "core" && !string.IsNullOrWhiteSpace(agent.ModelCriterion)));
    }

    /// <summary>
    /// A <em>fixture</em> is a replay input; a <em>baseline</em> is the reviewed static snapshot.
    /// The catalog reported the baseline and never the fixture, so binding 5 was enforced by
    /// nothing. This pins the distinction: every agent has a baseline, and — since the eval-fixture
    /// authoring pass closed owner Q2's backlog item — every core agent now has a fixture too,
    /// same as the pack's four.
    /// </summary>
    [Fact]
    public void Eval_fixture_presence_tracks_fixtures_not_baselines()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        var withFixture = catalog.Agents
            .Where(agent => agent.EvalFixturePresent)
            .Select(agent => agent.Slug)
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToArray();

        // All 33 core agents, plus all four of the pack's — a pack ships a fixture per agent from
        // its first commit (the binding rule), and core's historic backlog against that same rule
        // is now closed.
        Assert.Equal(
            ["approval-gatekeeper", "blog-researcher", "blog-reviewer", "blog-seo", "blog-translator",
             "blog-writer", "code-janitor", "committer", "competitive-analyst", "content-series-planner",
             "content-writer", "data-analyst", "decision-engine", "design-researcher", "documentalist",
             "email-copywriter", "evaluator", "groomer", "growth-writer", "lead-magnet-creator",
             "local-image-artist", "local-media-compositor", "local-media-director", "local-media-reviewer",
             "local-motion-artist", "producer", "programmer", "qa-tester", "secrets-reviewer",
             "security-auditor", "supply-chain-reviewer", "system-watchdog", "threat-modeler",
             "trend-researcher", "ui-auditor", "ui-designer", "wellness-coach"],
            withFixture);
        Assert.All(
            catalog.Agents.Where(agent => agent.Pack == "core"),
            agent => Assert.True(agent.EvalBaselinePresent));
    }

    /// <summary>
    /// §7.4, owner Q2: core was reported on but never gated for missing eval fixtures while its
    /// historic backlog stood, because blocking it would have blocked the first pack on core's own
    /// debt. The eval-fixture authoring pass closed that backlog — every core agent now ships a
    /// fixture — so the real catalog has no gaps left at all, core or pack, under either strict flag.
    /// <see cref="CatalogGenerator.CoreExemptReasons"/> itself stays (see its doc comment): this test
    /// no longer exercises it against production data, but it would still excuse a future core agent
    /// that lands without a fixture, the same way it excused these 27 while they were missing.
    /// </summary>
    [Fact]
    public void Core_has_no_remaining_binding_gaps_now_that_every_agent_has_a_fixture()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());
        var gaps = CatalogGenerator.FindBindingGaps(catalog);

        Assert.Empty(gaps);
    }

    [Fact]
    public void Markdown_is_stable_for_the_same_catalog()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        var first = CatalogGenerator.RenderMarkdown(catalog);
        var second = CatalogGenerator.RenderMarkdown(catalog);

        Assert.Equal(first, second);
        // The real property is "no generation timestamp", so that the committed artifact is
        // reproducible and CI's drift check means something. The old proxy banned the substring
        // "202" outright, which a model criterion citing owner decision Q3 (2026-07-30) trips —
        // that is content, not a stamp.
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", first);
    }

    [Fact]
    public void Generate_counts_only_direct_agent_directories_and_reports_strict_gaps()
    {
        using var tmp = new TempDir();
        var agents = Path.Combine(tmp.Path, "ProjectTemplate", "Agents");
        Directory.CreateDirectory(Path.Combine(agents, "direct-agent"));
        Directory.CreateDirectory(Path.Combine(agents, "scripts", "nested-fake"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "doc"));
        File.WriteAllText(Path.Combine(agents, "direct-agent", "SKILL.md"), "# Direct");
        File.WriteAllText(Path.Combine(agents, "scripts", "nested-fake", "SKILL.md"), "# Not an agent");
        File.WriteAllText(
            Path.Combine(agents, "contracts.json"),
            """{"agents":{"direct-agent":{"riskClass":"code-write"}}}""");
        File.WriteAllText(Path.Combine(agents, "models.json"), """{"_comment":"none"}""");
        File.WriteAllText(Path.Combine(agents, "automations.json"), """{"automations":[]}""");

        var catalog = new CatalogGenerator().Generate(tmp.Path);

        var agent = Assert.Single(catalog.Agents);
        Assert.Equal("direct-agent", agent.Slug);
        Assert.False(agent.EvalBaselinePresent);
        Assert.False(agent.EvalFixturePresent);
        var gap = Assert.Single(CatalogGenerator.FindBindingGaps(catalog));
        Assert.Contains(CatalogGenerator.ModelMappingReason, gap.Missing);
        Assert.Contains(CatalogGenerator.TeamReason, gap.Missing);
        Assert.Contains(CatalogGenerator.DispatchReason, gap.Missing);
        Assert.Contains(CatalogGenerator.EvalFixtureReason, gap.Missing);
        Assert.Equal("[core] direct-agent: missing model mapping, model criterion, team, enabled dispatching automation, eval fixture", gap.ToString());
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "ProjectTemplate", "Agents"))) return directory.FullName;
        throw new DirectoryNotFoundException("Test repository root not found.");
    }
}

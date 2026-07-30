using GigaClaw.Catalog;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

public sealed class CatalogGeneratorTests
{
    [Fact]
    public void Generate_reports_the_current_template_inventory_and_known_team_gap()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        Assert.Equal(33, catalog.Summary.Agents);
        Assert.Equal(33, catalog.Summary.Contracts);
        Assert.Equal(29, catalog.Summary.Automations);
        Assert.Equal(28, catalog.Summary.EnabledAutomations);
        Assert.Equal(33, catalog.Summary.ExplicitModelMappings);
        Assert.Equal(9, catalog.Summary.Teams);
        // 15 at T1 + the five contract files lane CL added (schema_check, verdict_contract,
        // handoff_contract and the two schemas). The catalog counts them because agents call them.
        Assert.Equal(20, catalog.Summary.Scripts);
        var contentWriter = Assert.Single(catalog.Agents, agent => agent.Slug == "content-writer");
        Assert.True(contentWriter.ContractPresent);
        Assert.Equal("content-write", contentWriter.RiskClass);
        Assert.Equal("claude-sonnet-4-6", contentWriter.ExplicitModelMapping);
        Assert.False(contentWriter.ProjectFallbackRequired);
        Assert.Contains("content-engine", contentWriter.Teams);
        Assert.NotEmpty(contentWriter.EnabledDispatchingAutomations);
        Assert.Contains("scripts/content_contract.py", catalog.Scripts);
        Assert.Equal(9, catalog.Teams.Count);
        Assert.Equal(29, catalog.Automations.Count);
        Assert.All(
            catalog.Agents,
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
        Assert.Equal(33, catalog.Agents.Count(agent => !string.IsNullOrWhiteSpace(agent.ModelCriterion)));
    }

    /// <summary>
    /// A <em>fixture</em> is a replay input; a <em>baseline</em> is the reviewed static snapshot.
    /// The catalog reported the baseline and never the fixture, so binding 5 was enforced by
    /// nothing. This pins the distinction: every agent has a baseline, only the six replayed ones
    /// have a fixture.
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

        Assert.Equal(
            ["approval-gatekeeper", "blog-writer", "growth-writer", "local-media-director", "programmer", "qa-tester"],
            withFixture);
        Assert.All(catalog.Agents, agent => Assert.True(agent.EvalBaselinePresent));
        Assert.All(catalog.Agents, agent => Assert.Equal("core", agent.Pack));
    }

    /// <summary>
    /// The gate reports core's 27 missing fixtures but never fails on them (§7.4, owner Q2): core
    /// predates the rule, and blocking it would block the first pack on core's backlog. Every other
    /// reason still fails core under <c>--strict</c>.
    /// </summary>
    [Fact]
    public void Core_is_reported_on_but_not_gated_for_missing_eval_fixtures()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());
        var gaps = CatalogGenerator.FindBindingGaps(catalog);

        Assert.All(gaps, gap => Assert.Equal([CatalogGenerator.EvalFixtureReason], gap.Missing));
        Assert.Equal(27, gaps.Count);
        Assert.All(
            gaps,
            gap => Assert.Empty(CatalogGenerator.GatedReasons(gap, strict: true, strictPacks: true, catalog)));
    }

    [Fact]
    public void Markdown_is_stable_for_the_same_catalog()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        var first = CatalogGenerator.RenderMarkdown(catalog);
        var second = CatalogGenerator.RenderMarkdown(catalog);

        Assert.Equal(first, second);
        Assert.DoesNotContain("202", first, StringComparison.Ordinal);
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

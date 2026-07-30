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
        var gaps = CatalogGenerator.FindBindingGaps(catalog);
        Assert.Contains(gaps, gap => gap.Contains("model mapping", StringComparison.Ordinal));
        Assert.Contains(gaps, gap => gap.Contains("team", StringComparison.Ordinal));
        Assert.Contains(gaps, gap => gap.Contains("enabled dispatching automation", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "ProjectTemplate", "Agents"))) return directory.FullName;
        throw new DirectoryNotFoundException("Test repository root not found.");
    }
}

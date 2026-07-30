using GigaClaw.Catalog;

namespace GigaClaw.Core.Tests.Services;

public sealed class CatalogGeneratorTests
{
    [Fact]
    public void Generate_reports_the_current_template_inventory_and_known_team_gap()
    {
        var catalog = new CatalogGenerator().Generate(RepositoryRoot());

        Assert.Equal(33, catalog.Summary.Agents);
        Assert.Equal(29, catalog.Summary.Automations);
        Assert.Equal(28, catalog.Summary.EnabledAutomations);
        Assert.Equal(12, catalog.Summary.ModelDefaults);
        var contentWriter = Assert.Single(catalog.Agents, agent => agent.Slug == "content-writer");
        Assert.True(contentWriter.ContractPresent);
        Assert.True(contentWriter.ModelDefaultPresent);
        Assert.True(contentWriter.ResolvedModelPresent);
        Assert.Empty(contentWriter.Teams);
        Assert.NotEmpty(contentWriter.DispatchingAutomations);
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

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "ProjectTemplate", "Agents"))) return directory.FullName;
        throw new DirectoryNotFoundException("Test repository root not found.");
    }
}

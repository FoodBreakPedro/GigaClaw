using System.IO;
using GigaClaw.Core.Models;
using Xunit;

namespace GigaClaw.Core.Tests.Web;

public class Opus48ModelSupportTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "GigaClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "GigaClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string ProjectSettings() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

    private static string LocalizationEn() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "ProjectSettings.en.json"));

    private static string LocalizationFr() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "ProjectSettings.fr.json"));

    private static string LocalizationEs() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "ProjectSettings.es.json"));

    // The model selectors (action editor, chat drawer, dashboard, member defaults)
    // all bind to ClaudeModelCatalog.Models, so the catalog is the single thing to assert on.

    [Fact]
    public void Catalog_Contains_Opus48()
    {
        Assert.Contains("claude-opus-4-8", ClaudeModelCatalog.Models);
    }

    [Fact]
    public void Catalog_Contains_Opus48_1m()
    {
        Assert.Contains("claude-opus-4-8-1m", ClaudeModelCatalog.Models);
    }

    [Fact]
    public void Catalog_Contains_Fable5()
    {
        Assert.Contains("claude-fable-5", ClaudeModelCatalog.Models);
    }

    [Fact]
    public void Catalog_Contains_Sonnet5()
    {
        Assert.Contains("claude-sonnet-5", ClaudeModelCatalog.Models);
    }

    [Fact]
    public void Catalog_DefaultModel_IsInModels()
    {
        Assert.Contains(ClaudeModelCatalog.DefaultModel, ClaudeModelCatalog.Models);
    }

    // ProjectSettings keeps a separate, hardcoded "fallback model" dropdown.

    [Fact]
    public void ProjectSettings_FallbackSelect_ContainsOpus48Option()
    {
        Assert.Contains("claude-opus-4-8", ProjectSettings());
    }

    // Localization keys present in all languages

    [Fact]
    public void Localization_En_ContainsFallbackOpus48Key()
    {
        Assert.Contains("FallbackOpus48", LocalizationEn());
    }

    [Fact]
    public void Localization_Fr_ContainsFallbackOpus48Key()
    {
        Assert.Contains("FallbackOpus48", LocalizationFr());
    }

    [Fact]
    public void Localization_Es_ContainsFallbackOpus48Key()
    {
        Assert.Contains("FallbackOpus48", LocalizationEs());
    }
}

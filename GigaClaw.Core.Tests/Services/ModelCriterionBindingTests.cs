using System.Text.Json;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// The coupling that makes <c>models.json</c>'s object form safe (doc/pack-infrastructure.md §7.2).
///
/// <para>
/// <c>DefaultModels()</c> used to accept only string values and <em>silently skip</em> anything
/// else, so introducing <c>{ "model": …, "criterion": … }</c> without teaching it the new shape
/// would have left every agent with no seeded default model and no error anywhere — the failure
/// would have surfaced weeks later as members mysteriously running on the project fallback. These
/// two assertions have to move together: the file ships objects, and the seeder reads objects.
/// </para>
/// </summary>
public sealed class ModelCriterionBindingTests
{
    private static string ModelsPath => Path.Combine(
        PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents", "models.json");

    [Fact]
    public void Every_shipped_mapping_uses_the_object_form_with_a_non_empty_criterion()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ModelsPath));

        var entries = document.RootElement.EnumerateObject()
            .Where(property => !property.Name.StartsWith('_'))
            .ToArray();

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.Equal(JsonValueKind.Object, entry.Value.ValueKind);
            Assert.True(entry.Value.TryGetProperty("model", out var model), $"{entry.Name} has no model.");
            Assert.False(string.IsNullOrWhiteSpace(model.GetString()), $"{entry.Name} has an empty model.");
            Assert.True(entry.Value.TryGetProperty("criterion", out var criterion), $"{entry.Name} has no criterion.");
            Assert.False(string.IsNullOrWhiteSpace(criterion.GetString()), $"{entry.Name} has an empty criterion.");
        }
    }

    [Fact]
    public void DefaultModels_seeds_every_object_form_mapping()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ModelsPath));
        var expected = document.RootElement.EnumerateObject()
            .Where(property => !property.Name.StartsWith('_'))
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetProperty("model").GetString()!,
                StringComparer.Ordinal);

        var seeded = new AgentsTemplateService().DefaultModels();

        Assert.Equal(expected.Count, seeded.Count);
        foreach (var (slug, model) in expected) Assert.Equal(model, seeded[slug]);
    }
}

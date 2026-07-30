using System.Text.Json.Serialization;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Tests.Web;

/// <summary>
/// ConditionEditor throws NotSupportedException for a condition type it has no UI mapping for,
/// so an automation using an unmapped condition takes the automation editor down. This asserts
/// every serializable condition type is reachable from the editor and the palette.
/// </summary>
public class ConditionEditorCoverageTests
{
    private static readonly string[] Discriminators = typeof(ConditionSpec)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
        .Cast<JsonDerivedTypeAttribute>()
        .Select(a => a.TypeDiscriminator?.ToString() ?? "")
        .Where(d => d.Length > 0)
        .ToArray();

    [Fact]
    public void Every_condition_type_is_editable()
    {
        var editor = File.ReadAllText(Path.Combine(WebComponentsDir, "ConditionEditor.razor"));

        Assert.NotEmpty(Discriminators);
        foreach (var discriminator in Discriminators)
        {
            // minDescriptionLength is deliberately edited as fieldLength (kept for back-compat).
            if (discriminator == "minDescriptionLength") continue;

            Assert.True(
                editor.Contains($"<option value=\"{discriminator}\">", StringComparison.Ordinal),
                $"ConditionEditor.razor has no type option for '{discriminator}'.");
            Assert.True(
                editor.Contains($"\"{discriminator}\" => new", StringComparison.Ordinal),
                $"ConditionEditor.razor cannot construct '{discriminator}' when the type is switched.");
        }
    }

    [Fact]
    public void Every_palette_condition_can_be_built()
    {
        var palette = File.ReadAllText(Path.Combine(WebComponentsDir, "NodePalette.razor"));
        var page = File.ReadAllText(Path.Combine(WebComponentsDir, "Pages", "Automations.razor"));

        foreach (var discriminator in Discriminators)
        {
            if (!palette.Contains($"new:condition:{discriminator}", StringComparison.Ordinal))
                continue; // Not offered in the palette; nothing to build.

            Assert.True(
                page.Contains($"\"{discriminator}\" => new", StringComparison.Ordinal),
                $"BuildCondition silently falls back for palette item '{discriminator}'.");
        }
    }

    private static string WebComponentsDir
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                    return Path.Combine(directory.FullName, "GigaClaw.Web", "Components");
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate GigaClaw repository root.");
        }
    }
}

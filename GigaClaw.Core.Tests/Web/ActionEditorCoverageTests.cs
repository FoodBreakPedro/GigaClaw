using System.Text.Json.Serialization;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Tests.Web;

/// <summary>
/// ActionEditor throws NotSupportedException for an action type it has no UI mapping for, so an
/// automation using an unmapped action takes the automation editor down for the whole project —
/// the same failure mode <see cref="ConditionEditorCoverageTests"/> guards for conditions. This
/// asserts every serializable action type is reachable from the editor, and that anything offered
/// in the palette can actually be built.
/// </summary>
public class ActionEditorCoverageTests
{
    private static readonly string[] Discriminators = typeof(ActionSpec)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
        .Cast<JsonDerivedTypeAttribute>()
        .Select(a => a.TypeDiscriminator?.ToString() ?? "")
        .Where(d => d.Length > 0)
        .ToArray();

    [Fact]
    public void Every_action_type_is_editable()
    {
        var editor = File.ReadAllText(Path.Combine(WebComponentsDir, "ActionEditor.razor"));

        Assert.NotEmpty(Discriminators);
        foreach (var discriminator in Discriminators)
        {
            Assert.True(
                editor.Contains($"<option value=\"{discriminator}\">", StringComparison.Ordinal),
                $"ActionEditor.razor has no type option for '{discriminator}'.");
            Assert.True(
                editor.Contains($"\"{discriminator}\" => new", StringComparison.Ordinal),
                $"ActionEditor.razor cannot construct '{discriminator}' when the type is switched.");
        }
    }

    [Fact]
    public void Every_palette_action_can_be_built()
    {
        var palette = File.ReadAllText(Path.Combine(WebComponentsDir, "NodePalette.razor"));
        var page = File.ReadAllText(Path.Combine(WebComponentsDir, "Pages", "Automations.razor"));

        foreach (var discriminator in Discriminators)
        {
            if (!palette.Contains($"new:action:{discriminator}", StringComparison.Ordinal))
                continue; // Not offered in the palette; nothing to build.

            Assert.True(
                page.Contains($"\"{discriminator}\" => new", StringComparison.Ordinal),
                $"BuildAction silently falls back for palette item '{discriminator}'.");
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

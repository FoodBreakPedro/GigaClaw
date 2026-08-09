using System.Text.Json;

namespace GigaClaw.Core.Tests.Web;

public sealed class MediaPreferencesSurfaceTests
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

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. path]));

    [Fact]
    public void Shared_editor_exposes_only_supported_image_and_video_sources()
    {
        var source = Read("GigaClaw.Web", "Components", "Shared", "MediaPreferencesEditor.razor");

        Assert.Contains("ImageSourcePreference.Pexels", source);
        Assert.Contains("ImageSourcePreference.LocalGeneration", source);
        Assert.Contains("ImageSourcePreference.PromptAndUpload", source);
        Assert.Contains("VideoSourcePreference.OpenMontage", source);
        Assert.Contains("VideoSourcePreference.PromptAndUpload", source);
        Assert.Contains("RequireMediaBeforeDelivery", source);
        Assert.Contains("MediaLocalFallback", source);
    }

    [Theory]
    [InlineData("Board.razor")]
    [InlineData("UnifiedBoard.razor")]
    public void Creation_surfaces_derive_defaults_and_persist_explicit_preferences(string page)
    {
        var source = Read("GigaClaw.Web", "Components", "Pages", page);

        Assert.Contains("<MediaPreferencesEditor", source);
        Assert.Contains("OnNewDeliverableChanged", source);
        Assert.Contains("DefaultImageSource", source);
        Assert.Contains("imageSource: _newTicketMediaCustomized ? _newTicketImageSource : null", source);
        Assert.Contains("videoSource: _newTicketMediaCustomized ? _newTicketVideoSource : null", source);
        Assert.Contains("requireMediaBeforeDelivery: _newTicketMediaCustomized ? _newTicketRequireMedia : null", source);
    }

    [Fact]
    public void Ticket_detail_edits_all_media_preferences_and_supports_undo()
    {
        var source = Read("GigaClaw.Web", "Components", "Pages", "Board.razor");

        Assert.Contains("ImageSource=\"@_selectedTicket.ImageSource\"", source);
        Assert.Contains("VideoSource=\"@_selectedTicket.VideoSource\"", source);
        Assert.Contains("RequireMediaBeforeDelivery=\"@_selectedTicket.RequireMediaBeforeDelivery\"", source);
        Assert.Contains("private async Task UpdateSelectedMediaAsync", source);
        Assert.Contains("UndoMediaPreferences", source);
        Assert.Contains("imageSource: oldImage", source);
        Assert.Contains("videoSource: oldVideo", source);
        Assert.Contains("requireMediaBeforeDelivery: oldRequire", source);
    }

    [Fact]
    public void Media_labels_exist_in_every_board_locale()
    {
        var requiredKeys = new[]
        {
            "VisualAssets", "Images", "ImageSource", "ImageSourcePexels", "ImageSourceLocal",
            "ImageSourcePromptUpload", "Video", "VideoSource", "VideoSourceLocal",
            "VideoSourcePromptUpload", "RequireMediaBeforeDelivery", "MediaAutomatic",
            "MediaLocalFallback", "MediaPromptUpload", "MediaOptional", "MediaRequired",
            "UndoMediaPreferences",
        };

        foreach (var language in new[] { "en", "es", "fr" })
        {
            using var document = JsonDocument.Parse(Read("GigaClaw.Core", "Localization", $"Board.{language}.json"));
            foreach (var key in requiredKeys)
                Assert.True(document.RootElement.TryGetProperty(key, out _), $"Missing {key} in {language}");
        }
    }

    [Fact]
    public void Expanded_media_form_keeps_the_create_dialog_scrollable()
    {
        var css = Read("GigaClaw.Web", "wwwroot", "app.css");

        Assert.Contains("max-height: min(92vh, 760px)", css);
        Assert.Contains(".create-popup-body", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains("@media (max-width: 560px)", css);
    }
}

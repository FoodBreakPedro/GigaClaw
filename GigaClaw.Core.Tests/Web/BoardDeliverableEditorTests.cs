using System.IO;
using System.Text.Json;

namespace GigaClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for the Checkpoint 6A ticket detail classifier. These source-level tests follow
/// the existing Board UI test pattern and keep the scope independent of a browser harness.
/// </summary>
public sealed class BoardDeliverableEditorTests
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

    private static string BoardPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "Board.razor");

    private static string UnifiedBoardPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "UnifiedBoard.razor");

    private static string LocalizationPath(string language) =>
        Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", $"Board.{language}.json");

    [Fact]
    public void Board_Detail_ExposesEditableContentTypeSelector()
    {
        var src = File.ReadAllText(BoardPath());

        Assert.Contains("@onchange=\"OnDeliverableTypeChanged\"", src);
        Assert.Contains("value=\"@(_selectedTicket.DeliverableType ?? \"\")\"", src);
        Assert.Contains("DeliverableCatalog.GetAll()", src);
    }

    [Fact]
    public void Board_Classification_DelegatesEntryAgentDerivationToTicketService()
    {
        var src = File.ReadAllText(BoardPath());
        var handler = src[src.IndexOf("private async Task OnDeliverableTypeChanged", StringComparison.Ordinal)..];
        var forwardUpdate = handler[..handler.IndexOf("PushUndo", StringComparison.Ordinal)];

        Assert.Contains("deliverableType: requestedType", src);
        Assert.DoesNotContain("assignedTo:", forwardUpdate);
        Assert.Contains("assignedTo: oldAssignee", src);
    }

    [Fact]
    public void Board_Classification_RestoresBothFieldsForUndo()
    {
        var src = File.ReadAllText(BoardPath());

        Assert.Contains("assignedTo: oldAssignee", src);
        Assert.Contains("deliverableType: oldType", src);
        Assert.Contains("UndoDeliverable", src);
    }

    [Fact]
    public void Board_RouteSummaries_UseCatalogCompletionOutcome()
    {
        var board = File.ReadAllText(BoardPath());
        var unified = File.ReadAllText(UnifiedBoardPath());

        Assert.Contains("selectedCreateDeliverable.CompletionOutcome", board);
        Assert.Contains("selectedDetailDeliverable.CompletionOutcome", board);
        Assert.Contains("selectedCreateDeliverable.CompletionOutcome", unified);
    }

    [Fact]
    public void Board_Localization_ContainsClassifierAndOutcomeKeysInAllSupportedLanguages()
    {
        foreach (var language in new[] { "en", "fr", "es" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(LocalizationPath(language)));
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("ContentType", out _), $"Missing ContentType in {language}");
            Assert.True(root.TryGetProperty("DeliverableCompletionOutcome", out _),
                $"Missing DeliverableCompletionOutcome in {language}");
            Assert.True(root.TryGetProperty("DeliverableAssigneePreserved", out _),
                $"Missing DeliverableAssigneePreserved in {language}");
        }
    }
}

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GigaClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for the unified multi-project board (/board): every project renders
/// as a collapsible swimlane, collapse state persists per-slug via localStorage, and
/// tickets can never move between projects via drag-and-drop. Source-text guards mirror
/// the pattern used by BoardFullscreenEscTests / BoardLegacyManagerRemovalTests.
/// </summary>
public class UnifiedBoardTests
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

    private static string UnifiedBoardRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "UnifiedBoard.razor");

    private static string UnifiedBoardJsPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "wwwroot", "js", "unified-board.js");

    private static string HomeRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "Home.razor");

    private static string AppRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "App.razor");

    private static string MainLayoutRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Layout", "MainLayout.razor");

    private static string ProjectCreationRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "ProjectCreation.razor");

    private static string AppSettingsRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "AppSettings.razor");

    private static string ProjectSettingsRazorPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", "ProjectSettings.razor");

    private static string UnifiedBoardEnJsonPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "UnifiedBoard.en.json");

    private static string UnifiedBoardFrJsonPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "UnifiedBoard.fr.json");

    private static string UnifiedBoardEsJsonPath() =>
        Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "UnifiedBoard.es.json");

    private static string LoadUnifiedBoard() => File.ReadAllText(UnifiedBoardRazorPath());

    // The unified board is the application default and keeps /board as a stable alias.
    [Fact]
    public void UnifiedBoard_HasRootAndBoardRoutes()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("@page \"/\"", src);
        Assert.Contains("@page \"/board\"", src);
    }

    // Case 1: every project must be loaded via ProjectService.ListProjectsAsync — no hardcoded
    // project list, so a newly created project automatically gets a lane.
    [Fact]
    public void UnifiedBoard_ListsAllProjects()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("ProjectService.ListProjectsAsync()", src);
    }

    // Case 8: lanes must be loaded concurrently (Task.WhenAll), not sequentially, so a page
    // with 7-10 projects doesn't pay N sequential round trips.
    [Fact]
    public void UnifiedBoard_LoadsLanesConcurrently()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("Task.WhenAll(", src);
    }

    // Case 5: the page must subscribe to BoardUpdateNotifier and dispose the subscription.
    [Fact]
    public void UnifiedBoard_SubscribesAndDisposesBoardUpdateNotifier()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated += OnProjectUpdatedExternal", src);
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated -= OnProjectUpdatedExternal", src);
    }

    // Case 6: clicking a ticket must deep-link to the existing per-project ticket route
    // rather than reimplementing the ticket modal.
    [Fact]
    public void UnifiedBoard_OpensTicketViaExistingBoardRoute()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("/board/{slug}/ticket/{ticketId}", src);
    }

    // Case 4: within-lane ticket moves must go through the same service call the
    // per-project Board.razor page uses (TicketService.ReorderTicketAsync).
    [Fact]
    public void UnifiedBoard_UsesReorderTicketAsyncForMoves()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("TicketService.ReorderTicketAsync(", src);
    }

    // Case 4: cross-lane drops must be rejected. The drag source lane is tracked and every
    // column's preventDefault + drop handler is gated on matching it, and OnDrop must also
    // re-check server-side (defense in depth) before ever calling ReorderTicketAsync.
    [Fact]
    public void UnifiedBoard_GatesDragOverAndDropOnSourceLane()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("_draggedFromSlug", src);
        Assert.Contains("@ondragover:preventDefault=\"@isSourceLane\"", src);
        Assert.Contains("@ondrop:preventDefault=\"@isSourceLane\"", src);
    }

    [Fact]
    public void UnifiedBoard_OnDrop_RejectsForeignLaneServerSide()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("slug != _draggedFromSlug", src);
    }

    [Fact]
    public void UnifiedBoardJs_SuppressesPostDragTicketClick()
    {
        var js = File.ReadAllText(UnifiedBoardJsPath());
        Assert.Contains("data-unified-ticket", js);
        Assert.Contains("dragend", js);
        Assert.Contains("stopImmediatePropagation", js);
    }

    [Fact]
    public void UnifiedBoard_ExposesProjectActionsAndGlobalSearch()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("UnifiedBoardSearchPlaceholder", src);
        Assert.Contains("FromAllBoards($\"/board/{slug}/dashboard\")", src);
        Assert.Contains("FromAllBoards($\"/board/{slug}/automations\")", src);
        Assert.Contains("FromAllBoards($\"/board/{slug}/settings\")", src);
        Assert.Contains("TogglePause(lane)", src);
        Assert.Contains("OpenChatDrawer(slug)", src);
        Assert.Contains("@onclick:stopPropagation", src);
    }

    [Fact]
    public void UnifiedBoard_ContainsVisibleProjectCreationControl()
    {
        Assert.Contains("<ProjectCreation", LoadUnifiedBoard());
        var creation = File.ReadAllText(ProjectCreationRazorPath());
        Assert.Contains("NewProjectPlaceholder", creation);
        Assert.Contains("CreateAndInitialize", creation);
    }

    // Case 2: collapse state must be persisted per project slug via a JS interop helper
    // living under wwwroot/js/, not inline JS in the component.
    [Fact]
    public void UnifiedBoardJs_ExistsWithPerSlugStorage()
    {
        Assert.True(File.Exists(UnifiedBoardJsPath()), "GigaClaw.Web/wwwroot/js/unified-board.js must exist.");
        var js = File.ReadAllText(UnifiedBoardJsPath());
        Assert.Contains("unified-board-collapsed-", js);
        Assert.Contains("getCollapsed", js);
        Assert.Contains("setCollapsed", js);
    }

    [Fact]
    public void UnifiedBoard_UsesJsInteropForCollapseState()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("unifiedBoardStorage.getCollapsed", src);
        Assert.Contains("unifiedBoardStorage.setCollapsed", src);
    }

    // The unified-board script must be registered in App.razor for the interop calls to work.
    [Fact]
    public void AppRazor_RegistersUnifiedBoardScript()
    {
        var src = File.ReadAllText(AppRazorPath());
        Assert.Contains("/js/unified-board.js", src);
    }

    // The legacy project-card view remains available without owning the root route.
    [Fact]
    public void HomeRazor_UsesProjectsRouteAndLinksToUnifiedBoard()
    {
        var src = File.ReadAllText(HomeRazorPath());
        Assert.Contains("@page \"/projects\"", src);
        Assert.DoesNotContain("@page \"/\"", src);
        Assert.Contains("href=\"/\"", src);
    }

    [Fact]
    public void MainLayout_RendersPersistentLogoAndGlobalNavigation()
    {
        var src = File.ReadAllText(MainLayoutRazorPath());
        Assert.Contains("GigaClaw-Logo-Horizontal.webp", src);
        Assert.Contains("href=\"/\"", src);
        Assert.Contains("href=\"/projects\"", src);
        Assert.Contains("href=\"/settings\"", src);
    }

    [Fact]
    public void Settings_AreSeparatedByScope()
    {
        var app = File.ReadAllText(AppSettingsRazorPath());
        var project = File.ReadAllText(ProjectSettingsRazorPath());

        Assert.Contains("@page \"/settings\"", app);
        Assert.Contains("ConfigureHermes", app);
        Assert.Contains("Settings.Language", app);
        Assert.DoesNotContain("ConfigureHermes", project);
        Assert.DoesNotContain("AppSettingsService", project);
        Assert.Contains("WorkspacePath", project);
        Assert.Contains("Members", project);
    }

    // Case 7: nav label localization keys must exist in both en and fr.
    [Fact]
    public void UnifiedBoardEnJson_HasAllBoardsNavLabelKey()
    {
        var json = File.ReadAllText(UnifiedBoardEnJsonPath());
        Assert.Contains("AllBoardsNavLabel", json);
        Assert.Contains("UnifiedBoardTitle", json);
    }

    [Fact]
    public void UnifiedBoardFrJson_HasAllBoardsNavLabelKey()
    {
        var json = File.ReadAllText(UnifiedBoardFrJsonPath());
        Assert.Contains("AllBoardsNavLabel", json);
        Assert.Contains("UnifiedBoardTitle", json);
    }

    [Fact]
    public void UnifiedBoardEsJson_HasAllBoardsNavLabelKey()
    {
        var json = File.ReadAllText(UnifiedBoardEsJsonPath());
        Assert.Contains("AllBoardsNavLabel", json);
        Assert.Contains("UnifiedBoardTitle", json);
    }

    // Parity: every key present in the en resource must also exist in fr and es
    // (and vice versa) so the localization fallback chain never silently ships English
    // strings under the fr/es locale, or blank keys under en.
    [Fact]
    public void UnifiedBoardJson_EnFrAndEsKeysMatch()
    {
        var enKeys = ExtractKeys(File.ReadAllText(UnifiedBoardEnJsonPath()));
        var frKeys = ExtractKeys(File.ReadAllText(UnifiedBoardFrJsonPath()));
        var esKeys = ExtractKeys(File.ReadAllText(UnifiedBoardEsJsonPath()));
        Assert.Equal(enKeys, frKeys);
        Assert.Equal(enKeys, esKeys);
    }

    private static HashSet<string> ExtractKeys(string json) =>
        Regex.Matches(json, "\"([A-Za-z0-9]+)\"\\s*:").Select(m => m.Groups[1].Value).ToHashSet();
}

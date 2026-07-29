using GigaClaw.Web;

namespace GigaClaw.Core.Tests.Web;

public class AllBoardsReturnNavigationTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null
               && !File.Exists(Path.Combine(dir, "GigaClaw.sln"))
               && !File.Exists(Path.Combine(dir, "GigaClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "GigaClaw.Web", "Components", "Pages", $"{name}.razor"));

    [Theory]
    [InlineData("/")]
    [InlineData("/board/demo")]
    [InlineData("/board/demo?returnTo=%2F")]
    public void NavigationReturn_AcceptsLocalPaths(string path)
    {
        Assert.True(NavigationReturn.IsSafeLocalPath(path));
        Assert.Equal(path, NavigationReturn.Resolve(path, "/fallback"));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("//example.com")]
    [InlineData("/\\example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void NavigationReturn_RejectsExternalOrMalformedPaths(string path)
    {
        Assert.False(NavigationReturn.IsSafeLocalPath(path));
        Assert.Equal("/fallback", NavigationReturn.Resolve(path, "/fallback"));
    }

    [Fact]
    public void NavigationReturn_EncodesNestedReturnPath()
    {
        Assert.Equal(
            "/board/demo/settings?returnTo=%2Fboard%2Fdemo%3FreturnTo%3D%252F",
            NavigationReturn.WithReturnTo(
                "/board/demo/settings",
                "/board/demo?returnTo=%2F"));
    }

    [Fact]
    public void UnifiedBoard_ProvidesAllBoardsReturnContextForProjectPagesAndTickets()
    {
        var source = Page("UnifiedBoard");
        Assert.Contains("FromAllBoards($\"/board/{slug}\")", source);
        Assert.Contains("FromAllBoards($\"/board/{slug}/dashboard\")", source);
        Assert.Contains("FromAllBoards($\"/board/{slug}/settings\")", source);
        Assert.Contains("FromAllBoards($\"/board/{slug}/ticket/{ticketId}\")", source);
    }

    [Theory]
    [InlineData("Board")]
    [InlineData("Dashboard")]
    [InlineData("ProjectSettings")]
    [InlineData("Automations")]
    public void ProjectPages_ConsumeSafeReturnContext(string page)
    {
        var source = Page(page);
        Assert.Contains("SupplyParameterFromQuery(Name = \"returnTo\")", source);
        Assert.Contains("NavigationReturn.Resolve(ReturnTo", source);
        Assert.Contains("href=\"@ReturnTarget\"", source);
    }

    [Fact]
    public void TicketClose_UsesItsImmediateReturnTarget()
    {
        var source = Page("Board");
        Assert.Contains("NavigationReturn.WithReturnTo(ticketPath, CurrentBoardPath)", source);
        Assert.Contains("Navigation.NavigateTo(ReturnTarget, replace: true)", source);
    }

    [Fact]
    public void UnifiedBoard_BacklogOffersInlineTicketCreation()
    {
        var source = Page("UnifiedBoard");
        Assert.Contains("column.Id == lane.Columns[0].Id", source);
        Assert.Contains("OpenCreateTicket(lane, column.Name)", source);
        Assert.Contains("CreateTicketFromLane", source);
        Assert.Contains("status: _newTicketStatus", source);
    }
}

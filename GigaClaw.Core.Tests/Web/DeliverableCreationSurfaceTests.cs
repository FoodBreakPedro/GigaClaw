using System.Text.RegularExpressions;

namespace GigaClaw.Core.Tests.Web;

/// <summary>
/// Both ticket-creation surfaces must offer the deliverable catalog and pass the chosen slug to
/// <c>TicketService</c>. A surface that renders the selector but drops the value on submit looks
/// correct and silently creates an unrouted ticket, so the pass-through is asserted separately from
/// the control.
/// </summary>
public class DeliverableCreationSurfaceTests
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

    private static string Pages(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Web", "Components", "Pages", file));

    private static string BoardEnJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "GigaClaw.Core", "Localization", "Board.en.json"));

    [Theory]
    [InlineData("Board.razor")]
    [InlineData("UnifiedBoard.razor")]
    public void Creation_surface_offers_the_deliverable_catalog(string page)
    {
        var source = Pages(page);
        Assert.Contains("DeliverableCatalog.GetAll()", source, StringComparison.Ordinal);
        Assert.Contains("_newTicketDeliverable", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Board.razor")]
    [InlineData("UnifiedBoard.razor")]
    public void Creation_surface_passes_the_chosen_deliverable_to_the_service(string page)
    {
        Assert.Contains("deliverableType:", Pages(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Board.razor")]
    [InlineData("UnifiedBoard.razor")]
    public void Creation_surface_says_whether_work_starts_now_or_waits_for_todo(string page)
    {
        // Assigning an entry agent does not pull a ticket out of Backlog. Leaving that unsaid is the
        // specific confusion checkpoint 4 exists to remove, so both surfaces must state it.
        var source = Pages(page);
        Assert.Contains("DeliverableStartsAtTodo", source, StringComparison.Ordinal);
        Assert.Contains("DeliverableStartsNow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Board_keeps_an_explicit_assignee_escape_hatch_and_says_it_wins()
    {
        var source = Pages("Board.razor");
        Assert.Contains("AdvancedAssignment", source, StringComparison.Ordinal);
        Assert.Contains("DeliverableAssigneeWins", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_deliverable_localization_key_the_surfaces_use_exists_in_english()
    {
        // The fallback chain ends at the raw key, so a typo ships "DeliverableRuns" to the user
        // rather than failing anywhere.
        var keys = ExtractKeys(BoardEnJson());
        var used = Regex.Matches(Pages("Board.razor") + Pages("UnifiedBoard.razor"), @"L\[""(Deliverable[A-Za-z0-9]*|AdvancedAssignment)""\]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(used);
        foreach (var key in used)
            Assert.True(keys.Contains(key), $"Board.en.json has no key '{key}'.");
    }

    private static HashSet<string> ExtractKeys(string json) =>
        Regex.Matches(json, "\"([A-Za-z0-9]+)\"\\s*:").Select(m => m.Groups[1].Value).ToHashSet();
}

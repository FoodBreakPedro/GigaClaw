using GigaClaw.Core.Automation.Handoffs;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// C8's dedup deliverable: a deterministic, I/O-free merge of lane findings by a normalized
/// <c>location|category</c> key, keeping per-lane attribution for every surviving finding.
/// </summary>
public sealed class FindingDeduplicatorTests
{
    [Fact]
    public void Two_lanes_naming_the_same_file_and_issue_merge_into_one_attributed_finding()
    {
        var findings = new[]
        {
            new LaneFinding("accessibility-lane", "ui-auditor", "GigaClaw.Web/wwwroot/app.css: focus ring removed on icon buttons", Blocking: true),
            new LaneFinding("coverage-lane", "qa-tester", "GigaClaw.Web/wwwroot/app.css:120 missing focus indicator on icon buttons breaks keyboard nav", Blocking: false),
        };

        var deduped = FindingDeduplicator.Dedupe(findings);

        var merged = Assert.Single(deduped);
        Assert.Equal(2, merged.Lanes.Count);
        Assert.Contains(merged.Lanes, lane => lane.TaskKey == "accessibility-lane" && lane.AgentSlug == "ui-auditor");
        Assert.Contains(merged.Lanes, lane => lane.TaskKey == "coverage-lane" && lane.AgentSlug == "qa-tester");
        // Blocking survives the merge if any contributing lane raised it as blocking.
        Assert.True(merged.Blocking);
        // The representative statement is the first-seen lane's wording.
        Assert.Equal(findings[0].Statement, merged.Statement);
    }

    [Fact]
    public void Findings_about_different_files_do_not_merge()
    {
        var findings = new[]
        {
            new LaneFinding("accessibility-lane", "ui-auditor", "app.css: focus ring removed", Blocking: false),
            new LaneFinding("coverage-lane", "qa-tester", "TicketService.cs: untested null branch", Blocking: false),
        };

        var deduped = FindingDeduplicator.Dedupe(findings);

        Assert.Equal(2, deduped.Count);
        Assert.All(deduped, finding => Assert.Single(finding.Lanes));
    }

    [Fact]
    public void Findings_in_the_same_file_but_different_categories_do_not_merge()
    {
        var findings = new[]
        {
            new LaneFinding("accessibility-lane", "ui-auditor", "app.css: contrast ratio fails WCAG AA on the toolbar", Blocking: true),
            new LaneFinding("coverage-lane", "qa-tester", "app.css: untested regression around the toolbar breakpoint", Blocking: false),
        };

        var deduped = FindingDeduplicator.Dedupe(findings);

        Assert.Equal(2, deduped.Count);
    }

    [Fact]
    public void No_findings_degrades_to_an_empty_list_not_an_error()
    {
        var deduped = FindingDeduplicator.Dedupe([]);

        Assert.Empty(deduped);
    }

    [Fact]
    public void Order_is_first_seen_and_deterministic_across_repeated_calls()
    {
        var findings = new[]
        {
            new LaneFinding("a", "agent-a", "one.cs: first issue", Blocking: false),
            new LaneFinding("b", "agent-b", "two.cs: second issue", Blocking: false),
            new LaneFinding("c", "agent-c", "one.cs: first issue restated", Blocking: false),
        };

        var first = FindingDeduplicator.Dedupe(findings);
        var second = FindingDeduplicator.Dedupe(findings);

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(f => f.Key), second.Select(f => f.Key));
        // one.cs's finding was seen first, so it stays first even though a third lane repeated it later.
        Assert.Contains("one.cs", first[0].Key, StringComparison.Ordinal);
        Assert.Equal(2, first[0].Lanes.Count);
    }

    [Fact]
    public void Uncategorized_findings_with_no_shared_words_do_not_collide_on_an_empty_bucket()
    {
        var findings = new[]
        {
            new LaneFinding("a", "agent-a", "Something entirely idiosyncratic happened here", Blocking: false),
            new LaneFinding("b", "agent-b", "A wholly different observation was made there", Blocking: false),
        };

        var deduped = FindingDeduplicator.Dedupe(findings);

        Assert.Equal(2, deduped.Count);
    }
}

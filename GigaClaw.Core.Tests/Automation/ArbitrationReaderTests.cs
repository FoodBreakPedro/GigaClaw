using GigaClaw.Core.Automation.Handoffs;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// The mechanically-enforceable half of hypothesis-debug's arbitration: reading the debug-lead's
/// winner + reason off its own comment, so the host (not the lead's prose compliance) closes the
/// losing lanes.
/// </summary>
public sealed class ArbitrationReaderTests
{
    [Fact]
    public void Reads_the_winner_and_reason_off_a_well_formed_comment()
    {
        var comment = """
            Reviewed both hypotheses against the repro.

            GIGACLAW-ARBITRATION v1 winner=investigator-a-lane
            reason: the stack trace in investigator-a's evidence matches the null-cache-lookup path exactly.
            """;

        Assert.True(ArbitrationReader.TryRead(comment, out var decision));
        Assert.Equal("investigator-a-lane", decision!.Winner);
        Assert.Equal(
            "the stack trace in investigator-a's evidence matches the null-cache-lookup path exactly.",
            decision.Reason);
    }

    [Fact]
    public void No_marker_reads_as_no_decision()
    {
        Assert.False(ArbitrationReader.TryRead("Just a status update, no decision yet.", out var decision));
        Assert.Null(decision);
    }

    [Fact]
    public void No_reason_line_still_reads_the_winner_with_a_placeholder_reason()
    {
        Assert.True(ArbitrationReader.TryRead(
            "GIGACLAW-ARBITRATION v1 winner=investigator-b-lane", out var decision));
        Assert.Equal("investigator-b-lane", decision!.Winner);
        Assert.Equal("no reason recorded", decision.Reason);
    }

    [Fact]
    public void The_last_marker_in_a_comment_wins()
    {
        var comment = """
            GIGACLAW-ARBITRATION v1 winner=investigator-a-lane
            reason: first guess.

            On reflection:
            GIGACLAW-ARBITRATION v1 winner=investigator-b-lane
            reason: the evidence actually points the other way.
            """;

        Assert.True(ArbitrationReader.TryRead(comment, out var decision));
        Assert.Equal("investigator-b-lane", decision!.Winner);
        Assert.Equal("the evidence actually points the other way.", decision.Reason);
    }

    [Fact]
    public void Latest_scans_newest_comment_first_across_a_ticket()
    {
        var comments = new List<string>
        {
            "GIGACLAW-ARBITRATION v1 winner=investigator-a-lane\nreason: early guess, later revised.",
            "Some unrelated status update.",
            "GIGACLAW-ARBITRATION v1 winner=investigator-b-lane\nreason: confirmed after a second repro.",
        };

        var decision = ArbitrationReader.Latest(comments);

        Assert.NotNull(decision);
        Assert.Equal("investigator-b-lane", decision!.Winner);
    }

    [Fact]
    public void Latest_over_no_comments_is_null()
    {
        Assert.Null(ArbitrationReader.Latest([]));
    }
}

using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Verdicts;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Phase 1.2: the reviewer-side twin of <see cref="RepairLoop"/>. The budget is recounted from the
/// ticket's own receipts, so these tests are about what the comment trail means — not about any
/// stored counter, of which there is none.
/// </summary>
public class ReviewerRetryTests
{
    private static VerdictComment Comment(string content, string author = "automation")
        => new(content, author, DateTime.UtcNow);

    private static List<VerdictComment> Trail(params string[] contents)
        => contents.Select(c => Comment(c)).ToList();

    [Fact]
    public void A_ticket_with_no_receipts_has_its_whole_budget()
    {
        var state = ReviewerRetry.Resolve(Trail("just prose", "GIGACLAW-VERDICT v1 qa-tester FIX artifact-x"), 1);

        Assert.Equal(0, state.RetriesUsed);
        Assert.Equal(1, state.Remaining);
        Assert.False(state.Exhausted);
    }

    [Fact]
    public void One_receipt_spends_the_default_budget()
    {
        var state = ReviewerRetry.Resolve(
            Trail(ReviewerRetry.RenderReceipt(12, "qa-tester")), ReviewerRetry.DefaultMaxRetries);

        Assert.Equal(1, state.RetriesUsed);
        Assert.True(state.Exhausted);
    }

    /// <summary>
    /// The agent filter matches <c>verdictIs</c>'s own, so one reviewer's flakiness never spends
    /// the budget of the reviewer actually being gated on.
    /// </summary>
    [Fact]
    public void Another_reviewers_receipt_does_not_spend_this_reviewers_budget()
    {
        var trail = Trail(ReviewerRetry.RenderReceipt(12, "ui-auditor"));

        Assert.False(ReviewerRetry.Resolve(trail, 1, agent: "qa-tester").Exhausted);
        Assert.True(ReviewerRetry.Resolve(trail, 1, agent: "ui-auditor").Exhausted);
        Assert.True(ReviewerRetry.Resolve(trail, 1).Exhausted); // no filter = any reviewer
    }

    [Fact]
    public void A_gate_receipt_closes_the_episode_and_returns_the_budget()
    {
        var state = ReviewerRetry.Resolve(
            Trail(
                ReviewerRetry.RenderReceipt(12, "qa-tester"),
                "GIGACLAW-GATE v1 ticket-12 blocked — the reviewer's verdict does not permit an exit.",
                "GIGACLAW-VERDICT v1 qa-tester FIX artifact-x"),
            1);

        Assert.Equal(0, state.RetriesUsed);
        Assert.False(state.Exhausted);
    }

    [Fact]
    public void A_repair_escalation_receipt_also_closes_the_episode()
    {
        var state = ReviewerRetry.Resolve(
            Trail(
                ReviewerRetry.RenderReceipt(12, "qa-tester"),
                "GIGACLAW-REPAIR v1 ticket-12 escalated 2/2"),
            1);

        Assert.False(state.Exhausted);
    }

    /// <summary>
    /// The receipt must not look like an episode boundary to itself, or the budget could never be
    /// spent — the one failure mode that would turn "retry once" into "retry forever".
    /// </summary>
    [Fact]
    public void The_retry_receipt_is_not_its_own_episode_boundary()
    {
        var receipt = ReviewerRetry.RenderReceipt(12, "qa-tester");

        Assert.False(ReviewerRetry.IsEpisodeBoundary(receipt));
        Assert.True(ReviewerRetry.IsRetryReceipt(receipt, out var reviewer));
        Assert.Equal("qa-tester", reviewer);
        Assert.Equal(2, ReviewerRetry.Resolve(Trail(receipt, receipt), 1).RetriesUsed);
    }

    [Fact]
    public void A_verdict_comment_is_never_mistaken_for_a_receipt()
    {
        Assert.False(ReviewerRetry.IsRetryReceipt(
            "GIGACLAW-VERDICT v1 qa-tester SHIP artifact-sha256:abc", out _));
        Assert.False(ReviewerRetry.IsEpisodeBoundary(
            "GIGACLAW-VERDICT v1 qa-tester SHIP artifact-sha256:abc"));
    }

    // ── The condition on top of it ──────────────────────────────────────────

    [Theory]
    [InlineData("withinCap", 0, true)]
    [InlineData("withinCap", 1, false)]
    [InlineData("exhausted", 0, false)]
    [InlineData("exhausted", 1, true)]
    public void The_condition_reads_the_two_modes(string mode, int used, bool expected)
    {
        var spec = new ReviewerRetryBudgetConditionSpec { Mode = mode, MaxRetries = 1 };
        Assert.Equal(expected, ConditionEvaluators.ReviewerRetryBudget(spec, new ReviewerRetryState(1, used)));
    }

    /// <summary>A typo in <c>mode</c> stalls the ticket rather than looping it, exactly as
    /// <c>repairBudget</c> does — an unknown mode matches neither arm.</summary>
    [Fact]
    public void An_unknown_mode_matches_nothing()
    {
        var spec = new ReviewerRetryBudgetConditionSpec { Mode = "withinCapp" };
        Assert.False(ConditionEvaluators.ReviewerRetryBudget(spec, new ReviewerRetryState(1, 0)));
        Assert.False(ConditionEvaluators.ReviewerRetryBudget(spec, new ReviewerRetryState(1, 5)));
    }

    /// <summary>An unestablishable budget blocks for a human rather than re-running a reviewer.</summary>
    [Fact]
    public void An_unknowable_budget_reads_as_exhausted()
    {
        Assert.True(ConditionEvaluators.ReviewerRetryBudget(
            new ReviewerRetryBudgetConditionSpec { Mode = "exhausted" }, state: null));
        Assert.False(ConditionEvaluators.ReviewerRetryBudget(
            new ReviewerRetryBudgetConditionSpec { Mode = "withinCap" }, state: null));
    }
}

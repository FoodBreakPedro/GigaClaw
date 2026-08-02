using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Verdicts;

/// <summary>
/// The reviewer-retry budget for one ticket, derived from its comment trail. Nothing here is
/// stored: re-deriving it after an engine restart yields the same numbers, because the evidence is
/// the receipts themselves — exactly the contract <see cref="RepairLoopState"/> keeps for the
/// author-side repair loop.
/// </summary>
public sealed record ReviewerRetryState(int MaxRetries, int RetriesUsed)
{
    public int Remaining => Math.Max(0, MaxRetries - RetriesUsed);

    /// <summary>True once the ticket must block instead of asking the reviewer again.</summary>
    public bool Exhausted => RetriesUsed >= MaxRetries;
}

/// <summary>
/// The bounded reviewer retry — "return to sender" for a verdict that never arrived. An
/// <c>INVALID</c>, <c>STALE</c> or <c>MISSING</c> outcome is the <em>reviewer's</em> output
/// failing, not the author's work, so the gate re-runs the reviewer once before it blocks the
/// ticket. <see cref="RepairLoop"/> is the mirror image on the author's side, and the two budgets
/// are deliberately separate: <see cref="RepairLoop.Resolve"/> skips unreadable verdicts entirely,
/// so a flaky reviewer never spends a repair round and would otherwise block on its first bad
/// answer.
/// <para>
/// <b>Where the count comes from.</b> Not a counter column and not engine memory — the ticket's own
/// comments. Every <c>GIGACLAW-REREVIEW v1</c> receipt the retry arm posts is one retry. That makes
/// the number auditable, restart-proof, and immune to a resumed run restarting it.
/// </para>
/// <para>
/// <b>Where an episode starts.</b> Any gate receipt (<c>GIGACLAW-GATE v1</c>) or repair escalation
/// (<c>GIGACLAW-REPAIR v1</c>) closes it: both mean the gate reached a decision on this round of
/// review, so whatever fails next is a fresh round-one rather than a permanently spent budget. The
/// retry receipt itself carries neither marker, so it cannot close its own episode.
/// </para>
/// </summary>
public static class ReviewerRetry
{
    /// <summary>Reviewer re-dispatches allowed per episode when an automation names no cap.</summary>
    public const int DefaultMaxRetries = 1;

    /// <summary>Marker line of the retry receipt the retry arm posts before re-dispatching.</summary>
    public const string RetryMarkerPrefix = "GIGACLAW-REREVIEW v1";

    private static readonly Regex RetryRegex = new(
        @"^GIGACLAW-REREVIEW\s+v1\s+ticket-(?<ticket>[0-9]+)\s+reviewer=(?<agent>[^\s]+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex GateReceiptRegex = new(
        @"^GIGACLAW-GATE\s+v1\s+ticket-[0-9]+\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// True when the comment is a retry receipt. <paramref name="reviewer"/> receives the slug the
    /// receipt names, so a per-reviewer budget can ignore another reviewer's retries.
    /// </summary>
    public static bool IsRetryReceipt(string? commentBody, out string? reviewer)
    {
        reviewer = null;
        if (commentBody is null) return false;
        var match = RetryRegex.Match(commentBody);
        if (!match.Success) return false;
        reviewer = match.Groups["agent"].Value;
        return true;
    }

    /// <summary>True when the comment is a receipt that closes the current review episode.</summary>
    public static bool IsEpisodeBoundary(string? commentBody)
        => commentBody is not null
            && (GateReceiptRegex.IsMatch(commentBody) || RepairLoop.IsEscalationReceipt(commentBody));

    /// <summary>
    /// Renders the retry receipt. The marker line is what the recount reads; the prose below it is
    /// what an owner reading the ticket needs, so the two never drift apart into separate sources.
    /// <para>
    /// Rendered by the <em>engine</em>, at the moment a re-review is actually dispatched — never by
    /// an <c>addComment</c> action ahead of the <c>runAgent</c>. A configured receipt is committed
    /// whether or not the dispatch happens, so a reviewer that is merely busy (its concurrency
    /// group taken) would spend the budget without being asked anything, leaving the ticket in
    /// Review with no arm left that matches it. The receipt is evidence of a dispatch, so only a
    /// dispatch may write it.
    /// </para>
    /// </summary>
    public static string RenderReceipt(int ticketId, string reviewer)
        => $"""
        {RetryMarkerPrefix} ticket-{ticketId} reviewer={reviewer}

        The newest {reviewer} verdict on this ticket could not be used: it resolved to `INVALID` (it
        breaks the v1 verdict contract in `.agents/scripts/verdict.schema.json`), `STALE` (the
        reviewed artifact changed after the review) or `MISSING` (the reviewer answered in prose and
        posted no parseable verdict).

        That is the reviewer's own output failing, not the work — so the automation engine has sent
        the review back to {reviewer} for one more pass instead of blocking the ticket or asking the
        author to repair something nobody actually refused. The ticket stays in Review and keeps its
        assignee.

        This receipt is written only because the re-review was dispatched, and it is what spends the
        retry budget: a second unusable verdict blocks the ticket for the owner.
        """;

    /// <summary>
    /// Recounts the reviewer-retry budget from the comment trail. <paramref name="agent"/>
    /// restricts the count to one reviewer, matching <c>verdictIs</c>'s own agent filter, so a
    /// second reviewer's retry never spends the budget of the one being gated on.
    /// </summary>
    public static ReviewerRetryState Resolve(
        IReadOnlyList<VerdictComment> comments,
        int maxRetries,
        string? agent = null)
    {
        var used = 0;
        foreach (var comment in comments)
        {
            // Order matters: a retry receipt carries neither boundary marker, so checking the
            // boundary first cannot swallow the retry it is meant to count.
            if (IsEpisodeBoundary(comment.Content))
            {
                used = 0;
                continue;
            }

            if (!IsRetryReceipt(comment.Content, out var reviewer))
                continue;
            if (!string.IsNullOrEmpty(agent) && !string.Equals(reviewer, agent, StringComparison.Ordinal))
                continue;

            used++;
        }

        return new ReviewerRetryState(Math.Max(0, maxRetries), used);
    }
}

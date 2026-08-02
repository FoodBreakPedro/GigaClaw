using System.Globalization;
using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Plan 2.1 — the per-ticket spend ceiling (<see cref="AutomationConfig.MaxTicketCostUsd"/>) and the
/// receipt that records a breach. The ceiling itself is enforced in
/// <see cref="RunStateManager.ShouldSkipAsync"/>; everything here is the bookkeeping around it.
/// <para>
/// <b>No agent is exempt — not even the triage agent.</b> <see cref="AutomationConfig.DailyBudgetUsd"/>
/// exempts the <c>ceo</c> group; this cap deliberately exempts nobody, including
/// <see cref="TriageAgent"/>. Exempting the triage agent reads as an obvious kindness (the point of
/// handing the ticket to it *is* re-scoping) but it builds a funded loop: over-cap ticket → assigned
/// to groomer → groomer runs (billed) → groomer re-routes to the producing agent → that dispatch is
/// skipped for being over cap → the ticket comes straight back to groomer → groomer runs again. Each
/// lap costs money and nothing about the ticket has changed, which is precisely the runaway the cap
/// exists to stop. A cap with an exemption is a suggestion. So the breach is a hard stop: the ticket
/// parks with groomer as its owner, and the re-scope happens once a human raises
/// <c>maxTicketCostUsd</c>, splits the ticket, or accepts it as done — all of which the receipt
/// spells out.
/// </para>
/// <para>
/// <b>Where the "already reported" bit lives.</b> Nowhere but the ticket's own comment trail — the
/// same stateless recount <see cref="Verdicts.RepairLoop"/> and <see cref="Verdicts.ReviewerRetry"/>
/// use. A breach is reported at most once per <i>episode</i>, and an episode is keyed on the cap
/// value the receipt names: while the cap is unchanged the ticket can be re-evaluated on every tick
/// without posting a second comment or re-assigning the ticket a second time, and raising the cap
/// opens a fresh episode that may report again when the new ceiling is reached in turn.
/// </para>
/// </summary>
public static class TicketCostCap
{
    /// <summary>Agent the breached ticket is handed to for re-scoping. A protected seeded member.</summary>
    public const string TriageAgent = "groomer";

    /// <summary>Marker line of the breach receipt. The recount reads this, humans read the prose below it.</summary>
    public const string MarkerPrefix = "GIGACLAW-COSTCAP v1";

    /// <summary>Author recorded on the breach comment, matching every other automation-authored comment.</summary>
    public const string ReceiptAuthor = "automation";

    private const string AmountFormat = "0.######";

    private static readonly Regex ReceiptRegex = new(
        @"^GIGACLAW-COSTCAP\s+v1\s+ticket-(?<ticket>[0-9]+)\s+cap=(?<cap>[0-9]+(?:\.[0-9]+)?)\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static string Amount(decimal value) => value.ToString(AmountFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the comment is a breach receipt for <paramref name="ticketId"/> written against
    /// <paramref name="cap"/>. A receipt naming a different cap belongs to a closed episode — the
    /// ceiling has moved since — and does not suppress a fresh report.
    /// </summary>
    private static bool IsReceiptForCap(string? commentBody, int ticketId, decimal cap)
    {
        if (commentBody is null) return false;
        foreach (Match match in ReceiptRegex.Matches(commentBody))
        {
            if (!int.TryParse(match.Groups["ticket"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                continue;
            if (id != ticketId) continue;
            if (!decimal.TryParse(match.Groups["cap"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var receiptCap))
                continue;
            // Compared as rendered, not as decimals: a receipt only ever carries the rendered form,
            // so a cap differing from it past AmountFormat's precision could never match one — every
            // tick would read as a fresh episode and post another receipt, the exact spam this guard
            // exists to stop. `==` would be shorter and would reintroduce that loop.
            if (string.Equals(Amount(receiptCap), Amount(cap), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when this breach has already been reported — i.e. the ticket already carries a receipt
    /// for the same cap. The one guard that keeps a per-tick trigger from spamming the ticket.
    /// </summary>
    public static bool AlreadyReported(IEnumerable<string?> commentBodies, int ticketId, decimal cap)
        => commentBodies.Any(body => IsReceiptForCap(body, ticketId, cap));

    /// <summary>
    /// Renders the breach receipt: the machine-readable marker line the recount reads, then the
    /// explanation an owner needs, so the two can never drift into separate sources.
    /// </summary>
    public static string RenderReceipt(int ticketId, decimal cap, decimal spent, string skippedAgent)
        => $"""
            {MarkerPrefix} ticket-{ticketId} cap={Amount(cap)} spent={Amount(spent)}

            **Per-ticket cost cap reached.** This ticket has consumed ${Amount(spent)} of agent budget
            against a `maxTicketCostUsd` of ${Amount(cap)}, so the `{skippedAgent}` dispatch was skipped
            and every further dispatch on this ticket will be skipped too — including `{TriageAgent}`'s
            own. The cap has no agent exemption on purpose: re-scoping a ticket that already spent its
            whole budget is a decision, not a retry.

            Assigned to `{TriageAgent}` so it is owned rather than orphaned. To let work continue, a
            human has to change something: split or narrow the ticket, raise `maxTicketCostUsd` in
            `.agents/automations.json`, or close it.
            """;
}

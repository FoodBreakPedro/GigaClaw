using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Handoffs;

/// <summary>A debug-lead's arbitration: which lane's hypothesis won, and why.</summary>
public sealed record ArbitrationDecision(string Winner, string Reason);

/// <summary>
/// Reads a <c>hypothesis-debug</c> lead's decision off its synthesis-ticket comment. Deliberately
/// not a frozen, versioned contract like the verdict or handoff schemas — this is host-internal
/// wiring for one C8 preset, not a cross-agent surface other packs are expected to emit.
/// <para>
/// The format is the smallest thing a lead can reliably produce and a host can reliably parse: a
/// marker line naming the winning task's key, and a free-text reason line. <see cref="TeamRunService"/>
/// reads it (opt-in via <c>TeamDefinition.RequireEvidenceCitingArbitration</c>) and posts the closing
/// comment on every losing lane's own ticket — the mechanically-enforceable half of "losing
/// hypotheses closed with reasons": the lead only has to state the winner, the host does the closing.
/// </para>
/// </summary>
public static class ArbitrationReader
{
    public const string MarkerPrefix = "GIGACLAW-ARBITRATION v1";

    private static readonly Regex MarkerRegex = new(
        @"^GIGACLAW-ARBITRATION\s+v1\s+winner=(?<winner>[a-z0-9][a-z0-9-]*)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ReasonRegex = new(
        @"^reason:\s*(?<reason>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Reads the newest marker in one comment body, or false when none is present.</summary>
    public static bool TryRead(string? commentBody, out ArbitrationDecision? decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(commentBody)) return false;

        Match? marker = null;
        foreach (Match candidate in MarkerRegex.Matches(commentBody))
            marker = candidate; // last marker in the comment wins, same rule as verdicts/handoffs.
        if (marker is null) return false;

        var reasonMatch = ReasonRegex.Match(commentBody, marker.Index + marker.Length);
        var reason = reasonMatch.Success ? reasonMatch.Groups["reason"].Value.Trim() : "no reason recorded";

        decision = new ArbitrationDecision(marker.Groups["winner"].Value, reason);
        return true;
    }

    /// <summary>Newest readable decision among a ticket's comments (oldest first), or null.</summary>
    public static ArbitrationDecision? Latest(IReadOnlyList<string> commentBodies)
    {
        for (var index = commentBodies.Count - 1; index >= 0; index--)
            if (TryRead(commentBodies[index], out var decision))
                return decision;
        return null;
    }
}

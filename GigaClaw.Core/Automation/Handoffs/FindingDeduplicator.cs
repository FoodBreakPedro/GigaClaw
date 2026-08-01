using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Handoffs;

/// <summary>One lane's raw claim before merging — a single <see cref="RunHandoff.OpenLoops"/> entry.</summary>
public sealed record LaneFinding(string TaskKey, string AgentSlug, string Statement, bool Blocking);

/// <summary>
/// Every lane that raised an equivalent finding, collapsed to one entry. <see cref="Lanes"/> keeps
/// per-lane attribution — the merge never hides who found what, only that two lanes found the same
/// thing.
/// </summary>
public sealed record DedupedFinding(string Key, string Statement, bool Blocking, IReadOnlyList<LaneFinding> Lanes);

/// <summary>
/// C8's dedup deliverable: a deterministic, I/O-free merge of the findings scattered across a
/// <c>parallel-review</c> run's lane handoffs, so the synthesizer reads one attributed list instead
/// of re-deriving overlap from N renderings by eye.
/// <para>
/// A <em>finding</em> is a lane's <see cref="RunHandoff.OpenLoops"/> entry — the closest the frozen
/// v1 handoff contract has to "thing this lane noticed" — so no schema change was needed to source
/// this from real handoffs. Two findings merge when they normalize to the same
/// <c>location|category</c> key: a <c>file[:line]</c>-shaped token pulled out of the statement (or
/// empty when the statement names none), paired with a coarse keyword bucket (falling back to the
/// statement's first significant words when no keyword matches, so two uncategorized findings still
/// don't collide on an empty bucket unless their words actually agree).
/// </para>
/// <para>
/// Pure function of its input: same findings in, same deduped list out, in first-seen order — so it
/// is unit-testable without a project, a ticket or a run, exactly like <see cref="Models.TeamJoinEvaluator"/>.
/// </para>
/// </summary>
public static class FindingDeduplicator
{
    /// <summary>Merges findings by their normalized key, preserving first-seen order and every lane's attribution.</summary>
    public static IReadOnlyList<DedupedFinding> Dedupe(IEnumerable<LaneFinding> findings)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, List<LaneFinding>>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var key = NormalizeKey(finding.Statement);
            if (!byKey.TryGetValue(key, out var lanes))
            {
                lanes = [];
                byKey[key] = lanes;
                order.Add(key);
            }

            lanes.Add(finding);
        }

        return [.. order.Select(key =>
        {
            var lanes = byKey[key];
            return new DedupedFinding(
                key,
                lanes[0].Statement,
                lanes.Any(lane => lane.Blocking),
                lanes);
        })];
    }

    /// <summary>The <c>location|category</c> key two equivalent findings normalize to. Internal for its own unit tests.</summary>
    internal static string NormalizeKey(string statement) =>
        $"{ExtractLocation(statement)}|{ExtractCategory(statement)}";

    // The line-number suffix (":123") is deliberately excluded from the key: two lanes rarely cite
    // the exact same line for the same regression, and file-level grouping is what makes an
    // accessibility and a coverage lane's independent mentions of one bad file actually merge.
    private static readonly Regex LocationRegex = new(
        @"(?<file>[\w][\w/.-]*\.[A-Za-z]{1,10})(:\d+)?", RegexOptions.Compiled);

    private static string ExtractLocation(string statement)
    {
        var match = LocationRegex.Match(statement);
        return match.Success ? match.Groups["file"].Value.ToLowerInvariant() : "";
    }

    // Coarse, deliberately small vocabulary — this is a stand-in dedup key until the dedicated
    // reviewer roles (G5) settle on richer finding categories. Order matters: first match wins.
    private static readonly (string Category, string[] Keywords)[] Categories =
    [
        ("contrast", ["contrast", "wcag"]),
        ("focus", ["focus"]),
        ("concurrency", ["concurren", "blocking call", "race condition"]),
        ("coverage", ["coverage", "untested", "regression", "test case"]),
        ("convention", ["convention", "pattern", "dead code", "unused"]),
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "this", "that", "with", "from", "have", "does", "will", "should", "there", "which", "still",
    };

    private static readonly Regex WordRegex = new(@"[a-z0-9]{4,}", RegexOptions.Compiled);

    private static string ExtractCategory(string statement)
    {
        var lowered = statement.ToLowerInvariant();
        foreach (var (category, keywords) in Categories)
            if (keywords.Any(keyword => lowered.Contains(keyword, StringComparison.Ordinal)))
                return category;

        // No keyword matched: fall back to the statement's own words, so two uncategorized findings
        // still merge only when they actually agree rather than sharing an empty bucket.
        var words = WordRegex.Matches(lowered)
            .Select(match => match.Value)
            .Where(word => !Stopwords.Contains(word))
            .Take(2);
        return string.Join("-", words);
    }
}

using System.Text;

namespace GigaClaw.Eval;

/// <summary>
/// A minimal unified-diff renderer over two line sequences, built for exactly one job: localizing
/// the bytes a hash comparison can only say "differ" about. No process dependency (no shelling out
/// to <c>git diff</c> or <c>diff</c>, which is not guaranteed to be on PATH in every environment this
/// runs in) and no external package — an LCS-based line diff over inputs this small (a fixture's
/// normalized stream is at most a few hundred lines) is milliseconds of work.
/// </summary>
internal static class UnifiedDiff
{
    /// <summary>Renders a unified-diff-style excerpt of <paramref name="expected"/> vs
    /// <paramref name="actual"/>, both split on <c>\n</c>. Returns "" when the two are identical.
    /// Hunks carry <paramref name="contextLines"/> of unchanged context on each side, exactly like
    /// <c>diff -u</c>; the changed lines themselves are never trimmed or shortened — only the number
    /// of hunks shown is capped (<paramref name="maxHunks"/>) so a pathologically scattered diff
    /// cannot produce an unbounded log. That cap bounds the surrounding excerpt, never a differing
    /// line's own content, and the first (and, in this repo's evidence, only) differing region is
    /// always among the hunks shown.</summary>
    public static string Render(
        string expected,
        string actual,
        int contextLines = 3,
        string expectedLabel = "reference (committed)",
        string actualLabel = "current (this run)",
        int maxHunks = 100)
    {
        var a = SplitLines(expected);
        var b = SplitLines(actual);
        var ops = Lcs(a, b);
        var hunks = BuildHunks(ops, contextLines);
        if (hunks.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("--- ").Append(expectedLabel).Append('\n');
        sb.Append("+++ ").Append(actualLabel).Append('\n');
        foreach (var hunk in hunks.Take(maxHunks))
            AppendHunk(sb, hunk, ops, a, b);
        if (hunks.Count > maxHunks)
            sb.Append($"... {hunks.Count - maxHunks} more differing region(s) omitted (not the region above) ...\n");
        return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Split('\n');

    private enum OpKind { Equal, Delete, Insert }

    private readonly record struct Op(OpKind Kind, int AIndex, int BIndex);

    /// <summary>Classic O(n·m) longest-common-subsequence line diff. Fine at this scale (a few
    /// hundred lines per fixture); not meant for arbitrary-sized inputs.</summary>
    private static List<Op> Lcs(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(new Op(OpKind.Equal, x, y));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(new Op(OpKind.Delete, x, -1));
                x++;
            }
            else
            {
                ops.Add(new Op(OpKind.Insert, -1, y));
                y++;
            }
        }
        while (x < n) { ops.Add(new Op(OpKind.Delete, x, -1)); x++; }
        while (y < m) { ops.Add(new Op(OpKind.Insert, -1, y)); y++; }
        return ops;
    }

    private readonly record struct Hunk(int Start, int End);

    /// <summary>Groups the op stream into runs that each contain at least one change, padded with
    /// up to <paramref name="contextLines"/> of surrounding equal lines and merged when two runs'
    /// padding would overlap — the same grouping <c>diff -u</c> does.</summary>
    private static List<Hunk> BuildHunks(List<Op> ops, int contextLines)
    {
        var changedIndices = ops
            .Select((op, index) => (op, index))
            .Where(entry => entry.op.Kind != OpKind.Equal)
            .Select(entry => entry.index)
            .ToArray();
        if (changedIndices.Length == 0) return [];

        var hunks = new List<Hunk>();
        var start = Math.Max(0, changedIndices[0] - contextLines);
        var end = Math.Min(ops.Count - 1, changedIndices[0] + contextLines);
        foreach (var changed in changedIndices.Skip(1))
        {
            var nextStart = Math.Max(0, changed - contextLines);
            if (nextStart <= end + 1)
            {
                end = Math.Min(ops.Count - 1, changed + contextLines);
            }
            else
            {
                hunks.Add(new Hunk(start, end));
                start = nextStart;
                end = Math.Min(ops.Count - 1, changed + contextLines);
            }
        }
        hunks.Add(new Hunk(start, end));
        return hunks;
    }

    private static void AppendHunk(StringBuilder sb, Hunk hunk, List<Op> ops, string[] a, string[] b)
    {
        // 1-based line numbers just before the hunk starts, found by walking the ops that precede
        // it — cheap at this scale and avoids threading counters through BuildHunks.
        var aLine = ops.Take(hunk.Start).Count(op => op.Kind != OpKind.Insert) + 1;
        var bLine = ops.Take(hunk.Start).Count(op => op.Kind != OpKind.Delete) + 1;
        sb.Append($"@@ -{aLine} +{bLine} @@\n");

        for (var i = hunk.Start; i <= hunk.End; i++)
        {
            var op = ops[i];
            switch (op.Kind)
            {
                case OpKind.Equal:
                    sb.Append(' ').Append(a[op.AIndex]).Append('\n');
                    break;
                case OpKind.Delete:
                    sb.Append('-').Append(a[op.AIndex]).Append('\n');
                    break;
                case OpKind.Insert:
                    sb.Append('+').Append(b[op.BIndex]).Append('\n');
                    break;
            }
        }
    }
}

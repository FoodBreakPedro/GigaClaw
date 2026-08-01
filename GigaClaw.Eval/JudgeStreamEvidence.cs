namespace GigaClaw.Eval;

/// <summary>
/// Line-level evidence for judge baseline drift. <c>evidence[].ref</c> and <c>inputDigest</c> are
/// both SHA-256 of <see cref="ReplayRunner.CanonicalStream"/> — a hash tells you THAT two runs
/// differ, never WHERE. This type is the "where": it commits that canonical string once per fixture
/// (generated on this repository's reference platform, macOS — see the type remarks on
/// <see cref="ReplayRunner.Normalize"/> for why a workspace-path leak was the first, now-disproven,
/// suspect for the Windows drift) and diffs a later run's stream against it.
///
/// <para>Deliberately NOT part of the judge verdict or its baseline: it does not affect
/// <c>inputDigest</c>, the digest algorithm, or normalization — it is read-side evidence only, so a
/// platform that reproduces the byte stream reproduces the SAME reference file this repository
/// already committed, and a platform that does not gets a line-level answer instead of a bare
/// "drift" status.</para>
/// </summary>
public static class JudgeStreamEvidence
{
    /// <summary>Mirrors the layout convention of <c>GigaClaw.Eval/baselines/judge/</c>: one
    /// subdirectory per baseline kind under <c>GigaClaw.Eval/baselines/</c>.</summary>
    public const string BaselineSubdirectory = "normalized-streams";

    public static string ReferencePath(string repositoryRoot, string fixtureId) =>
        Path.Combine(repositoryRoot, "GigaClaw.Eval", "baselines", BaselineSubdirectory, fixtureId + ".txt");

    /// <summary>Writes the committed reference for one fixture. The file's bytes are exactly
    /// <paramref name="canonicalStream"/> (see <see cref="ReplayRunner.CanonicalStream"/>) plus a
    /// single trailing newline — the newline is a text-file nicety for reviewers and git, and is
    /// stripped again before any comparison, so it never participates in the diff.</summary>
    public static void WriteReference(string repositoryRoot, string fixtureId, string canonicalStream)
    {
        var path = ReferencePath(repositoryRoot, fixtureId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, canonicalStream + "\n");
    }

    /// <summary>Renders drift evidence for one fixture: whether a committed reference exists, and
    /// when it does, a unified-diff-style excerpt of the current normalized stream against it. Never
    /// truncates the differing region (see <see cref="UnifiedDiff"/>); this is the whole point of the
    /// exercise, and CI's log is the only artifact a Windows failure leaves behind, so it must be
    /// self-sufficient without a Windows machine to reproduce on.</summary>
    public static string Describe(string repositoryRoot, string fixtureId, string currentCanonicalStream)
    {
        var path = ReferencePath(repositoryRoot, fixtureId);
        if (!File.Exists(path))
        {
            return $"No committed normalized-stream reference at {path}. Current stream " +
                   $"({currentCanonicalStream.Split('\n').Length} line(s)):\n{currentCanonicalStream}";
        }

        var reference = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n');
        var current = currentCanonicalStream.Replace("\r\n", "\n").TrimEnd('\n');
        var diff = UnifiedDiff.Render(reference, current, expectedLabel: path, actualLabel: "current run");
        return diff.Length == 0
            ? $"The normalized stream byte-matches the committed reference at {path}. " +
              "The drift is not in the normalized stream — look at the digest/evidence computation itself."
            : diff;
    }
}

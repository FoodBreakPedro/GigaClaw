using GigaClaw.Core.Automation.Policy;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R4's glob-vs-glob intersection approximation. Exact intersection is undecidable in general, so
/// <see cref="GlobIntersection"/> compares literal prefixes (the text before the first wildcard)
/// and is deliberately over-inclusive past that point — these tests pin both the sound direction
/// (a real divergence before any wildcard is always disjoint) and the documented false-positive
/// class (patterns that only ever differ after a wildcard are treated as intersecting).
/// </summary>
public class GlobIntersectionTests
{
    [Theory]
    // One glob's directory scope fully contains the other's.
    [InlineData("src/**", "src/api/**", true)]
    // Literal prefixes diverge before either pattern reaches a wildcard: genuinely disjoint.
    [InlineData("src/**", "docs/**", false)]
    // Near-miss prefix that diverges exactly at the directory-boundary character — proves this is
    // a real string comparison, not a naive "StartsWith" that would wrongly treat "src" as a
    // prefix of "src-old" and call them related.
    [InlineData("src-old/**", "src/**", false)]
    // Fully literal patterns (no wildcard at all): only intersect if identical.
    [InlineData("a/b.txt", "a/c.txt", false)]
    [InlineData("a/b.txt", "a/b.txt", true)]
    // A literal file path is covered by a directory glob whose literal prefix it shares.
    [InlineData("src/foo.cs", "src/**", true)]
    // Documented false-positive class: these never share a concrete path (different extensions)
    // but are still declared to intersect because they only diverge *after* the wildcard.
    [InlineData("src/*.cs", "src/*.md", true)]
    // Negation marker is stripped before comparison, same as GitIgnoreGlobSet's normalization.
    [InlineData("!src/**", "src/**", true)]
    // Backslash separators normalize to '/' before comparison.
    [InlineData(@"src\api\**", "src/api/foo.cs", true)]
    public void MayIntersect_LiteralPrefixRule(string a, string b, bool expected)
    {
        Assert.Equal(expected, GlobIntersection.MayIntersect(a, b, PathCaseSensitivity.Sensitive));
        // The rule is inherently symmetric — assert it actually is, both directions.
        Assert.Equal(expected, GlobIntersection.MayIntersect(b, a, PathCaseSensitivity.Sensitive));
    }

    [Fact]
    public void MayIntersect_CaseSensitivity_controls_whether_different_case_counts_as_divergence()
    {
        Assert.False(GlobIntersection.MayIntersect("Src/**", "src/**", PathCaseSensitivity.Sensitive));
        Assert.True(GlobIntersection.MayIntersect("Src/**", "src/**", PathCaseSensitivity.Insensitive));
    }

    [Fact]
    public void MayIntersect_ScopeSets_intersect_if_any_pair_does()
    {
        IReadOnlyList<string> a = ["docs/**", "src/api/**"];
        IReadOnlyList<string> b = ["assets/**", "src/**"];
        Assert.True(GlobIntersection.MayIntersect(a, b, PathCaseSensitivity.Sensitive));
    }

    [Fact]
    public void MayIntersect_ScopeSets_disjoint_only_if_every_pair_is()
    {
        IReadOnlyList<string> a = ["docs/**"];
        IReadOnlyList<string> b = ["src/**"];
        Assert.False(GlobIntersection.MayIntersect(a, b, PathCaseSensitivity.Sensitive));
    }
}

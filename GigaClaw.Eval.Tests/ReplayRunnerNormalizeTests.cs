using GigaClaw.Core.Automation;

namespace GigaClaw.Eval.Tests;

/// <summary>
/// Reproduces, on any platform, the Windows-only defect diagnosed from CI run 30669812287:
/// <c>JudgeRunnerTests.Judge_MatchesTheCommittedBaselineForEveryFixture</c> drifted on 29/38
/// fixtures, and for every one of them only <c>evidence[].ref</c> and <c>inputDigest</c> moved —
/// verdict, summary, every category note, and the rubric hash were all identical. Those two fields
/// are the only ones derived from <see cref="ReplayRunner.Digest"/>, which hashes <em>every</em>
/// event, including the "launch" event ClaudeRunner synthesizes with
/// <c>cwd={ctx.WorkspacePath}</c> — the one event no rubric criterion ever reads. That is exactly
/// the failure shape a scrub that misses one representation of the workspace path produces: the
/// digest moves, nothing score-derived does.
///
/// <see cref="ReplayRunner.Normalize"/> and <see cref="ReplayRunner.Digest"/> are <c>internal</c>
/// (via <c>InternalsVisibleTo</c>) precisely so this can be exercised directly, with a synthetic
/// Windows-shaped workspace path, instead of requiring a Windows machine to prove.
/// </summary>
public sealed class ReplayRunnerNormalizeTests
{
    // A stand-in for Guid.NewGuid().ToString("N") — 32 lowercase hex characters, exactly the shape
    // ReplayRunner.Replay() generates for the leaf of every throwaway workspace directory.
    private const string RunHex = "0123456789abcdef0123456789abcdef";
    private const string Leaf = "gigaclaw-replay-" + RunHex;

    // The form ReplayRunner itself would have built via Path.Combine on a Windows box, and the form
    // the two exact-substring Replace() calls in Normalize already handled before this fix — the
    // control group: these must keep normalizing correctly (no regression).
    private const string CanonicalBackslash = @"C:\Users\runneradmin\AppData\Local\Temp\" + Leaf;
    private const string CanonicalForwardSlash =
        @"C:/Users/runneradmin/AppData/Local/Temp/" + Leaf;

    // The three forms the CI evidence implicates (doc/roadmap/SESSION-HANDOFF.md § "One test is
    // still exempted on Windows", and the task's own candidate list): none of these is an exact
    // substring of CanonicalBackslash/CanonicalForwardSlash, so the pre-fix Normalize left them
    // untouched in the normalized stream.
    private const string DoubledBackslash =
        @"C:\\Users\\runneradmin\\AppData\\Local\\Temp\\" + Leaf;
    private const string ShortAncestorSegment = @"C:\Users\RUNNER~1\AppData\Local\Temp\" + Leaf;
    private const string DifferentCase = @"c:\users\RUNNERADMIN\appdata\local\temp\" + Leaf;

    public static TheoryData<string, string> LeakedForms => new()
    {
        { "canonical backslash (control)", CanonicalBackslash },
        { "canonical forward-slash (control)", CanonicalForwardSlash },
        { "doubled backslash (JSON-escaped)", DoubledBackslash },
        { "8.3 short ancestor segment (RUNNER~1)", ShortAncestorSegment },
        { "differently-cased drive/ancestor", DifferentCase },
    };

    /// <summary>Every representation of the same workspace path must normalize to the identical
    /// event stream and digest — the same guarantee <c>Replay_IsDeterministic_...</c> asserts across
    /// two real runs, asserted here across every path FORM a single real run could have leaked.</summary>
    [Theory]
    [MemberData(nameof(LeakedForms))]
    public void Normalize_ScrubsEveryRepresentationOfTheWorkspacePath_ToTheIdenticalStream(
        string label,
        string leakedForm)
    {
        _ = label; // xUnit surfaces this in the test explorer; not asserted on directly.

        var events = BuildSyntheticStream(leakedForm);
        var normalized = ReplayRunner.Normalize(events, CanonicalBackslash);

        var expected = ReplayRunner.Normalize(
            BuildSyntheticStream(CanonicalBackslash), CanonicalBackslash);

        Assert.Equal(
            expected.Select(e => (e.Index, e.Kind, e.Text)),
            normalized.Select(e => (e.Index, e.Kind, e.Text)));
        Assert.Equal(ReplayRunner.Digest(expected), ReplayRunner.Digest(normalized));

        // Positive control: prove the marker actually was replaced, not merely that both streams
        // happened to match by both being untouched.
        Assert.Contains(normalized, e => e.Text.Contains("{{workspace}}", StringComparison.Ordinal));
        Assert.DoesNotContain(normalized, e => e.Text.Contains("runneradmin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(normalized, e => e.Text.Contains("RUNNER~1", StringComparison.Ordinal));
        Assert.DoesNotContain(normalized, e => e.Text.Contains(RunHex, StringComparison.Ordinal));
    }

    /// <summary>The mechanism itself, isolated from the rest of Normalize: builds the synthetic
    /// "launch" event exactly as ClaudeRunner.SpawnAndWaitAsync does
    /// (<c>cwd={ctx.ExecutionPath ?? ctx.WorkspacePath}</c>) and confirms the digest moves if and
    /// only if the scrub fails to catch the leaked form — the same shape as the CI evidence
    /// (evidence[].ref / inputDigest move, nothing else).</summary>
    [Fact]
    public void LaunchEventIsTheOnlyEventThatCarriesTheWorkspacePath_AndScrubsCleanly()
    {
        var canonical = ReplayRunner.Normalize(
            BuildSyntheticStream(CanonicalBackslash), CanonicalBackslash);
        var leaked = ReplayRunner.Normalize(
            BuildSyntheticStream(ShortAncestorSegment), CanonicalBackslash);

        // Every OTHER event (assistant, tool_use) is untouched by the leak and must stay identical,
        // matching the CI evidence that categories/notes never moved.
        Assert.Equal(
            canonical.Where(e => e.Kind != "launch").Select(e => e.Text),
            leaked.Where(e => e.Kind != "launch").Select(e => e.Text));

        var canonicalLaunch = canonical.Single(e => e.Kind == "launch").Text;
        var leakedLaunch = leaked.Single(e => e.Kind == "launch").Text;
        Assert.Equal(canonicalLaunch, leakedLaunch);
        Assert.Equal("programmer (new) session={{session_id}} cwd={{workspace}} skill=programmer/SKILL.md", canonicalLaunch);
    }

    private static StreamEvent[] BuildSyntheticStream(string workspaceForm) =>
    [
        // Mirrors ClaudeRunner.SpawnAndWaitAsync's literal interpolation exactly, with the
        // caller-supplied workspaceForm standing in for whatever representation actually leaked.
        new(DateTime.UtcNow, "launch",
            $"programmer (new) session=abcd1234 cwd={workspaceForm} skill=programmer/SKILL.md"),
        new(DateTime.UtcNow, "assistant", "Fixed the login timeout. Ship it."),
        new(DateTime.UtcNow, "tool_use", "Edit", "{\"file_path\":\"src/login.cs\"}"),
    ];
}

using System.Globalization;

namespace GigaClaw.Eval;

/// <summary>
/// The default judge: a pure function from (rubric, replayed stream) to a v1 verdict.
///
/// Every input it reads is already deterministic — the replay layer scrubbed the workspace path,
/// the session id, timings and costs before handing the stream over — and every rule below is a
/// mechanical predicate over that stream. Nothing here reads the clock, the filesystem, the
/// environment or a model. Two runs of the same fixture therefore produce byte-identical verdicts,
/// which is what makes a committed baseline meaningful.
///
/// The one field the contract requires and a pure function cannot honestly supply is
/// <c>reviewedAtUtc</c>: there is no "when" for a function of its arguments. It is stamped with
/// <see cref="DeterministicReviewInstant"/> and the verdict is bound to its input by
/// <c>inputDigest</c> instead. The LLM judge, which really does run at a point in time, stamps the
/// real instant.
/// </summary>
public static class RubricJudge
{
    /// <summary>The deterministic judge's fixed review instant. See the type remarks: a pure
    /// function has no review time, and a wall clock would destroy byte-reproducibility.</summary>
    public const string DeterministicReviewInstant = "1970-01-01T00:00:00Z";

    public const string Mode = "deterministic";

    /// <summary>Text predicates compare case-insensitively: a rubric asserts that a property was
    /// reported, not that it was capitalized a particular way.</summary>
    private const StringComparison TextComparison = StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<string> CheckKinds { get; } =
    [
        "replay-expectations",
        "no-error-events",
        "event-kinds-present",
        "final-text-contains-all",
        "final-text-contains-any",
        "final-text-omits-all",
        "final-text-min-length",
        "tool-use-at-most",
    ];

    /// <summary>Scores one replayed fixture. <paramref name="rubricDigest"/> is cited as evidence so
    /// a rubric edit is visible in the verdict it produced.</summary>
    public static JudgeVerdict Score(
        ReplayFixture fixture,
        ReplayFixtureResult replay,
        AgentRubric rubric,
        string rubricDigest,
        double shipThresholdPercent)
    {
        var inputDigest = "sha256:" + replay.StreamDigest;
        var streamRef = inputDigest;
        var evidence = new List<JudgeEvidence>
        {
            new("hash", streamRef,
                $"normalized replay stream for fixture '{fixture.Id}' ({replay.Events.Count} event(s), exit {replay.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"),
            new("hash", rubricDigest, $"rubric '{rubric.Agent}' v{rubric.Version}"),
        };

        var categories = new List<JudgeCategory>();
        var vetoItems = new List<JudgeVetoItem>();
        foreach (var criterion in rubric.Criteria)
        {
            var (score, notes) = Evaluate(criterion, replay);
            categories.Add(new JudgeCategory(criterion.Name, score, criterion.Max, notes));
            if (score <= 0 && !string.IsNullOrWhiteSpace(criterion.Veto))
            {
                vetoItems.Add(new JudgeVetoItem(
                    criterion.Veto,
                    criterion.Statement ?? $"{criterion.Name}: {notes}",
                    [streamRef]));
            }
        }

        var earned = Round(categories.Sum(category => category.Score));
        var available = Round(categories.Sum(category => category.Max));
        var percent = available > 0 ? Math.Round(earned / available * 100, 1, MidpointRounding.AwayFromZero) : 0;

        var decision = vetoItems.Count > 0
            ? "BLOCK"
            : percent >= shipThresholdPercent
                ? "SHIP"
                : "FIX";

        return new JudgeVerdict(
            SchemaVersion: 1,
            Agent: fixture.Agent,
            TicketId: fixture.Ticket.Id,
            Verdict: decision,
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"Deterministic rubric judge scored fixture '{fixture.Id}' at {earned}/{available} ({percent}%)."),
            Categories: categories,
            VetoItems: vetoItems,
            // Deliberately no `path` evidence: the judged artifact is a captured stream, not a
            // workspace file. doc/verdict-contract.md calls this case out — citing an unrelated
            // file would make every such verdict read as STALE to `requireFreshArtifact`.
            Evidence: evidence,
            ReviewedAtUtc: DeterministicReviewInstant,
            InputDigest: inputDigest);
    }

    private static (double Score, string Notes) Evaluate(RubricCriterion criterion, ReplayFixtureResult replay)
    {
        var values = criterion.Values ?? [];
        var finalText = FinalAssistantText(replay);

        switch (criterion.Check)
        {
            case "replay-expectations":
            {
                var failed = replay.Checks.Where(check => check.Status != "pass").ToArray();
                return failed.Length == 0
                    ? (criterion.Max, $"All {replay.Checks.Count} fixture expectation(s) held.")
                    : (0, $"Unmet fixture expectation(s): {string.Join(", ", failed.Select(check => check.Id))}.");
            }

            case "no-error-events":
            {
                var errors = replay.Events.Count(captured =>
                    string.Equals(captured.Kind, "error", StringComparison.Ordinal));
                return errors == 0
                    ? (criterion.Max, "The stream carries no error event.")
                    : (0, $"The stream carries {errors} error event(s).");
            }

            case "event-kinds-present":
            {
                var kinds = replay.Events.Select(captured => captured.Kind).ToHashSet(StringComparer.Ordinal);
                var missing = values.Where(kind => !kinds.Contains(kind)).ToArray();
                return (
                    Portion(criterion.Max, values.Count - missing.Length, values.Count),
                    missing.Length == 0
                        ? $"All {values.Count} required event kind(s) appear."
                        : $"Missing event kind(s): {string.Join(", ", missing)}.");
            }

            case "final-text-contains-all":
            {
                var missing = values.Where(value => !finalText.Contains(value, TextComparison)).ToArray();
                return (
                    Portion(criterion.Max, values.Count - missing.Length, values.Count),
                    missing.Length == 0
                        ? $"All {values.Count} required marker(s) present."
                        : $"Absent marker(s): {string.Join(", ", missing)}.");
            }

            case "final-text-contains-any":
            {
                var hit = values.FirstOrDefault(value => finalText.Contains(value, TextComparison));
                return hit is null
                    ? (0, $"None of {values.Count} acceptable marker(s) present: {string.Join(", ", values)}.")
                    : (criterion.Max, $"Marker '{hit}' present.");
            }

            case "final-text-omits-all":
            {
                var hits = values.Where(value => finalText.Contains(value, TextComparison)).ToArray();
                return (
                    Portion(criterion.Max, values.Count - hits.Length, values.Count),
                    hits.Length == 0
                        ? $"None of {values.Count} forbidden phrase(s) appear."
                        : $"Forbidden phrase(s) present: {string.Join(", ", hits)}.");
            }

            case "final-text-min-length":
            {
                var length = finalText.Length;
                return (
                    Portion(criterion.Max, Math.Min(length, criterion.Threshold), criterion.Threshold),
                    $"Final message is {length} character(s) against a floor of {criterion.Threshold}.");
            }

            case "tool-use-at-most":
            {
                var used = replay.Events.Count(captured =>
                    string.Equals(captured.Kind, "tool_use", StringComparison.Ordinal));
                return used <= criterion.Threshold
                    ? (criterion.Max, $"{used} tool call(s) against a ceiling of {criterion.Threshold}.")
                    : (0, $"{used} tool call(s) exceeds the ceiling of {criterion.Threshold}.");
            }

            default:
                throw new InvalidDataException(
                    $"Rubric criterion '{criterion.Name}' uses unknown check '{criterion.Check}'. " +
                    $"Known checks: {string.Join(", ", CheckKinds)}.");
        }
    }

    internal static string FinalAssistantText(ReplayFixtureResult replay) =>
        replay.Events
            .LastOrDefault(captured => string.Equals(captured.Kind, "assistant", StringComparison.Ordinal))
            ?.Text ?? "";

    /// <summary>Partial credit, rounded so the written JSON is stable across platforms.</summary>
    private static double Portion(double max, int satisfied, int total) =>
        total <= 0 ? max : Round(max * satisfied / total);

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static double Percent(JudgeVerdict verdict)
    {
        var available = verdict.Categories.Sum(category => category.Max);
        if (available <= 0) return 0;
        return Math.Round(
            verdict.Categories.Sum(category => category.Score) / available * 100,
            1,
            MidpointRounding.AwayFromZero);
    }
}

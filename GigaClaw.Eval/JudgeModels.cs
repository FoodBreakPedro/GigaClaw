using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaClaw.Eval;

/// <summary>Optional, versioned knobs for the judge layer. Absent in an evalconfig.json written
/// before the judge existed, in which case <see cref="Default"/> applies.</summary>
public sealed record JudgeConfig(
    string RubricRoot,
    string ArtifactSubdirectory,
    string BaselineSubdirectory,
    double ShipThresholdPercent,
    double LlmTolerancePercent)
{
    public static JudgeConfig Default { get; } =
        new("GigaClaw.Eval/rubrics", "judge", "judge", 90, 10);
}

/// <summary>
/// One scored line of an agent's rubric. <see cref="Check"/> names a deterministic predicate over
/// the replayed stream — see <see cref="RubricJudge"/> for the vocabulary. Anything a judge cannot
/// compute from the captured stream alone has no place here: the rubric is what makes the
/// deterministic judge reproducible.
/// </summary>
public sealed record RubricCriterion(
    string Name,
    string Check,
    double Max,
    IReadOnlyList<string>? Values = null,
    int Threshold = 0,
    string? Veto = null,
    string? Statement = null);

/// <summary>An agent's committed rubric. <c>rubrics/&lt;agent&gt;.json</c>, falling back to
/// <c>rubrics/default.json</c> for agents that have not been given one yet.</summary>
public sealed record AgentRubric(
    int Version,
    string Agent,
    string Description,
    IReadOnlyList<RubricCriterion> Criteria);

// ---------------------------------------------------------------------------
// The verdict itself. These are the ONLY records in the eval project serialized
// with the contract's own camelCase names: a judge verdict must be byte-shaped
// like a reviewer verdict, because doc/verdict-contract.md says they are the
// same object. Everything wrapping them keeps the eval project's PascalCase.
// ---------------------------------------------------------------------------

public sealed record JudgeCategory(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record JudgeVetoItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("evidenceRefs")] IReadOnlyList<string> EvidenceRefs);

public sealed record JudgeEvidence(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>A v1 verdict as the judge emits it. Property order here is the order it is written in,
/// and it is validated against the frozen contract before it reaches disk.</summary>
public sealed record JudgeVerdict(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("ticketId")] int TicketId,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("categories")] IReadOnlyList<JudgeCategory> Categories,
    [property: JsonPropertyName("vetoItems")] IReadOnlyList<JudgeVetoItem> VetoItems,
    [property: JsonPropertyName("evidence")] IReadOnlyList<JudgeEvidence> Evidence,
    [property: JsonPropertyName("reviewedAtUtc")] string ReviewedAtUtc,
    [property: JsonPropertyName("inputDigest")] string InputDigest);

/// <summary>
/// What an LLM judge ran as. Recorded next to — never inside — the verdict, because the verdict
/// schema is frozen and because this is exactly the metadata that explains why two LLM verdicts
/// on the same input differ. The deterministic judge has no such record: it is the rubric.
/// </summary>
public sealed record JudgeModelRecord(
    string Cli,
    string CliVersion,
    string RequestedModel,
    string ReportedModel,
    int MaxTurns,
    string PromptDigest);

/// <summary>
/// How far the LLM judge landed from the deterministic one. Reported, never asserted: an LLM
/// verdict outside tolerance is a signal for a human, not a build failure.
/// </summary>
public sealed record JudgeTolerance(
    double DeterministicPercent,
    double LlmPercent,
    double DeltaPercent,
    double TolerancePercent,
    bool WithinTolerance,
    bool DecisionAgrees);

public sealed record JudgeFixtureResult(
    string Fixture,
    string Family,
    string Agent,
    string Mode,
    string Rubric,
    string Status,
    string BaselineStatus,
    JudgeVerdict? Verdict,
    JudgeModelRecord? Model,
    JudgeTolerance? Tolerance,
    IReadOnlyList<EvalCheckResult> Checks);

public sealed record JudgeReport(
    int Version,
    string Target,
    string Agent,
    IReadOnlyList<JudgeFixtureResult> Fixtures);

/// <summary>One recorded verdict per fixture. Committed under the eval project; the ephemeral
/// per-run report goes to the gitignored artifact directory instead.</summary>
public sealed record JudgeBaselineEntry(string Fixture, JudgeVerdict Verdict);

public sealed record JudgeBaseline(
    int Version,
    string Agent,
    string Judge,
    IReadOnlyList<JudgeBaselineEntry> Fixtures);

public sealed record JudgeRunResult(
    IReadOnlyList<JudgeReport> Reports,
    int ExitCode,
    long ElapsedMilliseconds);

internal static class JudgeJson
{
    /// <summary>Null-omitting: the verdict schema types <c>summary</c> and <c>notes</c> as strings,
    /// so a written <c>null</c> would be a contract violation rather than an absent field.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Committed baselines are read in review. Escaping every apostrophe to ' would make a
        // score change hard to see; the encoded values are eval-authored text written to a JSON
        // file, never interpolated into a document.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Could not parse {path}.");

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options) + "\n";

    /// <summary>Compact form used for equality and for embedding in a prompt.</summary>
    public static string Canonical<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = false });
}

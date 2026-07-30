namespace GigaClaw.Eval;

/// <summary>Optional, versioned knobs for the replay layer. Absent in older evalconfig.json files,
/// in which case <see cref="Default"/> applies.</summary>
public sealed record ReplayConfig(
    string FixtureRoot,
    string ArtifactSubdirectory,
    int TimeoutSeconds)
{
    public static ReplayConfig Default { get; } = new("GigaClaw.Eval/fixtures", "replay", 120);
}

public sealed record ReplayTicketComment(string Author, string Body);

/// <summary>The canned ticket a fixture replays against. It is rendered into the hermetic
/// workspace so the dispatched agent reads exactly these bytes instead of calling the REST API.</summary>
public sealed record ReplayTicket(
    int Id,
    string Title,
    string Status,
    string Assignee,
    string Description,
    IReadOnlyList<ReplayTicketComment> Comments);

/// <summary>Assertions a fixture makes about the captured run. Deliberately mechanical:
/// judging the *quality* of the reply belongs to the judge layer.</summary>
public sealed record ReplayExpectation(
    int ExitCode,
    string RunStatus,
    IReadOnlyList<string> EventKinds,
    string FinalTextContains);

public sealed record ReplayFixture(
    int Version,
    string Id,
    string Family,
    string Agent,
    string Scenario,
    int MaxTurns,
    ReplayTicket Ticket,
    ReplayExpectation Expect);

/// <summary>A captured stream event with every volatile field scrubbed. Index + Kind + Text is
/// the whole observable surface a later judge slice will read.</summary>
public sealed record ReplayEvent(int Index, string Kind, string Text);

/// <summary>
/// What one dispatch consumed. Deliberately <b>not</b> part of <see cref="ReplayFixtureResult"/>:
/// cost and token counts vary between runs of the same fixture, and recording them in the replay
/// report would destroy the byte-reproducibility the replay and judge layers are built on. Only the
/// Monte Carlo layer, which exists to measure variation and to cap spend, reads them.
/// <see cref="CostReported"/> distinguishes "the run cost nothing" from "no cost was reported".
/// </summary>
public sealed record ReplayUsage(decimal CostUsd, long TotalTokens, bool CostReported);

public sealed record ReplayFixtureResult(
    string Fixture,
    string Family,
    string Agent,
    string Scenario,
    string Status,
    int? ExitCode,
    string RunStatus,
    string StreamDigest,
    IReadOnlyList<ReplayEvent> Events,
    IReadOnlyList<EvalCheckResult> Checks);

public sealed record ReplayReport(
    int Version,
    string Mode,
    string Target,
    string Agent,
    IReadOnlyList<ReplayFixtureResult> Fixtures);

public sealed record ReplayRunResult(
    IReadOnlyList<ReplayReport> Reports,
    int ExitCode,
    long ElapsedMilliseconds);

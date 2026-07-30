namespace GigaClaw.Eval;

/// <summary>Optional, versioned knobs for the Monte Carlo layer. Absent in an evalconfig.json
/// written before it existed, in which case <see cref="Default"/> applies. The two ceilings are
/// defaults, not suggestions: a run that would push past either is never started.</summary>
public sealed record MonteCarloConfig(
    string ArtifactSubdirectory,
    int DefaultRuns,
    int MaxRuns,
    decimal MaxSpendUsd,
    int MinimumSampleForInterval)
{
    public static MonteCarloConfig Default { get; } = new("montecarlo", 5, 20, 5.00m, 5);
}

/// <summary>One dispatch the Monte Carlo layer asks for. The seam exists so a test can count
/// dispatches and hand back known costs: proving a cap stopped a run <em>before</em> it started
/// means proving the dispatch never happened, which is only observable from the dispatcher.</summary>
public sealed record MonteCarloRequest(ReplayFixture Fixture, int Run, bool RealCli);

/// <summary>One observation. <see cref="StreamDigest"/> is what makes "did anything actually vary?"
/// answerable: on a deterministic pipeline every sample carries the same digest.</summary>
public sealed record MonteCarloSample(
    int Run,
    string StreamDigest,
    string Decision,
    double Percent,
    decimal CostUsd,
    bool CostReported,
    long TotalTokens,
    string? Error);

public delegate MonteCarloSample MonteCarloDispatch(MonteCarloRequest request);

/// <summary>
/// Where variance could have come from, stated before any number is reported. A pipeline with no
/// stochastic step has no sampling distribution, and N runs of it are one observation repeated N
/// times — not a sample of size N. The report says which of the two it is.
/// </summary>
public sealed record MonteCarloSampling(
    bool VarianceIsPossible,
    string Source,
    string Note);

/// <summary>
/// What the sample actually supports. Every figure is reported next to its sample size, and
/// <see cref="IntervalMethod"/> is <c>none</c> — with <see cref="IntervalNote"/> saying why —
/// whenever the sample cannot honestly carry an interval.
/// </summary>
public sealed record MonteCarloStatistics(
    int SampleSize,
    double Mean,
    double Minimum,
    double Maximum,
    double Range,
    double StandardDeviation,
    int DistinctDecisions,
    int DistinctStreams,
    string IntervalMethod,
    double? IntervalLow,
    double? IntervalHigh,
    string IntervalNote);

/// <summary>
/// Money, and the ceiling it was measured against. <see cref="Basis"/> distinguishes dollars the
/// real CLI reported from the canned figure a mock scenario replays — the cap mechanism is the
/// same, but only one of them is real money.
/// </summary>
public sealed record MonteCarloCost(
    string Basis,
    decimal TotalUsd,
    decimal MeanUsd,
    decimal MaxRunUsd,
    decimal MaxSpendUsd,
    decimal ProjectedNextRunUsd,
    string CapStatus,
    string CapNote);

public sealed record MonteCarloFixtureResult(
    string Fixture,
    string Family,
    string Agent,
    string Rubric,
    string Status,
    int RequestedRuns,
    int DispatchedRuns,
    MonteCarloSampling Sampling,
    MonteCarloStatistics Statistics,
    MonteCarloCost Cost,
    IReadOnlyList<MonteCarloSample> Runs,
    IReadOnlyList<EvalCheckResult> Checks);

public sealed record MonteCarloReport(
    int Version,
    string Mode,
    string Target,
    string Agent,
    IReadOnlyList<MonteCarloFixtureResult> Fixtures);

public sealed record MonteCarloRunResult(
    IReadOnlyList<MonteCarloReport> Reports,
    int ExitCode,
    long ElapsedMilliseconds);

/// <summary>Command-line request, validated before anything is dispatched.</summary>
public sealed record MonteCarloOptions(int Runs, int MaxRuns, decimal MaxSpendUsd, bool RealCli);

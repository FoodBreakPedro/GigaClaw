using System.Diagnostics;
using System.Globalization;

namespace GigaClaw.Eval;

/// <summary>
/// Monte Carlo mode: N runs of a fixture, summarized honestly and stopped by a hard cost cap.
///
/// The uncomfortable fact this layer is built around is that <b>most of the pipeline has no
/// variance to sample</b>. Mock replay reads a committed NDJSON scenario, and the deterministic
/// <see cref="RubricJudge"/> is a pure function of (rubric, stream). Running that combination N
/// times does not produce a sample of size N; it produces one observation repeated N times. So:
///
/// * the sampled quantity is always the <b>agent run</b>, scored by the fixed deterministic judge.
///   Using the non-reproducible LLM judge here would put variance in the measuring instrument as
///   well as the sample, which is why <c>--llm</c> is refused rather than quietly accepted;
/// * on the mock path the report says the distribution is <b>degenerate</b> and refuses to print an
///   interval, and the N runs are instead used for what they can honestly prove — that the pipeline
///   really is deterministic N ways, which is an integrity error if it is not;
/// * on the <c>--real-cli</c> path the variance is real, and the statistics below apply.
///
/// Caps are checked <b>before</b> each dispatch, never after, using the worst per-run cost observed
/// so far as the estimate for the next one. A run that would breach a ceiling is never started.
/// </summary>
public sealed class MonteCarloRunner
{
    private const string Pass = "pass";
    private const string Warning = "warning";
    private const string Error = "error";
    private const string Integrity = "integrity";
    private const string Policy = "policy";

    private const string NoInterval = "none";
    private const string StudentT95 = "student-t-95";

    private const string MockBasis = "canned-by-mock";
    private const string RealBasis = "reported-by-real-cli";

    private readonly string _repositoryRoot;
    private readonly EvalConfig _config;
    private readonly MonteCarloConfig _monteCarlo;
    private readonly ReplayRunner _replay;
    private readonly JudgeRunner _judge;
    private readonly MonteCarloDispatch _dispatch;

    public MonteCarloRunner(string repositoryRoot, MonteCarloDispatch? dispatch = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _config = EvalJson.Read<EvalConfig>(
            Path.Combine(_repositoryRoot, "GigaClaw.Eval", "evalconfig.json"));
        _monteCarlo = _config.MonteCarlo ?? MonteCarloConfig.Default;
        ValidateConfig(_monteCarlo);
        _replay = new ReplayRunner(_repositoryRoot);
        _judge = new JudgeRunner(_repositoryRoot);
        _dispatch = dispatch ?? DispatchReplayAndScore;
    }

    /// <summary>Defaults every unspecified ceiling from the committed config, then refuses anything
    /// nonsensical up front — a cap that is only checked once the runs have started is not a cap.</summary>
    public MonteCarloOptions DefaultOptions(int? runs = null, int? maxRuns = null, decimal? maxSpendUsd = null, bool realCli = false)
    {
        var options = new MonteCarloOptions(
            runs ?? _monteCarlo.DefaultRuns,
            maxRuns ?? _monteCarlo.MaxRuns,
            maxSpendUsd ?? _monteCarlo.MaxSpendUsd,
            realCli);
        if (options.Runs <= 0)
            throw new ArgumentException("--runs must be at least 1.");
        if (options.MaxRuns <= 0)
            throw new ArgumentException("--max-runs must be at least 1.");
        if (options.MaxSpendUsd < 0)
            throw new ArgumentException("--max-spend-usd must not be negative.");
        return options;
    }

    /// <param name="target">A fixture id, a pipeline family, a catalog agent slug, or "all".</param>
    public MonteCarloRunResult Run(string target, MonteCarloOptions options, bool writeReport = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var fixtures = _replay.ResolveTarget(target);

        var results = fixtures.Select(fixture => Sample(fixture, options)).ToArray();

        var reports = results
            .GroupBy(result => result.Agent, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new MonteCarloReport(
                Version: 1,
                Mode: (options.RealCli ? "real-cli" : "mock") + "+" + RubricJudge.Mode,
                Target: target,
                Agent: group.Key,
                Fixtures: group.OrderBy(result => result.Fixture, StringComparer.Ordinal).ToArray()))
            .ToArray();

        if (writeReport)
            foreach (var report in reports)
                WriteReport(report);
        stopwatch.Stop();

        var failed = reports.SelectMany(report => report.Fixtures).Any(result => result.Status != Pass);
        return new MonteCarloRunResult(reports, failed ? 1 : 0, stopwatch.ElapsedMilliseconds);
    }

    // ---------------------------------------------------------------------
    // Sampling
    // ---------------------------------------------------------------------

    private MonteCarloFixtureResult Sample(ReplayFixture fixture, MonteCarloOptions options)
    {
        var checks = new List<EvalCheckResult>();
        var (_, rubricSource) = _judge.LoadRubric(fixture.Agent);

        var requested = options.Runs;
        if (options.MaxRuns < requested)
        {
            checks.Add(new EvalCheckResult(
                "montecarlo.maxruns", Policy, Warning,
                Invariant($"--max-runs {options.MaxRuns} clamps the requested {requested} run(s); {requested - options.MaxRuns} run(s) will not be dispatched.")));
            requested = options.MaxRuns;
        }

        var samples = new List<MonteCarloSample>();
        var spent = 0m;
        var worstRun = 0m;
        var capStatus = "not-reached";
        var capNote = Invariant($"Ceiling ${options.MaxSpendUsd} was never approached ({requested} run(s) requested).");

        for (var run = 1; run <= requested; run++)
        {
            // Cap check BEFORE dispatch. With no observation yet the next run's cost is unknown, so
            // the only honest pre-flight test is whether the ceiling permits spending anything at
            // all; from the second run on, the worst run observed so far is the estimate.
            var projected = samples.Count == 0 ? 0m : worstRun;
            if (samples.Count == 0 && options.MaxSpendUsd <= 0m)
            {
                capStatus = "spend";
                capNote = "The spend ceiling is $0, so no run was dispatched: the first run's cost cannot be known before it is spent.";
                break;
            }
            if (samples.Count > 0 && spent + projected > options.MaxSpendUsd)
            {
                capStatus = "spend";
                capNote = Invariant(
                    $"Run {run} was not dispatched: ${spent} already spent plus an estimated ${projected} (the worst run so far) would exceed the ${options.MaxSpendUsd} ceiling.");
                break;
            }

            var sample = _dispatch(new MonteCarloRequest(fixture, run, options.RealCli));
            samples.Add(sample);
            spent += sample.CostUsd;
            worstRun = Math.Max(worstRun, sample.CostUsd);
        }

        if (samples.Count == requested && requested < options.Runs)
        {
            capStatus = "runs";
            capNote = Invariant($"Stopped at the --max-runs ceiling of {options.MaxRuns}; ${spent} spent.");
        }

        var sampling = Describe(options.RealCli);
        var statistics = Summarize(samples, sampling.VarianceIsPossible);
        var cost = new MonteCarloCost(
            Basis: options.RealCli ? RealBasis : MockBasis,
            TotalUsd: Round4(spent),
            MeanUsd: samples.Count == 0 ? 0m : Round4(spent / samples.Count),
            MaxRunUsd: Round4(worstRun),
            MaxSpendUsd: options.MaxSpendUsd,
            ProjectedNextRunUsd: Round4(worstRun),
            CapStatus: capStatus,
            CapNote: capNote);

        checks.Add(CostCheck(cost, samples.Count, requested));
        checks.Add(new EvalCheckResult("montecarlo.sampling", Policy, sampling.VarianceIsPossible ? Pass : Warning, sampling.Note));
        checks.Add(new EvalCheckResult("montecarlo.statistics", Policy, Pass, statistics.IntervalNote));

        var failedRuns = samples.Where(sample => sample.Error is not null).ToArray();
        checks.Add(failedRuns.Length == 0
            ? new EvalCheckResult("montecarlo.sample", Integrity, Pass,
                Invariant($"All {samples.Count} dispatched run(s) produced a scoreable verdict."))
            : new EvalCheckResult("montecarlo.sample", Integrity, Error,
                Invariant($"{failedRuns.Length} of {samples.Count} run(s) produced no scoreable verdict: ") +
                string.Join("; ", failedRuns.Select(sample => $"run {sample.Run}: {sample.Error}"))));

        if (samples.Count == 0)
        {
            checks.Add(new EvalCheckResult(
                "montecarlo.dispatch", Integrity, Error,
                "No run was dispatched, so nothing was measured. Raise --max-runs or --max-spend-usd."));
        }
        else if (!sampling.VarianceIsPossible && statistics.DistinctStreams > 1)
        {
            // The one thing N runs of a deterministic pipeline can honestly prove.
            checks.Add(new EvalCheckResult(
                "montecarlo.determinism", Integrity, Error,
                Invariant($"The mock pipeline is supposed to be deterministic, but {samples.Count} run(s) ") +
                Invariant($"produced {statistics.DistinctStreams} distinct stream(s) and {statistics.DistinctDecisions} distinct decision(s).")));
        }
        else if (!sampling.VarianceIsPossible)
        {
            checks.Add(new EvalCheckResult(
                "montecarlo.determinism", Integrity, Pass,
                Invariant($"All {samples.Count} run(s) produced one identical stream and one identical verdict.")));
        }

        return new MonteCarloFixtureResult(
            fixture.Id,
            fixture.Family,
            fixture.Agent,
            rubricSource,
            checks.Any(check => check.Category == Integrity && check.Status == Error) ? Error : Pass,
            options.Runs,
            samples.Count,
            sampling,
            statistics,
            cost,
            samples,
            checks);
    }

    private static MonteCarloSampling Describe(bool realCli) => realCli
        ? new MonteCarloSampling(
            VarianceIsPossible: true,
            Source: "real-cli-agent-run",
            Note: "Variance is possible: each run dispatches the real CLI, so the stream being scored " +
                  "is redrawn every time. The judge is held fixed (the deterministic rubric), so the " +
                  "spread below is the agent's, not the instrument's.")
        : new MonteCarloSampling(
            VarianceIsPossible: false,
            Source: "none",
            Note: "Degenerate: mock replay reads a committed scenario and the deterministic judge is a " +
                  "pure function of it, so this pipeline has no sampling distribution. N runs here are " +
                  "one observation repeated N times, not a sample of size N. They are reported as zero " +
                  "variance and used as an N-way determinism check instead. Add --real-cli to sample " +
                  "something that can actually vary.");

    private EvalCheckResult CostCheck(MonteCarloCost cost, int dispatched, int requested)
    {
        var basis = cost.Basis == MockBasis
            ? "canned scenario dollars, not real money"
            : "dollars reported by the CLI";
        var message = Invariant(
            $"{dispatched} of {requested} run(s) dispatched; ${cost.TotalUsd} total, ${cost.MeanUsd} mean per run (n={dispatched}), ") +
            Invariant($"worst run ${cost.MaxRunUsd}, ceiling ${cost.MaxSpendUsd} [{basis}]. ") + cost.CapNote;
        return new EvalCheckResult("montecarlo.cost", Policy, cost.CapStatus == "not-reached" ? Pass : Warning, message);
    }

    // ---------------------------------------------------------------------
    // Statistics
    // ---------------------------------------------------------------------

    /// <summary>
    /// Summarizes the sample and — the point of this method — declines to compute an interval the
    /// sample cannot support. Three separate refusals, each with its own stated reason: nothing was
    /// sampled, a single observation, a sample with exactly zero spread, and a sample below the
    /// configured minimum size. Only past all four is a Student-t interval reported, named.
    /// </summary>
    internal MonteCarloStatistics Summarize(IReadOnlyList<MonteCarloSample> samples, bool varianceIsPossible)
    {
        var n = samples.Count;
        if (n == 0)
        {
            return new MonteCarloStatistics(
                0, 0, 0, 0, 0, 0, 0, 0, NoInterval, null, null,
                "n=0: no run was dispatched, so there is nothing to summarize.");
        }

        var values = samples.Select(sample => sample.Percent).ToArray();
        var mean = Round1(values.Average());
        var minimum = Round1(values.Min());
        var maximum = Round1(values.Max());
        var decisions = samples.Select(sample => sample.Decision).Distinct(StringComparer.Ordinal).Count();
        var streams = samples.Select(sample => sample.StreamDigest).Distinct(StringComparer.Ordinal).Count();

        // Bessel-corrected: the sample standard deviation, not the population one. n=1 has none.
        var deviation = n < 2
            ? 0
            : Round2(Math.Sqrt(values.Sum(value => Math.Pow(value - values.Average(), 2)) / (n - 1)));

        var what = Invariant($"mean {mean}%, range {minimum}–{maximum}%, sample sd {deviation} (n={n})");

        string note;
        if (n == 1)
        {
            note = Invariant($"n=1: one observation has no spread and supports no interval. What this run shows: {mean}%.");
        }
        else if (deviation == 0)
        {
            note = varianceIsPossible
                ? Invariant($"n={n}: every run scored identically, so the sample variance is exactly zero. Reported as zero variance, not as an interval — a ±0 interval would claim a precision {n} identical draws do not establish. What this sample shows: {what}.")
                : Invariant($"n={n}: a deterministic pipeline produced {n} identical verdicts, which is one observation repeated {n} times. Reported as zero variance rather than as an interval. What this sample shows: {what}.");
        }
        else if (n < _monteCarlo.MinimumSampleForInterval)
        {
            note = Invariant($"n={n} is below the configured minimum of {_monteCarlo.MinimumSampleForInterval} for a confidence interval, so none is reported. What this sample shows: {what}.");
        }
        else
        {
            var margin = Round1(TCritical95(n - 1) * deviation / Math.Sqrt(n));
            return new MonteCarloStatistics(
                n, mean, minimum, maximum, Round1(maximum - minimum), deviation, decisions, streams,
                StudentT95, Round1(mean - margin), Round1(mean + margin),
                Invariant($"95% confidence interval for the mean, Student t, two-sided, df={n - 1} (n={n}): {Round1(mean - margin)}–{Round1(mean + margin)}%. Alongside it: {what}."));
        }

        return new MonteCarloStatistics(
            n, mean, minimum, maximum, Round1(maximum - minimum), deviation, decisions, streams,
            NoInterval, null, null, note);
    }

    /// <summary>Two-sided 97.5th-percentile Student-t values, indexed by degrees of freedom. Beyond
    /// df=30 the normal approximation is used, which is the conventional cut-off and is well within
    /// the precision this report claims.</summary>
    private static readonly double[] TCritical =
    [
        0, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    private static double TCritical95(int degreesOfFreedom) =>
        degreesOfFreedom >= 1 && degreesOfFreedom < TCritical.Length
            ? TCritical[degreesOfFreedom]
            : 1.960;

    // ---------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------

    /// <summary>Production dispatch: one replay, scored by the deterministic judge with the agent's
    /// committed rubric. No baseline is touched — the verdict recorded by <c>judge</c> stays the
    /// only baseline, and a sample is data about a distribution, not a golden.</summary>
    private MonteCarloSample DispatchReplayAndScore(MonteCarloRequest request)
    {
        var (replayed, usage) = _replay.ReplayObserved(request.Fixture, request.RealCli);
        var scored = _judge.ScoreReplayed(request.Fixture, replayed);
        var verdict = scored.Verdict;
        var error = verdict is null
            ? string.Join("; ", scored.Checks.Where(check => check.Status == Error).Select(check => check.Message))
            : null;

        return new MonteCarloSample(
            request.Run,
            replayed.StreamDigest,
            verdict?.Verdict ?? "<none>",
            verdict is null ? 0 : RubricJudge.Percent(verdict),
            usage.CostUsd,
            usage.CostReported,
            usage.TotalTokens,
            error);
    }

    // ---------------------------------------------------------------------
    // Paths and formatting
    // ---------------------------------------------------------------------

    /// <summary>Messages carry scores and money, so they are formatted invariantly.</summary>
    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private void WriteReport(MonteCarloReport report)
    {
        var directory = Path.Combine(
            ResolveConfiguredPath(_config.ArtifactRoot),
            _monteCarlo.ArtifactSubdirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{report.Agent}.json"), EvalJson.Serialize(report));
    }

    public string ReportPath(string agent) => Path.Combine(
        ResolveConfiguredPath(_config.ArtifactRoot),
        _monteCarlo.ArtifactSubdirectory,
        $"{agent}.json");

    private string ResolveConfiguredPath(string path)
    {
        var resolved = Path.GetFullPath(Path.Combine(_repositoryRoot, path));
        var rootPrefix = _repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repositoryRoot
            : _repositoryRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal) &&
            !string.Equals(resolved, _repositoryRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Configured eval path '{path}' escapes the repository root.");
        }
        return resolved;
    }

    private static void ValidateConfig(MonteCarloConfig config)
    {
        if (config.ArtifactSubdirectory.Length == 0 ||
            config.ArtifactSubdirectory.Contains('/') ||
            config.ArtifactSubdirectory.Contains('\\'))
        {
            throw new InvalidDataException("Monte Carlo artifact subdirectory must be a single path segment.");
        }
        if (config.DefaultRuns <= 0)
            throw new InvalidDataException("Monte Carlo default run count must be positive.");
        if (config.MaxRuns <= 0)
            throw new InvalidDataException("Monte Carlo run ceiling must be positive.");
        if (config.MaxSpendUsd < 0)
            throw new InvalidDataException("Monte Carlo spend ceiling must not be negative.");
        if (config.MinimumSampleForInterval < 2)
            throw new InvalidDataException("A confidence interval needs at least two observations.");
    }
}

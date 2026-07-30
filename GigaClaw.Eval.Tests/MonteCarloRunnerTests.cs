namespace GigaClaw.Eval.Tests;

/// <summary>
/// Monte Carlo tests run against the real repository. The two that dispatch for real need the mock
/// claude CLI (<c>dotnet build GigaClaw.ClaudeMock -c Release</c>) and share the replay collection,
/// because dispatching sets GIGACLAW_CLAUDE_BIN process-wide. The cap and statistics tests inject a
/// dispatcher instead: proving a run was stopped <em>before</em> it started means proving the
/// dispatch never happened, which only the dispatcher can witness.
/// </summary>
[Collection("Replay")]
public sealed class MonteCarloRunnerTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string Fixture = "dev-fix-login-timeout";
    private const string Agent = "programmer";

    [Fact]
    public void CostCap_StopsTheRunThatWouldBreachIt_BeforeItIsDispatched()
    {
        var dispatched = new List<int>();
        var runner = new MonteCarloRunner(
            RepositoryRoot,
            request =>
            {
                dispatched.Add(request.Run);
                return Sample(request.Run, percent: 100, cost: 0.40m);
            });

        // Run 1 costs $0.40, run 2 takes the total to $0.80. Before run 3 the worst observed run is
        // $0.40, so $0.80 + $0.40 = $1.20 would breach the $1.00 ceiling and run 3 is never started.
        var result = runner.Run(
            Fixture,
            runner.DefaultOptions(runs: 10, maxSpendUsd: 1.00m, realCli: true),
            writeReport: false);
        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(new[] { 1, 2 }, dispatched);
        Assert.Equal(2, fixture.DispatchedRuns);
        Assert.Equal(10, fixture.RequestedRuns);
        Assert.Equal("spend", fixture.Cost.CapStatus);
        Assert.Equal(0.80m, fixture.Cost.TotalUsd);
        Assert.Equal(0.40m, fixture.Cost.MeanUsd);
        Assert.Contains("Run 3 was not dispatched", fixture.Cost.CapNote, StringComparison.Ordinal);
        // The cap doing its job is not a failure; measuring nothing would be.
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void CostCap_OfZero_DispatchesNothing_AndSaysSoInsteadOfPassing()
    {
        var dispatched = 0;
        var runner = new MonteCarloRunner(
            RepositoryRoot,
            request =>
            {
                dispatched++;
                return Sample(request.Run, percent: 100, cost: 0.10m);
            });

        var result = runner.Run(
            Fixture,
            runner.DefaultOptions(runs: 4, maxSpendUsd: 0m, realCli: true),
            writeReport: false);
        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(0, dispatched);
        Assert.Equal(0, fixture.DispatchedRuns);
        Assert.Equal(0, fixture.Statistics.SampleSize);
        Assert.Equal("none", fixture.Statistics.IntervalMethod);
        Assert.Contains("n=0", fixture.Statistics.IntervalNote, StringComparison.Ordinal);
        // Nothing was measured, so the run does not get to report success.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            fixture.Checks,
            check => check.Id == "montecarlo.dispatch" && check.Status == "error");
    }

    [Fact]
    public void MaxRuns_ClampsTheRequestedSample_BeforeTheExtraRunsAreDispatched()
    {
        var dispatched = 0;
        var runner = new MonteCarloRunner(
            RepositoryRoot,
            request =>
            {
                dispatched++;
                return Sample(request.Run, percent: 100, cost: 0m);
            });

        var result = runner.Run(
            Fixture,
            runner.DefaultOptions(runs: 12, maxRuns: 3, maxSpendUsd: 100m, realCli: true),
            writeReport: false);
        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(3, dispatched);
        Assert.Equal(3, fixture.DispatchedRuns);
        Assert.Contains(
            fixture.Checks,
            check => check.Id == "montecarlo.maxruns" && check.Status == "warning");
    }

    [Fact]
    public void IdenticalDeterministicRuns_AreReportedAsZeroVariance_NotAsAnInterval()
    {
        var runner = new MonteCarloRunner(RepositoryRoot);

        var result = runner.Run(
            Fixture,
            runner.DefaultOptions(runs: 3, maxSpendUsd: 1000m),
            writeReport: false);
        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, fixture.DispatchedRuns);
        Assert.Equal(3, fixture.Statistics.SampleSize);

        // Three real dispatches through the mock, and they really are one observation three times.
        Assert.Equal(1, fixture.Statistics.DistinctStreams);
        Assert.Equal(1, fixture.Statistics.DistinctDecisions);
        Assert.Equal(0d, fixture.Statistics.StandardDeviation);
        Assert.Equal(0d, fixture.Statistics.Range);

        // The point of the test: no interval is manufactured from a spread that does not exist.
        Assert.Equal("none", fixture.Statistics.IntervalMethod);
        Assert.Null(fixture.Statistics.IntervalLow);
        Assert.Null(fixture.Statistics.IntervalHigh);
        Assert.Contains("zero variance", fixture.Statistics.IntervalNote, StringComparison.Ordinal);
        Assert.Contains("n=3", fixture.Statistics.IntervalNote, StringComparison.Ordinal);

        // And the mode says up front that no variance was possible here at all.
        Assert.False(fixture.Sampling.VarianceIsPossible);
        Assert.Contains(
            fixture.Checks,
            check => check.Id == "montecarlo.sampling" && check.Status == "warning");
        Assert.Contains(
            fixture.Checks,
            check => check.Id == "montecarlo.determinism" && check.Status == "pass");
    }

    [Fact]
    public void AnInterval_IsReportedOnlyAtOrAboveTheMinimumSampleSize_AndNamesItsMethod()
    {
        // A spread that is genuinely there, so the only thing gating the interval is sample size.
        double[] percents = [90, 92, 94, 96, 98, 100];

        var small = WithPercents(percents[..4]).Run(
            Fixture,
            new MonteCarloOptions(4, 20, 100m, RealCli: true),
            writeReport: false);
        var smallFixture = Assert.Single(Assert.Single(small.Reports).Fixtures);

        Assert.Equal(4, smallFixture.Statistics.SampleSize);
        Assert.True(smallFixture.Statistics.StandardDeviation > 0);
        Assert.Equal("none", smallFixture.Statistics.IntervalMethod);
        Assert.Null(smallFixture.Statistics.IntervalLow);
        // It says what it has instead of reporting an interval it cannot support.
        Assert.Contains("below the configured minimum", smallFixture.Statistics.IntervalNote, StringComparison.Ordinal);
        Assert.Contains("mean 93%", smallFixture.Statistics.IntervalNote, StringComparison.Ordinal);
        Assert.Contains("range 90–96%", smallFixture.Statistics.IntervalNote, StringComparison.Ordinal);

        var large = WithPercents(percents).Run(
            Fixture,
            new MonteCarloOptions(6, 20, 100m, RealCli: true),
            writeReport: false);
        var largeFixture = Assert.Single(Assert.Single(large.Reports).Fixtures);

        Assert.Equal(6, largeFixture.Statistics.SampleSize);
        Assert.Equal("student-t-95", largeFixture.Statistics.IntervalMethod);
        Assert.NotNull(largeFixture.Statistics.IntervalLow);
        Assert.NotNull(largeFixture.Statistics.IntervalHigh);
        // mean 95, sd 3.74, t(0.975, df=5) = 2.571 → margin 3.9.
        Assert.Equal(95d, largeFixture.Statistics.Mean);
        Assert.Equal(3.74d, largeFixture.Statistics.StandardDeviation);
        Assert.Equal(91.1d, largeFixture.Statistics.IntervalLow);
        Assert.Equal(98.9d, largeFixture.Statistics.IntervalHigh);
        // Every reported figure carries its sample size, and the method is named.
        Assert.Contains("Student t", largeFixture.Statistics.IntervalNote, StringComparison.Ordinal);
        Assert.Contains("df=5", largeFixture.Statistics.IntervalNote, StringComparison.Ordinal);
        Assert.Contains("(n=6)", largeFixture.Statistics.IntervalNote, StringComparison.Ordinal);
    }

    [Fact]
    public void MonteCarlo_WritesAnEphemeralReport_AndNoBaseline()
    {
        var runner = new MonteCarloRunner(RepositoryRoot);
        var before = Directory.GetFiles(
            Path.Combine(RepositoryRoot, "GigaClaw.Eval", "baselines"), "*.json", SearchOption.AllDirectories).Length;

        var result = runner.Run(Fixture, runner.DefaultOptions(runs: 2, maxSpendUsd: 1000m));

        Assert.Equal(0, result.ExitCode);
        // The report is ephemeral and lives under the gitignored artifact root.
        var report = runner.ReportPath(Agent);
        Assert.True(File.Exists(report));
        Assert.StartsWith(
            Path.Combine(RepositoryRoot, "artifacts", "eval"),
            Path.GetFullPath(report),
            StringComparison.Ordinal);
        // The deterministic verdict stays the only baseline; sampling a distribution records none.
        Assert.Equal(before, Directory.GetFiles(
            Path.Combine(RepositoryRoot, "GigaClaw.Eval", "baselines"), "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void MonteCarlo_RefusesANonsensicalSampleOrCeiling()
    {
        var runner = new MonteCarloRunner(RepositoryRoot);

        Assert.Contains("--runs", Assert.Throws<ArgumentException>(
            () => runner.DefaultOptions(runs: 0)).Message, StringComparison.Ordinal);
        Assert.Contains("--max-runs", Assert.Throws<ArgumentException>(
            () => runner.DefaultOptions(maxRuns: 0)).Message, StringComparison.Ordinal);
        Assert.Contains("--max-spend-usd", Assert.Throws<ArgumentException>(
            () => runner.DefaultOptions(maxSpendUsd: -1m)).Message, StringComparison.Ordinal);
    }

    /// <summary>Feeds a known score sequence through the dispatch seam, so the statistics are
    /// exercised against a sample whose mean, spread and interval can be checked by hand.</summary>
    private static MonteCarloRunner WithPercents(IReadOnlyList<double> percents) =>
        new(RepositoryRoot, request => Sample(request.Run, percents[request.Run - 1], cost: 0m));

    private static MonteCarloSample Sample(int run, double percent, decimal cost) =>
        new(
            Run: run,
            // A distinct digest per run: these stand in for runs that really did differ.
            StreamDigest: $"digest-{run}-{percent}",
            Decision: percent >= 90 ? "SHIP" : "FIX",
            Percent: percent,
            CostUsd: cost,
            CostReported: true,
            TotalTokens: 1000,
            Error: null);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "catalog.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "ProjectTemplate", "Agents")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the repository root from the test assembly.");
    }
}

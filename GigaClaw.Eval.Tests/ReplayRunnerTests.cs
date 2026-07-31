namespace GigaClaw.Eval.Tests;

/// <summary>
/// Replay tests run against the real repository: the committed fixtures, the committed
/// ProjectTemplate skills, and the mock claude CLI built from GigaClaw.ClaudeMock. Build the mock
/// first (<c>dotnet build GigaClaw.ClaudeMock -c Release</c>) or these fail with a message saying so.
///
/// They share one xUnit collection because ReplayRunner sets GIGACLAW_CLAUDE_BIN process-wide and
/// the runtime caches the resolved binary in a static Lazy on first dispatch.
/// </summary>
[CollectionDefinition("Replay", DisableParallelization = true)]
public sealed class ReplayCollection;

[Collection("Replay")]
public sealed class ReplayRunnerTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Replay_IsDeterministic_AcrossRepeatedRunsOfTheSameFixture()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        var first = runner.Run("dev-fix-login-timeout");
        var firstReport = File.ReadAllText(runner.ReportPath("programmer"));
        var second = runner.Run("dev-fix-login-timeout");
        var secondReport = File.ReadAllText(runner.ReportPath("programmer"));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        // The written artifact is what CI diffs, so comparing the files is the real guarantee.
        Assert.Equal(firstReport, secondReport);

        var firstFixture = Assert.Single(Assert.Single(first.Reports).Fixtures);
        var secondFixture = Assert.Single(Assert.Single(second.Reports).Fixtures);
        Assert.Equal(firstFixture.StreamDigest, secondFixture.StreamDigest);
        Assert.Equal(
            firstFixture.Events.Select(e => (e.Index, e.Kind, e.Text)),
            secondFixture.Events.Select(e => (e.Index, e.Kind, e.Text)));
        // A session id would differ between the two runs; it must have been scrubbed.
        Assert.All(firstFixture.Events, e => Assert.DoesNotContain("cwd=/", e.Text, StringComparison.Ordinal));
        Assert.Contains(firstFixture.Events, e => e.Text.Contains("{{session_id}}", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPipelineFamilyHasAFixtureThatReplaysGreen()
    {
        var runner = new ReplayRunner(RepositoryRoot);
        var families = runner.LoadFixtures()
            .Select(fixture => fixture.Family)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "blog", "dev", "governance", "growth", "media", "security" },
            families.OrderBy(family => family, StringComparer.Ordinal));

        var result = runner.Run("all", writeReport: false);

        Assert.Equal(0, result.ExitCode);
        Assert.All(
            result.Reports.SelectMany(report => report.Fixtures),
            fixture => Assert.Equal("pass", fixture.Status));
    }

    [Fact]
    public void Replay_CapturesAFailingDispatchInsteadOfThrowing()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        var result = runner.Run("dev-suite-fails-hard", writeReport: false);
        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(1, fixture.ExitCode);
        Assert.Equal("Failed", fixture.RunStatus);
        Assert.Equal("pass", fixture.Status);
        Assert.All(fixture.Checks, check => Assert.Equal("pass", check.Status));
    }

    [Fact]
    public void Replay_MarksTheFixtureFailed_WhenTheCapturedStreamMissesAnExpectation()
    {
        var runner = new ReplayRunner(RepositoryRoot);
        var committed = runner.LoadFixtures().Single(fixture => fixture.Id == "dev-fix-login-timeout");
        var impossible = committed with
        {
            Expect = committed.Expect with
            {
                EventKinds = [.. committed.Expect.EventKinds, "ask_user_question"],
                FinalTextContains = "this phrase is not in the scenario"
            }
        };

        var result = runner.ReplaySingle(impossible);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.Checks, check => check.Id == "replay.events" && check.Status == "error");
        Assert.Contains(result.Checks, check => check.Id == "replay.text" && check.Status == "error");
        Assert.Contains(result.Checks, check => check.Id == "replay.exit" && check.Status == "pass");
    }

    [Fact]
    public void LoadFixtures_EnumeratesPackFixtureRootsBesideCore()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        var ids = runner.LoadFixtures().Select(fixture => fixture.Id).ToArray();

        // Core fixtures are still present…
        Assert.Contains("dev-fix-login-timeout", ids);
        // …and the security-assurance pack's fixtures are enumerated from Packs/<id>/eval/fixtures.
        Assert.Contains("security-injection-in-review", ids);
        Assert.Contains("security-threat-model-auth", ids);
        Assert.Contains("security-supply-chain-advisory", ids);
        Assert.Contains("security-secret-in-diff", ids);
        Assert.Contains("security-secret-clean-diff", ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PackAgentFixtures_ReplayAgainstThePackSkill_AndPass()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        // Agent-slug targeting must reach a pack agent; secrets-reviewer carries both the BLOCK
        // fixture and the SHIP fixture, so an always-BLOCK regression fails exactly one of them.
        var result = runner.Run("secrets-reviewer", writeReport: false);

        var fixtures = Assert.Single(result.Reports).Fixtures;
        Assert.Equal(
            new[] { "security-secret-clean-diff", "security-secret-in-diff" },
            fixtures.Select(fixture => fixture.Fixture).ToArray());
        Assert.Equal(0, result.ExitCode);
        Assert.All(fixtures, fixture => Assert.Equal("pass", fixture.Status));
    }

    [Fact]
    public void SecretsReviewer_ShipPathFixture_ReplaysAShipVerdict()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        var result = runner.Run("security-secret-clean-diff", writeReport: false);

        var fixture = Assert.Single(Assert.Single(result.Reports).Fixtures);
        Assert.Equal("pass", fixture.Status);
        var finalText = fixture.Events.Last(captured => captured.Kind == "assistant").Text;
        Assert.Contains("SHIP", finalText, StringComparison.Ordinal);
        Assert.DoesNotContain("BLOCK", finalText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDiscoveredPackFixtureDirectoryIsAConfiguredReplayRoot()
    {
        // catalog.json advertises EvalFixturePresent from the fixture directories
        // PackCatalogSourceReader discovers; the replay layer enumerates the roots evalconfig.json
        // configures. This pins the two to each other: a pack that ships fixtures without being
        // added to Replay.FixtureRoots would make the catalog claim coverage replay cannot execute.
        var configured = new ReplayRunner(RepositoryRoot).LoadFixtures()
            .Select(fixture => fixture.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var source in GigaClaw.Catalog.PackCatalogSourceReader.Discover(RepositoryRoot))
        {
            if (source.FixtureDirectory is null) continue;
            foreach (var path in Directory.EnumerateFiles(source.FixtureDirectory, "*.json"))
                Assert.Contains(Path.GetFileNameWithoutExtension(path), configured);
        }
    }

    [Fact]
    public void Replay_RejectsAnUnknownTarget()
    {
        var runner = new ReplayRunner(RepositoryRoot);

        var exception = Assert.Throws<ArgumentException>(
            () => runner.Run("no-such-fixture", writeReport: false));

        Assert.Contains("Unknown replay target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealCli_IsRefused_UnlessTheCostIsAcknowledged()
    {
        var previous = Environment.GetEnvironmentVariable(ReplayRunner.AllowRealCliVariable);
        Environment.SetEnvironmentVariable(ReplayRunner.AllowRealCliVariable, null);
        try
        {
            var runner = new ReplayRunner(RepositoryRoot);
            var exception = Assert.Throws<ArgumentException>(
                () => runner.Run("dev-fix-login-timeout", realCli: true, writeReport: false));
            Assert.Contains(ReplayRunner.AllowRealCliVariable, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplayRunner.AllowRealCliVariable, previous);
        }
    }

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

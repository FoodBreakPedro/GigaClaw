using GigaClaw.Eval.Tests.Helpers;
using System.Diagnostics;
using System.Text.Json;

namespace GigaClaw.Eval.Tests;

/// <summary>
/// Judge tests run against the real repository: the committed fixtures, rubrics and baselines, and
/// the mock claude CLI built from GigaClaw.ClaudeMock (<c>dotnet build GigaClaw.ClaudeMock -c
/// Release</c>). They share the replay collection because judging replays, and the replay layer
/// sets GIGACLAW_CLAUDE_BIN process-wide.
/// </summary>
[Collection("Replay")]
public sealed class JudgeRunnerTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string Fixture = "dev-fix-login-timeout";
    private const string Agent = "programmer";

    [Fact]
    public void Judge_IsByteIdentical_AcrossRepeatedRunsOfTheSameFixture()
    {
        var runner = new JudgeRunner(RepositoryRoot);

        var first = runner.Run(Fixture);
        var firstReport = File.ReadAllText(runner.ReportPath(Agent));
        var second = runner.Run(Fixture);
        var secondReport = File.ReadAllText(runner.ReportPath(Agent));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        // The written artifact is what CI diffs, so comparing the files is the real guarantee.
        Assert.Equal(firstReport, secondReport);

        var firstVerdict = Assert.Single(Assert.Single(first.Reports).Fixtures).Verdict;
        var secondVerdict = Assert.Single(Assert.Single(second.Reports).Fixtures).Verdict;
        Assert.NotNull(firstVerdict);
        Assert.Equal(
            JsonSerializer.Serialize(firstVerdict),
            JsonSerializer.Serialize(secondVerdict));
        // A wall clock would be the one thing that could differ between the two.
        Assert.Equal(RubricJudge.DeterministicReviewInstant, firstVerdict!.ReviewedAtUtc);
    }

    [KnownWindowsFailureFact(
        "Only the stream-digest fields (evidence[].ref / inputDigest) drift; scored text is "
        + "identical to the character. The workspace-path theory is DISPROVEN: aed9f74's "
        + "structural scrub changed the produced Windows digests not at all (run 30673550327 "
        + "byte-matches the pre-fix run 30669812287), so the differing bytes are something the "
        + "path scrub never touches and remain unidentified. The exemption was briefly removed "
        + "(118496e) on a false 'observed green' — a continue-on-error step's conclusion always "
        + "reads success; only its log or outcome tells the truth. Next step: dump the normalized "
        + "stream on a Windows runner and diff it against the committed macOS reference.")]
    public void Judge_MatchesTheCommittedBaselineForEveryFixture()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var result = runner.Run("all", writeReport: false);

        var fixtures = result.Reports.SelectMany(report => report.Fixtures).ToArray();

        // Baseline status first, and with the drift spelled out. This test has been exempted on
        // Windows for longer than anyone can date, and the reason it stayed undiagnosed is that it
        // reported only "expected match, got drift" — which names the symptom and nothing else.
        // Every verdict is a hash of the normalized replay stream plus scored text, so the useful
        // question is always *which field moved*; print it rather than making the next person
        // reconstruct it from a Windows machine they may not have.
        var drifted = fixtures.Where(f => f.BaselineStatus != "match").ToArray();
        Assert.True(drifted.Length == 0, DescribeBaselineDrift(drifted, runner));

        Assert.Equal(0, result.ExitCode);
        // 34 core fixtures (the eval-fixture authoring pass closed core's historic backlog against
        // owner Q2's item, and the baseline review's D8 added the render-gate fixture) plus the
        // security-assurance pack's 5.
        Assert.Equal(39, fixtures.Length);
        Assert.All(fixtures, fixture =>
        {
            Assert.Equal("pass", fixture.Status);
            Assert.NotNull(fixture.Verdict);
        });

        // dev-suite-fails-hard now discharges qa-tester's contract — SKILL.md calls this situation
        // cannot-exercise-change, which is a BLOCK verdict and a move to Blocked — so the judge
        // scores it SHIP. The agent's BLOCK and the judge's SHIP are the two axes described in
        // doc/verdict-contract.md; an agent that correctly refuses has performed correctly.
        var qa = fixtures.Single(fixture => fixture.Agent == "qa-tester");
        Assert.Equal("SHIP", qa.Verdict!.Verdict);
    }

    /// <summary>
    /// RubricJudge.Score derives BLOCK from <c>vetoItems.Count &gt; 0</c> before any threshold
    /// comparison, so a regression that dropped veto handling entirely would still leave every
    /// all-SHIP baseline matching. media-render-before-sign-off exists to make that impossible:
    /// it is the one committed fixture whose run trips a veto, and it must stay one.
    /// </summary>
    [Fact]
    public void TheVetoPathIsPinnedByACommittedBaseline()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var result = runner.Run("all", writeReport: false);
        var fixtures = result.Reports.SelectMany(report => report.Fixtures).ToArray();

        var blocked = fixtures
            .Where(fixture => fixture.Verdict!.Verdict == "BLOCK")
            .ToArray();
        Assert.NotEmpty(blocked);

        var render = fixtures.Single(fixture => fixture.Fixture == "media-render-before-sign-off");
        Assert.Equal("BLOCK", render.Verdict!.Verdict);
        var veto = Assert.Single(render.Verdict.VetoItems);
        Assert.Equal("render-without-sign-off", veto.Code);
    }

    /// <summary>
    /// Renders a baseline drift as the committed verdict beside the produced one, field by field,
    /// so the failure names its own cause. Kept next to the assertion rather than in a helper
    /// class: its whole job is to be read at the moment the test goes red.
    /// </summary>
    private static string DescribeBaselineDrift(IReadOnlyList<JudgeFixtureResult> drifted, JudgeRunner runner)
    {
        // Assert.True evaluates its message argument unconditionally — even when `drifted` is
        // empty and the assertion is about to pass — so this must tolerate the empty case rather
        // than assume a caller only builds the message on failure.
        if (drifted.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{drifted.Count} fixture(s) do not reproduce the committed baseline. ");
        sb.Append($"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} ");
        sb.Append($"tempPath={Path.GetTempPath()}");

        var options = new JsonSerializerOptions { WriteIndented = true };
        foreach (var fixture in drifted)
        {
            sb.Append($"\n\n--- {fixture.Agent} / {fixture.Fixture}: {fixture.BaselineStatus} ---");

            var baselinePath = Path.Combine(
                RepositoryRoot, "GigaClaw.Eval", "baselines", "judge", fixture.Agent + ".json");
            if (!File.Exists(baselinePath))
            {
                sb.Append($"\n  no baseline file at {baselinePath}");
                continue;
            }

            var committed = JsonDocument.Parse(File.ReadAllText(baselinePath))
                .RootElement.GetProperty("Fixtures")
                .EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("Fixture").GetString() == fixture.Fixture);

            sb.Append("\n  committed: ").Append(
                committed.ValueKind == JsonValueKind.Undefined
                    ? "(no entry for this fixture)"
                    : Indent(committed.GetProperty("Verdict").GetRawText()));
            sb.Append("\n  produced:  ").Append(
                fixture.Verdict is null ? "(null)" : Indent(JsonSerializer.Serialize(fixture.Verdict, options)));
        }

        // The two most likely remaining culprits, both invisible in a bare status string.
        sb.Append("\n\nIf only `evidence[].ref` / `inputDigest` moved, the normalized replay stream ");
        sb.Append("differs — compare ReplayRunner.Normalize's workspace scrubbing against the ");
        sb.Append("temp path above (8.3 short names and symlinked temp dirs both defeat a plain ");
        sb.Append("string Replace). If a `notes` character count moved, the scored text itself ");
        sb.Append("differs and the mock CLI's output is the place to look.");

        // Line-level evidence for the first drifting fixture: the exact bytes evidence[].ref /
        // inputDigest hash, diffed against the macOS reference committed at
        // GigaClaw.Eval/baselines/normalized-streams/. A hash difference names a symptom; this
        // names the byte. Only the first fixture, not all of them — the point is to localize the
        // mechanism once, not to dump 29 fixtures' worth of stream into one CI log.
        var first = drifted[0];
        sb.Append($"\n\n=== normalized-stream evidence for {first.Agent} / {first.Fixture} " +
                  $"(first of {drifted.Count} drifted fixture(s)) ===\n");
        sb.Append(JudgeStreamEvidence.Describe(RepositoryRoot, first.Fixture, runner.NormalizedStream(first.Fixture)));

        return sb.ToString();

        static string Indent(string json) => json.Replace("\n", "\n  ");
    }

    [Fact]
    public void Judge_RejectsAVerdictThatBreaksTheContract_InsteadOfWritingIt()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var fixture = LoadFixture(Fixture);
        // Two criteria under one name: the contract forbids a repeated category, so the verdict
        // this rubric produces must never reach a report or a baseline.
        var rubric = new AgentRubric(
            Version: 1,
            Agent: Agent,
            Description: "Deliberately contract-breaking.",
            Criteria:
            [
                new RubricCriterion("Same name", "replay-expectations", 10),
                new RubricCriterion("Same name", "no-error-events", 10),
            ]);

        var result = runner.JudgeSingle(fixture, rubric);

        Assert.Equal("error", result.Status);
        Assert.Null(result.Verdict);
        Assert.Contains(
            result.Checks,
            check => check.Id == "judge.contract" && check.Status == "error" &&
                     check.Message.Contains("repeats a category name", StringComparison.Ordinal));
        // The result record is exactly what gets serialized into the report file.
        Assert.DoesNotContain("schemaVersion", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_TurnsAFailedVetoCriterionIntoABlock()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var rubric = new AgentRubric(
            Version: 1,
            Agent: Agent,
            Description: "One veto criterion the committed scenario cannot satisfy.",
            Criteria:
            [
                new RubricCriterion("Dispatch behaved as declared", "replay-expectations", 50),
                new RubricCriterion(
                    "Shipped the migration",
                    "final-text-contains-all",
                    50,
                    Values: ["this phrase is not in the scenario"],
                    Veto: "migration-missing",
                    Statement: "The run never shipped the migration it was dispatched for."),
            ]);

        var result = runner.JudgeSingle(LoadFixture(Fixture), rubric);

        Assert.Equal("pass", result.Status);
        Assert.Equal("BLOCK", result.Verdict!.Verdict);
        var veto = Assert.Single(result.Verdict.VetoItems);
        Assert.Equal("migration-missing", veto.Code);
        // A veto item must cite evidence the verdict actually lists, or the contract rejects it.
        Assert.All(veto.EvidenceRefs, reference =>
            Assert.Contains(result.Verdict.Evidence, evidence => evidence.Ref == reference));
    }

    [Fact]
    public void EveryCommittedBaselineVerdictPassesTheShippedValidator()
    {
        var directory = Path.Combine(RepositoryRoot, "GigaClaw.Eval", "baselines", "judge");
        var baselines = Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        // 33 core fixture agents plus the security-assurance pack's 4.
        Assert.Equal(37, baselines.Length);

        var scratch = Path.Combine(Path.GetTempPath(), "gigaclaw-judge-verdicts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var path in baselines)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in document.RootElement.GetProperty("Fixtures").EnumerateArray())
                {
                    var name = entry.GetProperty("Fixture").GetString()!;
                    var verdict = Path.Combine(scratch, $"{name}.json");
                    File.WriteAllText(verdict, entry.GetProperty("Verdict").GetRawText());

                    // The Python validator shipped to workspaces is the authoring-side enforcement
                    // point. A judge verdict it would reject is not the same object a reviewer emits.
                    var (exitCode, output) = RunValidator(verdict);
                    Assert.True(exitCode == 0, $"{name} must satisfy the v1 verdict contract:{Environment.NewLine}{output}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LlmJudge_IsInformational_AndRecordsWhatProducedIt()
    {
        var runner = new JudgeRunner(
            RepositoryRoot,
            request => Canned(request, "SHIP", 100, request.InputDigest));

        var result = runner.Run(Fixture, llm: true, writeReport: false);
        var judged = Assert.Single(Assert.Single(result.Reports).Fixtures);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("deterministic+llm", judged.Mode);
        // The deterministic verdict is still the one that is baselined and reported.
        Assert.Equal(RubricJudge.DeterministicReviewInstant, judged.Verdict!.ReviewedAtUtc);
        Assert.Equal("match", judged.BaselineStatus);

        Assert.NotNull(judged.Model);
        Assert.Equal("claude-opus-5", judged.Model!.ReportedModel);
        Assert.Equal("mock-cli 9.9.9", judged.Model.CliVersion);
        Assert.Equal(1, judged.Model.MaxTurns);
        Assert.StartsWith("sha256:", judged.Model.PromptDigest, StringComparison.Ordinal);

        Assert.NotNull(judged.Tolerance);
        Assert.True(judged.Tolerance!.WithinTolerance);
        Assert.True(judged.Tolerance.DecisionAgrees);
    }

    [Fact]
    public void LlmJudge_DiscardsAVerdictBoundToADifferentStream_WithoutFailingTheRun()
    {
        var runner = new JudgeRunner(
            RepositoryRoot,
            request => Canned(request, "SHIP", 100, "sha256:" + new string('b', 64)));

        var result = runner.Run(Fixture, llm: true, writeReport: false);
        var judged = Assert.Single(Assert.Single(result.Reports).Fixtures);

        // Discarded, but an unreproducible judge never breaks the build.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("pass", judged.Status);
        Assert.Null(judged.Tolerance);
        var check = Assert.Single(judged.Checks, check => check.Id == "judge.llm.contract");
        Assert.Equal("error", check.Status);
        Assert.Equal("informational", check.Category);
        Assert.Contains("bound to", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LlmJudge_IsRefused_UnlessTheCostIsAcknowledged()
    {
        var previous = Environment.GetEnvironmentVariable(LlmJudge.AllowVariable);
        Environment.SetEnvironmentVariable(LlmJudge.AllowVariable, null);
        try
        {
            var result = new JudgeRunner(RepositoryRoot).Run(Fixture, llm: true, writeReport: false);
            var judged = Assert.Single(Assert.Single(result.Reports).Fixtures);

            Assert.Equal(0, result.ExitCode);
            var check = Assert.Single(judged.Checks, check => check.Id == "judge.llm.contract");
            Assert.Contains(LlmJudge.AllowVariable, check.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmJudge.AllowVariable, previous);
        }
    }

    // ------------------------------------------------------------------
    // replay-expectation-ids: the split of the old single 40-point
    // `replay-expectations` bucket. The whole point of the check kind is that
    // *which* expectation failed now changes the score, so every test below is
    // written as a pair — the same rubric against a stream that satisfies the
    // named expectation and one that does not.
    // ------------------------------------------------------------------

    [Fact]
    public void ReplayExpectationIds_ScoreTheReceiptApartFromTheDispatchAssertions()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var rubric = SplitExpectationRubric();

        // Passing half: the committed fixture satisfies every expectation it declares.
        var passing = runner.JudgeSingle(LoadFixture(Fixture), rubric);

        Assert.Equal("pass", passing.Status);
        Assert.Equal("SHIP", passing.Verdict!.Verdict);
        Assert.Equal(15, Category(passing, "Dispatch behaved as declared").Score);
        Assert.Equal(25, Category(passing, "Delivered the declared receipt").Score);

        // Failing half: only the declared final-text marker is unsatisfiable. Under the old
        // all-or-nothing `replay-expectations` criterion this and a wrong exit code were the same
        // 0; here the dispatch assertions keep their full 15 and only the receipt is docked.
        var failing = runner.JudgeSingle(
            WithExpectedFinalText(LoadFixture(Fixture), "no assistant message ever says this"),
            rubric);

        Assert.Equal("pass", failing.Status);
        Assert.Equal(15, Category(failing, "Dispatch behaved as declared").Score);
        Assert.Equal(0, Category(failing, "Delivered the declared receipt").Score);
        Assert.Contains("replay.text", Category(failing, "Delivered the declared receipt").Notes!, StringComparison.Ordinal);
        // 15 + 20 + 20 + 20 out of 100 — a receipt-less run is no longer indistinguishable from a
        // clean one, which is the entire reason the criterion was split.
        Assert.Equal(75, failing.Verdict!.Categories.Sum(category => category.Score));

        // The behaviour this replaced, pinned so the improvement is legible: the undivided
        // `replay-expectations` criterion collapses the same stream to a flat 0, which is why a
        // wrong exit code and a missing receipt used to be the same 40-point loss.
        var undivided = runner.JudgeSingle(
            WithExpectedFinalText(LoadFixture(Fixture), "no assistant message ever says this"),
            new AgentRubric(1, Agent, "The pre-split criterion.",
                [new RubricCriterion("Dispatch behaved as declared", "replay-expectations", 40)]));
        Assert.Equal(0, Category(undivided, "Dispatch behaved as declared").Score);
    }

    [Fact]
    public void ReplayExpectationIds_TripTheirVetoWhenTheNamedExpectationIsUnmet()
    {
        var runner = new JudgeRunner(RepositoryRoot);

        var result = runner.JudgeSingle(
            WithExpectedFinalText(LoadFixture(Fixture), "no assistant message ever says this"),
            SplitExpectationRubric());

        // Fail-closed exactly as the single 40-point criterion did: an unmet expectation is a veto,
        // not merely a lower score, so a split that quietly turned BLOCK into FIX would fail here.
        Assert.Equal("BLOCK", result.Verdict!.Verdict);
        var veto = Assert.Single(result.Verdict.VetoItems);
        Assert.Equal("replay-expectation-unmet", veto.Code);
    }

    [Fact]
    public void ReplayExpectationIds_FailClosedOnAnExpectationTheReplayNeverProduced()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var rubric = new AgentRubric(
            Version: 1,
            Agent: Agent,
            Description: "Names an expectation id the replay layer does not emit.",
            Criteria:
            [
                new RubricCriterion(
                    "Delivered the declared receipt",
                    "replay-expectation-ids",
                    100,
                    Values: ["replay.text", "replay.telepathy"]),
            ]);

        var result = runner.JudgeSingle(LoadFixture(Fixture), rubric);

        // A rubric naming an expectation that no longer exists is a broken instrument. Scoring the
        // criterion at its maximum because nothing contradicted it is the failure mode this guards.
        Assert.Equal(0, Category(result, "Delivered the declared receipt").Score);
        Assert.Contains(
            "replay.telepathy",
            Category(result, "Delivered the declared receipt").Notes!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRubric_GradesEveryExpectationTheReplayLayerAsserts()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var (rubric, source) = runner.LoadRubric("documentalist");
        Assert.Equal("default", source);

        var graded = rubric.Criteria
            .Where(criterion => criterion.Check == "replay-expectation-ids")
            .SelectMany(criterion => criterion.Values ?? [])
            .ToHashSet(StringComparer.Ordinal);

        var asserted = new ReplayRunner(RepositoryRoot)
            .ReplaySingle(LoadFixture(Fixture))
            .Checks.Select(check => check.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Splitting one bucket into two named ones introduces a way to lose an assertion silently:
        // add a replay check the default rubric does not name and it stops being scored at all.
        Assert.Equal(asserted.OrderBy(id => id, StringComparer.Ordinal), graded.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>The committed default rubric's split shape, restated in-memory so these tests read
    /// as a specification of the check kind rather than as a mirror of one JSON file.</summary>
    private static AgentRubric SplitExpectationRubric() =>
        new(Version: 1,
            Agent: Agent,
            Description: "The default rubric's split replay expectations.",
            Criteria:
            [
                new RubricCriterion(
                    "Dispatch behaved as declared",
                    "replay-expectation-ids",
                    15,
                    Values: ["replay.dispatch", "replay.exit", "replay.status", "replay.events"],
                    Veto: "replay-expectation-unmet",
                    Statement: "The run did not start, exit, terminate or emit events the way the fixture declares."),
                new RubricCriterion(
                    "Delivered the declared receipt",
                    "replay-expectation-ids",
                    25,
                    Values: ["replay.text"],
                    Veto: "replay-expectation-unmet",
                    Statement: "The final message does not carry the marker the fixture declares."),
                new RubricCriterion("Stream carries no error", "no-error-events", 20),
                new RubricCriterion("Final message is a handoff", "final-text-min-length", 20, Threshold: 80),
                new RubricCriterion(
                    "No placeholder handoff",
                    "final-text-omits-all",
                    20,
                    Values: ["TODO:", "lorem ipsum", "as an ai", "placeholder text"]),
            ]);

    private static JudgeCategory Category(JudgeFixtureResult result, string name) =>
        result.Verdict!.Categories.Single(category => category.Name == name);

    private static ReplayFixture WithExpectedFinalText(ReplayFixture fixture, string marker) =>
        fixture with { Expect = fixture.Expect with { FinalTextContains = marker } };

    [Fact]
    public void EveryFixtureAgentHasAResolvableRubric()
    {
        var runner = new JudgeRunner(RepositoryRoot);
        var replay = new ReplayRunner(RepositoryRoot);

        foreach (var fixture in replay.LoadFixtures())
        {
            var (rubric, source) = runner.LoadRubric(fixture.Agent);
            // Core fixture agents ship bespoke rubrics; pack agents without one are judged by the
            // shared default — resolvable either way is what this test guarantees.
            Assert.True(
                source == fixture.Agent || source == "default",
                $"Rubric for '{fixture.Agent}' resolved to unexpected source '{source}'.");
            Assert.Equal(source, rubric.Agent);
            Assert.NotEmpty(rubric.Criteria);
        }

        // An agent without a bespoke rubric still gets judged, by the shared default.
        var (fallback, fallbackSource) = runner.LoadRubric("documentalist");
        Assert.Equal("default", fallbackSource);
        Assert.Equal("default", fallback.Agent);
    }

    private static LlmJudgeTranscript Canned(LlmJudgeRequest request, string decision, double score, string digest)
    {
        var verdict = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["agent"] = request.Fixture.Agent,
            ["ticketId"] = request.Fixture.Ticket.Id,
            ["verdict"] = decision,
            ["summary"] = "Canned judge reply used to exercise the transport.",
            ["categories"] = new[] { new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "Overall", ["score"] = score, ["max"] = 100.0,
            } },
            ["vetoItems"] = Array.Empty<object>(),
            ["evidence"] = new[] { new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["kind"] = "hash", ["ref"] = digest,
            } },
            ["reviewedAtUtc"] = "2026-07-30T12:00:00Z",
            ["inputDigest"] = digest,
        };

        var body = JsonSerializer.Serialize(verdict, new JsonSerializerOptions { WriteIndented = true });
        var text =
            $"GIGACLAW-VERDICT v1 {request.Fixture.Agent} {decision} artifact-{digest}\n\n" +
            $"```json\n{body}\n```\n";
        return new LlmJudgeTranscript(text, "mock-cli", "mock-cli 9.9.9", "claude-opus-5", "claude-opus-5", 1);
    }

    private static ReplayFixture LoadFixture(string id) =>
        new ReplayRunner(RepositoryRoot).LoadFixtures().Single(fixture => fixture.Id == id);

    private static (int ExitCode, string Output) RunValidator(string verdictPath)
    {
        var script = Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents", "scripts", "verdict_contract.py");
        foreach (var (executable, prefix) in new (string, string[])[]
                 { ("python3", []), ("python", []), ("py", ["-3"]) })
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = RepositoryRoot,
            };
            foreach (var argument in prefix.Append(script).Append(verdictPath))
                info.ArgumentList.Add(argument);

            try
            {
                using var process = Process.Start(info);
                if (process is null) continue;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output);
            }
            catch (Exception)
            {
                // Interpreter not installed under this name; try the next.
            }
        }

        throw new InvalidOperationException(
            "Python 3 was not found (tried python3, python, py -3). The verdict contract's single " +
            "implementation is Python, so it is a prerequisite for judging.");
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

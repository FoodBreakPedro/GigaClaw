using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GigaClaw.Core.Automation.Verdicts;

namespace GigaClaw.Eval;

/// <summary>
/// The judge pass: replay a fixture, score the captured stream against the agent's rubric, and
/// emit a v1 verdict — the same object a reviewer emits, read back by the same host-side reader
/// before it is allowed anywhere near disk.
///
/// Two judges, deliberately unequal in standing:
/// * the deterministic <see cref="RubricJudge"/> is the default, is byte-reproducible, is compared
///   against a committed baseline, and owns the exit code;
/// * the opt-in <see cref="LlmJudge"/> is informational — recorded with the model, CLI version and
///   settings that produced it, compared against the deterministic verdict within a tolerance, and
///   never baselined, because an LLM verdict is not reproducible and this harness does not pretend
///   it is.
///
/// Sampling variance across repeated runs belongs to <see cref="MonteCarloRunner"/>, which reuses
/// <see cref="ScoreReplayed"/> as its fixed measuring instrument.
/// </summary>
public sealed class JudgeRunner
{
    private const string Pass = "pass";
    private const string Error = "error";
    private const string Integrity = "integrity";
    private const string Policy = "policy";
    private const string Informational = "informational";
    private const string DefaultRubric = "default";

    private readonly string _repositoryRoot;
    private readonly EvalConfig _config;
    private readonly JudgeConfig _judge;
    private readonly ReplayRunner _replay;
    private readonly LlmJudgeDispatch _llmDispatch;

    public JudgeRunner(string repositoryRoot, LlmJudgeDispatch? llmDispatch = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _config = EvalJson.Read<EvalConfig>(
            Path.Combine(_repositoryRoot, "GigaClaw.Eval", "evalconfig.json"));
        _judge = _config.Judge ?? JudgeConfig.Default;
        ValidateJudgeConfig(_judge);
        _replay = new ReplayRunner(_repositoryRoot);
        _llmDispatch = llmDispatch ?? LlmJudge.DispatchRealCli;
        _ = ResolveConfiguredPath(_judge.RubricRoot);
    }

    /// <param name="target">A fixture id, a pipeline family, a catalog agent slug, or "all".</param>
    /// <param name="llm">Also ask a real model to score the same stream. Costed, informational.</param>
    public JudgeRunResult Run(
        string target,
        bool llm = false,
        bool updateBaselines = false,
        bool writeReport = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var fixtures = _replay.LoadFixtures().ToDictionary(fixture => fixture.Id, StringComparer.Ordinal);
        var replayed = _replay.Run(target, realCli: false, writeReport: false);

        var judged = replayed.Reports
            .SelectMany(report => report.Fixtures)
            .OrderBy(result => result.Fixture, StringComparer.Ordinal)
            .Select(result => JudgeOne(fixtures[result.Fixture], result, llm))
            .ToArray();

        if (updateBaselines)
        {
            foreach (var group in judged.GroupBy(result => result.Agent, StringComparer.Ordinal))
                WriteBaseline(group.Key, group);
            judged = [.. judged.Select(result => Restate(result with
            {
                BaselineStatus = CompareBaseline(result.Agent, result.Fixture, result.Verdict),
            }))];
        }

        var reports = judged
            .GroupBy(result => result.Agent, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new JudgeReport(
                Version: 1,
                Target: target,
                Agent: group.Key,
                Fixtures: group.OrderBy(result => result.Fixture, StringComparer.Ordinal).ToArray()))
            .ToArray();

        if (writeReport)
            foreach (var report in reports)
                WriteReport(report);
        stopwatch.Stop();

        var failed = reports.SelectMany(report => report.Fixtures).Any(result => result.Status != Pass);
        return new JudgeRunResult(reports, failed ? 1 : 0, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Judges one in-memory fixture against an in-memory rubric, skipping the baseline.
    /// Lets a caller exercise a rubric that must be refused without committing one.</summary>
    public JudgeFixtureResult JudgeSingle(ReplayFixture fixture, AgentRubric rubric)
    {
        var replayed = _replay.ReplaySingle(fixture);
        return Score(fixture, replayed, rubric, rubric.Agent, compareBaseline: false, llm: false);
    }

    /// <summary>Scores a stream that has already been replayed, with the agent's committed rubric
    /// and without any baseline comparison. The Monte Carlo layer needs a fixed measuring
    /// instrument, not a golden: one sample of a distribution is data, never a baseline.</summary>
    public JudgeFixtureResult ScoreReplayed(ReplayFixture fixture, ReplayFixtureResult replayed)
    {
        var (rubric, source) = LoadRubric(fixture.Agent);
        return Score(fixture, replayed, rubric, source, compareBaseline: false, llm: false);
    }

    /// <summary>Re-replays one fixture and returns the exact bytes that fed its
    /// <c>evidence[].ref</c>/<c>inputDigest</c> — see <see cref="ReplayRunner.CanonicalStream"/>.
    /// Diagnostic only, never on the hot scoring path: a baseline drift names a hash difference,
    /// and this is what turns that into something a human can diff.</summary>
    public string NormalizedStream(string fixtureId)
    {
        var fixture = _replay.LoadFixtures()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, fixtureId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown fixture '{fixtureId}'.");
        return ReplayRunner.CanonicalStream(_replay.ReplaySingle(fixture).Events);
    }

    /// <summary>Regenerates the committed normalized-stream reference dumps under
    /// <c>GigaClaw.Eval/baselines/normalized-streams/</c> for every fixture <paramref name="target"/>
    /// resolves to. Unlike <c>--update-baselines</c>, which records a new verdict, this records the
    /// bytes the verdict's digest was computed from — meant to be regenerated on every platform whose
    /// bytes should be on record (the committed reference here is macOS's), not only when a rubric
    /// changes.</summary>
    public void WriteStreamBaselines(string target)
    {
        foreach (var fixture in _replay.ResolveTarget(target))
        {
            var replayed = _replay.ReplaySingle(fixture);
            JudgeStreamEvidence.WriteReference(
                _repositoryRoot, fixture.Id, ReplayRunner.CanonicalStream(replayed.Events));
        }
    }

    private JudgeFixtureResult JudgeOne(ReplayFixture fixture, ReplayFixtureResult replayed, bool llm)
    {
        var (rubric, source) = LoadRubric(fixture.Agent);
        return Score(fixture, replayed, rubric, source, compareBaseline: true, llm);
    }

    private JudgeFixtureResult Score(
        ReplayFixture fixture,
        ReplayFixtureResult replayed,
        AgentRubric rubric,
        string rubricSource,
        bool compareBaseline,
        bool llm)
    {
        var checks = new List<EvalCheckResult>();
        var rubricDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JudgeJson.Canonical(rubric))));

        JudgeVerdict? verdict;
        try
        {
            verdict = RubricJudge.Score(fixture, replayed, rubric, rubricDigest, _judge.ShipThresholdPercent);
            checks.Add(new EvalCheckResult(
                "judge.rubric", Integrity, Pass,
                $"Scored {rubric.Criteria.Count} criterion/criteria from rubric '{rubricSource}'."));
        }
        catch (InvalidDataException exception)
        {
            return new JudgeFixtureResult(
                fixture.Id, fixture.Family, fixture.Agent, RubricJudge.Mode, rubricSource,
                Error, "not-compared", null, null, null,
                [new EvalCheckResult("judge.rubric", Integrity, Error, exception.Message)]);
        }

        // The gate. An unvalidated judge verdict is worse than none, so a verdict that breaks the
        // frozen contract is dropped here and never reaches the report, the baseline, or a caller.
        if (!IsContractValid(verdict, out var contractError))
        {
            checks.Add(new EvalCheckResult(
                "judge.contract", Integrity, Error,
                $"The judge verdict violates the v1 verdict contract and was discarded: {contractError}"));
            return new JudgeFixtureResult(
                fixture.Id, fixture.Family, fixture.Agent, RubricJudge.Mode, rubricSource,
                Error, "not-compared", null, null, null, checks);
        }

        checks.Add(new EvalCheckResult(
            "judge.contract", Integrity, Pass,
            "The judge verdict satisfies the v1 verdict contract."));
        checks.Add(new EvalCheckResult(
            "judge.decision", Policy, Pass,
            Invariant($"{verdict.Verdict} at {RubricJudge.Percent(verdict)}% with {verdict.VetoItems.Count} veto item(s).")));

        var baselineStatus = compareBaseline
            ? CompareBaseline(fixture.Agent, fixture.Id, verdict)
            : "not-compared";
        if (compareBaseline)
            checks.Add(BaselineCheck(baselineStatus));

        JudgeModelRecord? model = null;
        JudgeTolerance? tolerance = null;
        if (llm)
        {
            var inputDigest = verdict.InputDigest;
            var request = new LlmJudgeRequest(
                fixture, replayed, rubric,
                LlmJudge.BuildPrompt(fixture, replayed, rubric, inputDigest),
                inputDigest);

            try
            {
                var (llmVerdict, llmModel, errors) = LlmJudge.Judge(request, _llmDispatch);
                model = llmModel;
                string? llmContractError = null;
                var accepted = llmVerdict is not null && IsContractValid(llmVerdict, out llmContractError);
                if (!accepted)
                {
                    var reasons = errors.Count > 0
                        ? string.Join("; ", errors)
                        : llmContractError ?? "no verdict was produced";
                    checks.Add(new EvalCheckResult(
                        "judge.llm.contract", Informational, Error,
                        $"The LLM verdict was discarded: {reasons}"));
                }
                else
                {
                    tolerance = LlmJudge.Compare(verdict, llmVerdict!, _judge.LlmTolerancePercent);
                    checks.Add(new EvalCheckResult(
                        "judge.llm.contract", Informational, Pass,
                        Invariant($"{llmVerdict!.Verdict} at {tolerance.LlmPercent}% from {model.ReportedModel} ({model.CliVersion}).")));
                    checks.Add(new EvalCheckResult(
                        "judge.llm.tolerance",
                        Informational,
                        tolerance.WithinTolerance && tolerance.DecisionAgrees ? Pass : "warning",
                        Invariant($"LLM is {tolerance.DeltaPercent} point(s) from the deterministic {tolerance.DeterministicPercent}% ") +
                        Invariant($"(tolerance {tolerance.TolerancePercent}); decisions {(tolerance.DecisionAgrees ? "agree" : "differ")}. ") +
                        "Informational: an LLM verdict is not reproducible and never fails this run."));
                }
            }
            catch (ArgumentException exception)
            {
                checks.Add(new EvalCheckResult("judge.llm.contract", Informational, Error, exception.Message));
            }
        }

        return new JudgeFixtureResult(
            fixture.Id,
            fixture.Family,
            fixture.Agent,
            llm ? $"{RubricJudge.Mode}+{LlmJudge.Mode}" : RubricJudge.Mode,
            rubricSource,
            checks.Any(check => check.Category == Integrity && check.Status == Error) ? Error : Pass,
            baselineStatus,
            verdict,
            model,
            tolerance,
            checks);
    }

    /// <summary>Messages carry scores, so they are formatted invariantly: a report written under a
    /// comma-decimal locale must not differ from one written under a dot-decimal locale.</summary>
    private static string Invariant(FormattableString message) =>
        message.ToString(CultureInfo.InvariantCulture);

    /// <summary>Round-trips the verdict through its own serializer and back through the host-side
    /// reader, so what is validated is exactly the bytes that would be written.</summary>
    private static bool IsContractValid(JudgeVerdict verdict, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(JudgeJson.Canonical(verdict));
            return VerdictReader.TryRead(document.RootElement, out _, out error);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Rubrics
    // ---------------------------------------------------------------------

    /// <summary>Reads <c>rubrics/&lt;agent&gt;.json</c>, falling back to <c>rubrics/default.json</c>
    /// for an agent that has not been given a bespoke rubric yet.</summary>
    public (AgentRubric Rubric, string Source) LoadRubric(string agent)
    {
        var root = ResolveConfiguredPath(_judge.RubricRoot);
        var specific = Path.Combine(root, $"{agent}.json");
        var path = File.Exists(specific) ? specific : Path.Combine(root, $"{DefaultRubric}.json");
        if (!File.Exists(path))
            throw new ArgumentException($"No rubric for agent '{agent}' and no {DefaultRubric}.json under {_judge.RubricRoot}.");

        var rubric = JudgeJson.Read<AgentRubric>(path);
        var source = Path.GetFileNameWithoutExtension(path);
        if (rubric.Version != 1)
            throw new InvalidDataException($"Unsupported rubric version {rubric.Version} in {path}.");
        if (!string.Equals(rubric.Agent, source, StringComparison.Ordinal))
            throw new InvalidDataException($"Rubric '{rubric.Agent}' must be stored as {rubric.Agent}.json.");
        if (rubric.Criteria.Count == 0)
            throw new InvalidDataException($"Rubric '{rubric.Agent}' scores no criteria.");
        foreach (var criterion in rubric.Criteria)
            if (!RubricJudge.CheckKinds.Contains(criterion.Check, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"Rubric '{rubric.Agent}' criterion '{criterion.Name}' uses unknown check '{criterion.Check}'.");

        return (rubric, source);
    }

    // ---------------------------------------------------------------------
    // Baselines
    // ---------------------------------------------------------------------

    private string CompareBaseline(string agent, string fixture, JudgeVerdict? verdict)
    {
        if (verdict is null) return "not-compared";
        var path = BaselinePath(agent);
        if (!File.Exists(path)) return "missing";
        try
        {
            var baseline = JudgeJson.Read<JudgeBaseline>(path);
            if (baseline.Version != 1 ||
                !string.Equals(baseline.Agent, agent, StringComparison.Ordinal) ||
                !string.Equals(baseline.Judge, RubricJudge.Mode, StringComparison.Ordinal))
            {
                return "drift";
            }

            var recorded = baseline.Fixtures
                .FirstOrDefault(entry => string.Equals(entry.Fixture, fixture, StringComparison.Ordinal));
            if (recorded is null) return "missing";
            return string.Equals(
                JudgeJson.Canonical(recorded.Verdict),
                JudgeJson.Canonical(verdict),
                StringComparison.Ordinal)
                ? "match"
                : "drift";
        }
        catch
        {
            return "drift";
        }
    }

    /// <summary>Merges the judged fixtures into the agent's committed baseline, leaving entries for
    /// fixtures this run did not touch alone. Verdicts the contract gate rejected are not recorded.</summary>
    private void WriteBaseline(string agent, IEnumerable<JudgeFixtureResult> results)
    {
        var path = BaselinePath(agent);
        var entries = new Dictionary<string, JudgeVerdict>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                foreach (var entry in JudgeJson.Read<JudgeBaseline>(path).Fixtures)
                    entries[entry.Fixture] = entry.Verdict;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                entries.Clear(); // An unreadable baseline is replaced, not merged into.
            }
        }

        foreach (var result in results)
            if (result.Verdict is not null)
                entries[result.Fixture] = result.Verdict;

        var baseline = new JudgeBaseline(
            Version: 1,
            Agent: agent,
            Judge: RubricJudge.Mode,
            Fixtures: entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new JudgeBaselineEntry(entry.Key, entry.Value))
                .ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JudgeJson.Serialize(baseline));
    }

    /// <summary>Replaces the stale baseline check after <c>--update-baselines</c> rewrote the file.</summary>
    private static JudgeFixtureResult Restate(JudgeFixtureResult result)
    {
        if (result.Checks.All(check => check.Id != "judge.baseline")) return result;
        var checks = result.Checks
            .Select(check => check.Id == "judge.baseline" ? BaselineCheck(result.BaselineStatus) : check)
            .ToArray();
        return result with
        {
            Checks = checks,
            Status = checks.Any(check => check.Category == Integrity && check.Status == Error) ? Error : Pass,
        };
    }

    private static EvalCheckResult BaselineCheck(string status) => status switch
    {
        "match" => new EvalCheckResult("judge.baseline", Integrity, Pass, "The verdict matches the recorded baseline."),
        "not-compared" => new EvalCheckResult("judge.baseline", Integrity, Pass, "No baseline comparison was requested."),
        "missing" => new EvalCheckResult(
            "judge.baseline", Integrity, Error,
            "No baseline verdict is recorded for this fixture. Record one with --update-baselines and review the diff."),
        _ => new EvalCheckResult(
            "judge.baseline", Integrity, Error,
            "The verdict differs from the recorded baseline. Either the agent regressed or the baseline needs re-recording."),
    };

    // ---------------------------------------------------------------------
    // Paths
    // ---------------------------------------------------------------------

    private void WriteReport(JudgeReport report)
    {
        var directory = Path.Combine(
            ResolveConfiguredPath(_config.ArtifactRoot),
            _judge.ArtifactSubdirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{report.Agent}.json"), JudgeJson.Serialize(report));
    }

    public string ReportPath(string agent) => Path.Combine(
        ResolveConfiguredPath(_config.ArtifactRoot),
        _judge.ArtifactSubdirectory,
        $"{agent}.json");

    public string BaselinePath(string agent) => Path.Combine(
        ResolveConfiguredPath(_config.BaselineRoot),
        _judge.BaselineSubdirectory,
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

    private static void ValidateJudgeConfig(JudgeConfig config)
    {
        if (Path.IsPathRooted(config.RubricRoot))
            throw new InvalidDataException("Judge rubric root must be repository-relative.");
        foreach (var segment in new[] { config.ArtifactSubdirectory, config.BaselineSubdirectory })
        {
            if (segment.Length == 0 || segment.Contains('/') || segment.Contains('\\'))
                throw new InvalidDataException("Judge artifact and baseline subdirectories must be single path segments.");
        }
        if (config.ShipThresholdPercent is <= 0 or > 100)
            throw new InvalidDataException("Judge ship threshold must be a percentage above zero.");
        if (config.LlmTolerancePercent < 0)
            throw new InvalidDataException("Judge LLM tolerance must not be negative.");
    }
}

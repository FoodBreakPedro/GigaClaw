using System.Globalization;

namespace GigaClaw.Eval;

internal static class Program
{
    // Options that consume the following argument; anything else that is not "--" prefixed is a
    // positional (the verb, then the target).
    private static readonly string[] ValueOptions =
        ["--root", "--runs", "--max-runs", "--max-spend-usd"];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            return Usage();

        var positionals = Positionals(args);
        var verb = positionals.FirstOrDefault() switch
        {
            "replay" => "replay",
            "judge" => "judge",
            "montecarlo" or "monte-carlo" => "montecarlo",
            "static" => "static",
            _ => "static"
        };
        var target = positionals.FirstOrDefault() is "replay" or "judge" or "static" or "montecarlo" or "monte-carlo"
            ? positionals.Skip(1).FirstOrDefault()
            : positionals.FirstOrDefault();
        if (target is null)
            return Usage();

        var rootArgument = OptionValue(args, "--root");
        var root = FindRepositoryRoot(rootArgument ?? Directory.GetCurrentDirectory());
        var writeReport = !args.Contains("--no-report", StringComparer.Ordinal);

        try
        {
            return verb switch
            {
                "replay" => RunReplay(root, target, args, writeReport),
                "judge" => RunJudge(root, target, args, writeReport),
                "montecarlo" => RunMonteCarlo(root, target, args, writeReport),
                _ => RunStatic(root, target, args, writeReport),
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"eval: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"eval: {exception.Message}");
            return 1;
        }
    }

    private static int RunStatic(string root, string target, string[] args, bool writeReport)
    {
        var result = new StaticEvalRunner(root).Run(
            target,
            args.Contains("--strict", StringComparer.Ordinal),
            args.Contains("--update-baselines", StringComparer.Ordinal),
            writeReport);
        Print(result);
        return result.ExitCode;
    }

    private static int RunReplay(string root, string target, string[] args, bool writeReport)
    {
        var result = new ReplayRunner(root).Run(
            target,
            args.Contains("--real-cli", StringComparer.Ordinal),
            writeReport);
        Print(result);
        return result.ExitCode;
    }

    private static int RunJudge(string root, string target, string[] args, bool writeReport)
    {
        var runner = new JudgeRunner(root);
        // Regenerates the committed normalized-stream references (GigaClaw.Eval/baselines/
        // normalized-streams/), the bytes evidence[].ref/inputDigest hash. Unlike
        // --update-baselines this never touches a verdict, so it is safe (and meant) to run on any
        // platform whose bytes should be on record — the committed set here is macOS's.
        if (args.Contains("--update-streams", StringComparer.Ordinal))
            runner.WriteStreamBaselines(target);

        var result = runner.Run(
            target,
            args.Contains("--llm", StringComparer.Ordinal),
            args.Contains("--update-baselines", StringComparer.Ordinal),
            writeReport);
        Print(result, runner, root);
        return result.ExitCode;
    }

    private static int RunMonteCarlo(string root, string target, string[] args, bool writeReport)
    {
        // Refused rather than quietly accepted: Monte Carlo samples the agent run, and the judge is
        // the fixed instrument measuring it. Letting the non-reproducible LLM judge in would put
        // variance in the instrument too, and the resulting spread would be uninterpretable.
        if (args.Contains("--llm", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "montecarlo does not accept --llm: it samples the agent run and holds the judge fixed. " +
                "Use `judge --llm` to measure the judge instead.");
        }

        var runner = new MonteCarloRunner(root);
        var options = runner.DefaultOptions(
            IntOption(args, "--runs"),
            IntOption(args, "--max-runs"),
            DecimalOption(args, "--max-spend-usd"),
            args.Contains("--real-cli", StringComparer.Ordinal));

        var result = runner.Run(target, options, writeReport);
        Print(result);
        return result.ExitCode;
    }

    private static void Print(EvalRunResult result)
    {
        foreach (var agent in result.Report.Agents)
        {
            foreach (var check in agent.Checks.Where(check => check.Status != "pass"))
                Console.WriteLine(
                    $"{agent.Agent}: {check.Category}/{check.Status}: {check.Id}: {check.Message}");
            if (agent.BaselineStatus != "match")
                Console.WriteLine($"{agent.Agent}: error: baseline.{agent.BaselineStatus}");
        }

        Console.WriteLine(Summary(result.Report));
        Console.WriteLine($"Elapsed: {result.ElapsedMilliseconds} ms.");
    }

    // The summary must account for everything that drives the exit code, not just per-check
    // statuses: baseline.missing/baseline.drift are agent-level conditions (not checks), and
    // counting only checks once printed "0 error(s)" while exiting 1 — a summary that
    // misattributes is worse than none.
    internal static string Summary(EvalReport report)
    {
        var checks = report.Agents.SelectMany(agent => agent.Checks).ToArray();
        var missing = report.Agents.Count(agent => agent.BaselineStatus == "missing");
        var drift = report.Agents.Count(agent => agent.BaselineStatus == "drift");
        var baselineSegment = (missing, drift) switch
        {
            (0, 0) => "",
            (> 0, 0) => $", {missing} baseline error(s) (baseline.missing: {missing})",
            (0, > 0) => $", {drift} baseline error(s) (baseline.drift: {drift})",
            _ => $", {missing + drift} baseline error(s) " +
                 $"(baseline.missing: {missing}, baseline.drift: {drift})"
        };
        return
            $"Evaluated {report.Agents.Count} agent(s): " +
            $"{checks.Count(check => check.Status == "error")} error(s), " +
            $"{checks.Count(check => check.Status == "warning")} warning(s), " +
            $"{checks.Count(check => check.Status == "pass")} pass(es)" +
            baselineSegment + ".";
    }

    private static void Print(ReplayRunResult result)
    {
        var fixtures = result.Reports.SelectMany(report => report.Fixtures).ToArray();
        foreach (var report in result.Reports)
        {
            foreach (var fixture in report.Fixtures)
            {
                foreach (var check in fixture.Checks.Where(check => check.Status != "pass"))
                    Console.WriteLine(
                        $"{report.Agent}/{fixture.Fixture}: {check.Category}/{check.Status}: {check.Id}: {check.Message}");
            }
        }

        var mode = result.Reports.FirstOrDefault()?.Mode ?? "mock";
        Console.WriteLine(
            $"Replayed {fixtures.Length} fixture(s) across {result.Reports.Count} agent(s) in {mode} mode: " +
            $"{fixtures.Count(fixture => fixture.Status == "pass")} pass(es), " +
            $"{fixtures.Count(fixture => fixture.Status != "pass")} failure(s).");
        Console.WriteLine($"Elapsed: {result.ElapsedMilliseconds} ms.");
    }

    private static void Print(JudgeRunResult result, JudgeRunner runner, string root)
    {
        var fixtures = result.Reports.SelectMany(report => report.Fixtures).ToArray();
        foreach (var fixture in fixtures)
        {
            foreach (var check in fixture.Checks.Where(check => check.Status != "pass"))
                Console.WriteLine(
                    $"{fixture.Agent}/{fixture.Fixture}: {check.Category}/{check.Status}: {check.Id}: {check.Message}");
            if (fixture.Verdict is not null)
                Console.WriteLine(
                    $"{fixture.Agent}/{fixture.Fixture}: {fixture.Verdict.Verdict} " +
                    $"({RubricJudge.Percent(fixture.Verdict)}%) baseline={fixture.BaselineStatus}");
        }

        Console.WriteLine(
            $"Judged {fixtures.Length} fixture(s) across {result.Reports.Count} agent(s): " +
            $"{fixtures.Count(fixture => fixture.Status == "pass")} pass(es), " +
            $"{fixtures.Count(fixture => fixture.Status != "pass")} failure(s).");
        Console.WriteLine($"Elapsed: {result.ElapsedMilliseconds} ms.");

        // A bare "baseline.drift" names a symptom the same way it always has; a hash difference in
        // evidence[].ref/inputDigest never says WHERE. Print the exact bytes that hash for the
        // first drifting fixture, diffed against the macOS reference this repository commits under
        // GigaClaw.Eval/baselines/normalized-streams/, so a CI log alone localizes the difference —
        // this is the log a `continue-on-error` interrogation step leaves behind; its conclusion
        // always reads success, so the log has to be self-sufficient (doc/roadmap/SESSION-HANDOFF.md
        // § The Windows exemption).
        var drifted = fixtures.Where(fixture => fixture.BaselineStatus == "drift").ToArray();
        if (drifted.Length == 0) return;

        var first = drifted[0];
        Console.WriteLine();
        Console.WriteLine(
            $"=== normalized-stream evidence for {first.Agent}/{first.Fixture} " +
            $"(first of {drifted.Length} drifted fixture(s)) ===");
        Console.WriteLine(JudgeStreamEvidence.Describe(root, first.Fixture, runner.NormalizedStream(first.Fixture)));
    }

    private static void Print(MonteCarloRunResult result)
    {
        var fixtures = result.Reports.SelectMany(report => report.Fixtures).ToArray();
        foreach (var fixture in fixtures)
        {
            Console.WriteLine(
                $"{fixture.Agent}/{fixture.Fixture}: {fixture.DispatchedRuns} of {fixture.RequestedRuns} " +
                $"run(s) dispatched (cap {fixture.Cost.CapStatus}).");
            foreach (var run in fixture.Runs)
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  run {run.Run}: {run.Decision} {run.Percent}% · ${run.CostUsd}" +
                    $"{(run.CostReported ? "" : " (no cost reported)")} · {run.TotalTokens} token(s)"));
            // montecarlo.cost and montecarlo.statistics are printed in full below, so they are not
            // repeated here even when they carry a warning.
            foreach (var check in fixture.Checks.Where(check =>
                         check.Status != "pass" &&
                         check.Id is not ("montecarlo.cost" or "montecarlo.statistics")))
                Console.WriteLine($"  {check.Category}/{check.Status}: {check.Id}: {check.Message}");
            Console.WriteLine($"  cost: {fixture.Checks.Single(check => check.Id == "montecarlo.cost").Message}");
            Console.WriteLine($"  score: {fixture.Statistics.IntervalNote}");
        }

        var mode = result.Reports.FirstOrDefault()?.Mode ?? "mock+deterministic";
        var dispatched = fixtures.Sum(fixture => fixture.DispatchedRuns);
        var spent = fixtures.Sum(fixture => fixture.Cost.TotalUsd);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Sampled {fixtures.Length} fixture(s) across {result.Reports.Count} agent(s) in {mode} mode: " +
            $"{dispatched} run(s) dispatched, ${spent} spent in total."));
        Console.WriteLine($"Elapsed: {result.ElapsedMilliseconds} ms.");
    }

    private static string[] Positionals(string[] args)
    {
        var positionals = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (ValueOptions.Contains(args[index], StringComparer.Ordinal))
            {
                index++;
                continue;
            }
            if (args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            positionals.Add(args[index]);
        }
        return [.. positionals];
    }

    private static string? OptionValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }

    private static int? IntOption(string[] args, string option)
    {
        var value = OptionValue(args, option);
        if (value is null) return null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{option} expects a whole number, got '{value}'.");
        return parsed;
    }

    private static decimal? DecimalOption(string[] args, string option)
    {
        var value = OptionValue(args, option);
        if (value is null) return null;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{option} expects a number of US dollars, got '{value}'.");
        return parsed;
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(start));
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "catalog.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "ProjectTemplate", "Agents")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not find a repository root containing catalog.json and ProjectTemplate/Agents.");
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: GigaClaw.Eval [static] <agent|all> [--strict] [--update-baselines] [--no-report] [--root PATH]");
        Console.Error.WriteLine(
            "       GigaClaw.Eval replay <fixture|family|agent|all> [--real-cli] [--no-report] [--root PATH]");
        Console.Error.WriteLine(
            "       GigaClaw.Eval judge  <fixture|family|agent|all> [--llm] [--update-baselines] " +
            "[--update-streams] [--no-report] [--root PATH]");
        Console.Error.WriteLine(
            "       GigaClaw.Eval montecarlo <fixture|family|agent|all> [--runs N] [--max-runs N] " +
            "[--max-spend-usd USD] [--real-cli] [--no-report] [--root PATH]");
        return 2;
    }
}

namespace GigaClaw.Eval;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            return Usage();

        var target = args.FirstOrDefault(argument =>
            !argument.StartsWith("--", StringComparison.Ordinal));
        if (target is null)
            return Usage();
        var rootArgument = OptionValue(args, "--root");
        var root = FindRepositoryRoot(rootArgument ?? Directory.GetCurrentDirectory());
        var strict = args.Contains("--strict", StringComparer.Ordinal);
        var updateBaselines = args.Contains("--update-baselines", StringComparer.Ordinal);
        var writeReport = !args.Contains("--no-report", StringComparer.Ordinal);

        try
        {
            var result = new StaticEvalRunner(root).Run(
                target,
                strict,
                updateBaselines,
                writeReport);
            Print(result);
            return result.ExitCode;
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

        var checks = result.Report.Agents.SelectMany(agent => agent.Checks).ToArray();
        Console.WriteLine(
            $"Evaluated {result.Report.Agents.Count} agent(s): " +
            $"{checks.Count(check => check.Status == "error")} error(s), " +
            $"{checks.Count(check => check.Status == "warning")} warning(s), " +
            $"{checks.Count(check => check.Status == "pass")} pass(es).");
        Console.WriteLine($"Elapsed: {result.ElapsedMilliseconds} ms.");
    }

    private static string? OptionValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
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
            "Usage: GigaClaw.Eval <agent|all> [--strict] [--update-baselines] [--no-report] [--root PATH]");
        return 2;
    }
}

using System.Diagnostics;

namespace GigaClaw.Core.Tests.Helpers;

/// <summary>
/// Runs the contract validators that ship to workspaces under <c>.agents/scripts/</c>. They are
/// the single implementation of the verdict and handoff rules, so the tests exercise them rather
/// than re-implementing the rules in C#.
/// </summary>
public static class PythonContractRunner
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ScriptsDir { get; } =
        Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents", "scripts");

    public static string FixturesDir(string name) =>
        Path.Combine(RepositoryRoot, "GigaClaw.Core.Tests", "Fixtures", name);

    public static (int ExitCode, string Output) RunScript(string scriptName, params string[] arguments)
        => Run(Path.Combine(ScriptsDir, scriptName), arguments);

    private static (int ExitCode, string Output) Run(string script, string[] arguments)
    {
        var (executable, prefix) = Interpreter.Value;
        return Execute(executable, [.. prefix, script, .. arguments])
            ?? throw new InvalidOperationException($"Could not start '{executable}' to run {Path.GetFileName(script)}.");
    }

    /// <summary>
    /// Resolved once: the Windows "python" launcher stub answers to the name without being an
    /// interpreter, so each candidate is probed rather than assumed.
    /// </summary>
    private static readonly Lazy<(string Executable, string[] Prefix)> Interpreter = new(() =>
    {
        (string Executable, string[] Prefix)[] candidates =
        [
            ("python3", []),
            ("python", []),
            ("py", ["-3"]),
        ];

        foreach (var candidate in candidates)
        {
            var probe = Execute(
                candidate.Executable,
                [.. candidate.Prefix, "-c", "import sys; print(sys.version_info[0])"]);
            if (probe is { ExitCode: 0 } result && result.Output.Trim().StartsWith('3'))
                return candidate;
        }

        throw new InvalidOperationException(
            "Python 3 was not found (tried python3, python, py -3). The template's contract " +
            "enforcement layer is Python, so it is a prerequisite for running the test suite.");
    });

    private static (int ExitCode, string Output)? Execute(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception)
        {
            return null; // Interpreter not installed under this name.
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GigaClaw repository root.");
    }
}

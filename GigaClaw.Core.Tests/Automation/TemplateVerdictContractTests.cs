using System.Diagnostics;
using System.Text.Json;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Guards the frozen v1 verdict contract shipped in ProjectTemplate: the schema every
/// reviewer, gate and eval judge shares, and the Python validator that enforces it. The
/// validator is the single implementation - these tests run it against committed fixtures
/// rather than re-implementing the rules in C#.
/// </summary>
public class TemplateVerdictContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptsDir = Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents", "scripts");
    private static readonly string FixturesDir = Path.Combine(RepositoryRoot, "GigaClaw.Core.Tests", "Fixtures", "verdicts");
    private static readonly string Validator = Path.Combine(ScriptsDir, "verdict_contract.py");

    [Fact]
    public void Schema_is_frozen_at_v1_with_the_agreed_field_set()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ScriptsDir, "verdict.schema.json")));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var field in new[]
                 {
                     "schemaVersion", "agent", "ticketId", "verdict",
                     "categories", "vetoItems", "evidence", "reviewedAtUtc", "inputDigest",
                 })
        {
            Assert.Contains(field, required);
        }

        // Unknown fields must not slip through silently: the contract fails closed.
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var verdicts = root.GetProperty("properties").GetProperty("verdict").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "SHIP", "FIX", "BLOCK" }, verdicts);
    }

    [Fact]
    public void Validator_self_test_passes()
    {
        var (exitCode, output) = RunValidator("--self-test");
        Assert.True(exitCode == 0, $"verdict_contract.py --self-test failed:{Environment.NewLine}{output}");
    }

    [Fact]
    public void Every_reviewer_worked_example_validates()
    {
        var fixtures = Directory.GetFiles(Path.Combine(FixturesDir, "valid"), "*.json").OrderBy(p => p).ToArray();

        // One worked example per reviewer that gates a pipeline (A11/P8).
        Assert.Equal(5, fixtures.Length);

        foreach (var fixture in fixtures)
        {
            var (exitCode, output) = RunValidator(fixture);
            Assert.True(exitCode == 0, $"{Path.GetFileName(fixture)} should be a valid verdict:{Environment.NewLine}{output}");
        }
    }

    [Fact]
    public void Every_malformed_verdict_is_rejected()
    {
        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "invalid"), "*.json"))
        {
            var (exitCode, output) = RunValidator(fixture);
            Assert.True(exitCode == 1, $"{Path.GetFileName(fixture)} should have been rejected but exited {exitCode}:{Environment.NewLine}{output}");
        }
    }

    [Fact]
    public void Stale_verdicts_are_rejected_against_the_reviewed_artifact()
    {
        var fixture = Path.Combine(FixturesDir, "valid", "blog-reviewer-ship.json");
        var currentDigest = JsonDocument.Parse(File.ReadAllText(fixture))
            .RootElement.GetProperty("inputDigest").GetString()!;

        Assert.Equal(0, RunValidator(fixture, "--expect-digest", currentDigest).ExitCode);

        var (exitCode, output) = RunValidator(fixture, "--expect-digest", "sha256:" + new string('b', 64));
        Assert.True(exitCode == 1, "a verdict bound to a different artifact must be rejected as stale");
        Assert.Contains("stale verdict", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Comment_transport_round_trips_and_self_disagreement_is_rejected()
    {
        var accepted = RunValidator(Path.Combine(FixturesDir, "transport", "qa-tester-comment.md"), "--extract");
        Assert.True(accepted.ExitCode == 0, $"verdict comment should validate:{Environment.NewLine}{accepted.Output}");
        Assert.Contains("BLOCK by qa-tester", accepted.Output, StringComparison.Ordinal);

        var rejected = RunValidator(Path.Combine(FixturesDir, "transport", "marker-body-mismatch.md"), "--extract");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("marker line says", rejected.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_contract_delegates_verdict_validation_to_the_shared_validator()
    {
        // content_contract.py is the enforcement point agents already call; --verdict must
        // reach the same implementation rather than grow a second one.
        var (exitCode, output) = Run(
            Path.Combine(ScriptsDir, "content_contract.py"),
            "--verdict", Path.Combine(FixturesDir, "invalid", "ship-with-veto-item.json"));

        Assert.Equal(1, exitCode);
        Assert.Contains("forbids SHIP", output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunValidator(params string[] arguments)
        => Run(Validator, arguments);

    private static (int ExitCode, string Output) Run(string script, params string[] arguments)
    {
        var (executable, prefix) = Interpreter.Value;
        var invocation = prefix.Append(script).Concat(arguments).ToArray();
        return Execute(executable, invocation)
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
                candidate.Prefix.Append("-c").Append("import sys; print(sys.version_info[0])").ToArray());
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

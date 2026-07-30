using System.Text.Json;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Guards the frozen v1 verdict contract shipped in ProjectTemplate: the schema every
/// reviewer, gate and eval judge shares, and the Python validator that enforces it. The
/// validator is the single implementation - these tests run it against committed fixtures
/// rather than re-implementing the rules in C#.
/// </summary>
public class TemplateVerdictContractTests
{
    private static readonly string ScriptsDir = PythonContractRunner.ScriptsDir;
    private static readonly string FixturesDir = PythonContractRunner.FixturesDir("verdicts");

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

        // One worked example per gated *pipeline*, not per reviewer. blog-reviewer serves two:
        // the file-based path and the AD-7 content path, whose draft is the ticket description and
        // whose verdict therefore cites hash evidence only. Counting reviewers instead of pipelines
        // is what let the AD-7 path ship without a typed verdict at all.
        Assert.Equal(6, fixtures.Length);

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
        => PythonContractRunner.RunScript("verdict_contract.py", arguments);

    private static (int ExitCode, string Output) Run(string script, params string[] arguments)
        => PythonContractRunner.RunScript(Path.GetFileName(script), arguments);
}

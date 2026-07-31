using System.Text.Json;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Guards the frozen v1 handoff contract shipped in ProjectTemplate and keeps the host-side
/// reader honest against the Python validator that agents actually call.
/// </summary>
public class TemplateHandoffContractTests
{
    private static readonly string FixturesDir = PythonContractRunner.FixturesDir("handoffs");

    [Fact]
    public void Schema_is_frozen_at_v1_with_the_agreed_field_set()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(PythonContractRunner.ScriptsDir, "handoff.schema.json")));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var field in new[]
                 {
                     "schemaVersion", "agent", "ticketId", "runId", "summary",
                     "outputs", "ownedFiles", "assumptions", "openLoops", "acceptanceCriteria", "producedAtUtc",
                 })
        {
            Assert.Contains(field, required);
        }
    }

    [Fact]
    public void Validator_self_test_passes()
    {
        var (exitCode, output) = PythonContractRunner.RunScript("handoff_contract.py", "--self-test");
        Assert.True(exitCode == 0, $"handoff_contract.py --self-test failed:{Environment.NewLine}{output}");
    }

    [Fact]
    public void Committed_fixtures_are_classified_by_the_shared_validator()
    {
        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "valid"), "*.json"))
        {
            var (exitCode, output) = PythonContractRunner.RunScript("handoff_contract.py", fixture);
            Assert.True(exitCode == 0, $"{Path.GetFileName(fixture)} should be valid:{Environment.NewLine}{output}");
        }

        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "invalid"), "*.json"))
        {
            var (exitCode, output) = PythonContractRunner.RunScript("handoff_contract.py", fixture);
            Assert.True(exitCode == 1, $"{Path.GetFileName(fixture)} should have been rejected but exited {exitCode}:{Environment.NewLine}{output}");
        }
    }

    [Fact]
    public void Host_reader_and_shipped_validator_agree_on_owned_file_discipline()
    {
        // Where the two implementations must not diverge: the lease layer consumes ownedFiles
        // from the host reader, while agents are policed by the Python validator.
        foreach (var fixture in Directory.GetFiles(Path.Combine(FixturesDir, "valid"), "*.json"))
        {
            var element = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(fixture));
            Assert.True(
                HandoffReader.TryRead(element, out _, out var error),
                $"{Path.GetFileName(fixture)} is accepted by handoff_contract.py but rejected here: {error}");
        }

        foreach (var name in new[] { "owned-file-absolute", "owned-file-escapes-workspace", "unknown-property" })
        {
            var element = JsonSerializer.Deserialize<JsonElement>(
                File.ReadAllText(Path.Combine(FixturesDir, "invalid", $"{name}.json")));
            Assert.False(
                HandoffReader.TryRead(element, out _, out _),
                $"{name} is rejected by handoff_contract.py but accepted here");
        }
    }

    [Fact]
    public void Shared_schema_engine_backs_both_contracts()
    {
        // schema_check.py is the single subset evaluator; if it breaks, both contracts do.
        foreach (var script in new[] { "verdict_contract.py", "handoff_contract.py" })
        {
            var source = File.ReadAllText(Path.Combine(PythonContractRunner.ScriptsDir, script));
            Assert.Contains("from schema_check import", source, StringComparison.Ordinal);
        }
    }
}

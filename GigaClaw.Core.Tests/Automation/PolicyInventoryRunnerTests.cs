using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

public class PolicyInventoryRunnerTests
{
    private static readonly string PolicyDir =
        Path.Combine(RepositoryRoot(), "GigaClaw.Core", "Automation", "Policy");

    [Fact]
    public void Inventory_jsonl_contains_all_33_exercised_template_agents()
    {
        var inventoryPath = Path.Combine(PolicyDir, "sp1-glob-failure-inventory.jsonl");
        Assert.True(File.Exists(inventoryPath), $"Inventory file missing at '{inventoryPath}'");

        var lines = File.ReadAllLines(inventoryPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        Assert.Equal(33, lines.Count);

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("agent", out var agentProp));
            Assert.False(string.IsNullOrWhiteSpace(agentProp.GetString()));

            Assert.True(root.TryGetProperty("exerciseState", out var stateProp));
            Assert.Equal("exercised", stateProp.GetString());

            Assert.True(root.TryGetProperty("observedViolationCount", out var violationProp));
            Assert.True(violationProp.ValueKind == JsonValueKind.Number);
        }
    }

    [Fact]
    public void SP1_review_document_exists_and_describes_policy_signoff()
    {
        var reviewPath = Path.Combine(PolicyDir, "SP1-REVIEW.md");
        Assert.True(File.Exists(reviewPath), $"SP1-REVIEW.md missing at '{reviewPath}'");

        var content = File.ReadAllText(reviewPath);
        Assert.Contains("SP-1 Policy Enforcement Review Sheet", content);
        Assert.Contains("programmer", content);
        Assert.Contains("code-janitor", content);
        Assert.Contains("approval-gatekeeper", content);
        Assert.Contains("Flip to Block", content);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "ProjectTemplate", "Agents"))) return directory.FullName;
        throw new DirectoryNotFoundException("Test repository root not found.");
    }
}

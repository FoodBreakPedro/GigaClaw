using GigaClaw.Core.Automation;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

public class ClaudeRunnerAgentContractTests
{
    [Fact]
    public async Task Missing_manifest_is_backward_compatible()
    {
        using var tmp = new TempDir();

        var contract = await ClaudeRunner.LoadAgentContractAsync(
            tmp.Path, "programmer", CancellationToken.None);

        Assert.Null(contract);
    }

    [Fact]
    public async Task Loader_injects_only_current_agents_compact_contract()
    {
        using var tmp = new TempDir();
        var agentsDir = Path.Combine(tmp.Path, ".agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(Path.Combine(agentsDir, "contracts.json"), """
            {
              "version": 1,
              "defaults": { "maxAttempts": 3 },
              "agents": {
                "programmer": { "writes": ["src/**"] },
                "reviewer": { "writes": [], "maxAttempts": 1 }
              }
            }
            """);

        var contract = await ClaudeRunner.LoadAgentContractAsync(
            tmp.Path, "programmer", CancellationToken.None);

        Assert.NotNull(contract);
        Assert.Contains("""agentSlug":"programmer""", contract);
        Assert.Contains("""version":1""", contract);
        Assert.Contains("""defaults":{"maxAttempts":3}""", contract);
        Assert.Contains("""agent":{"writes":["src/**"]}""", contract);
        Assert.DoesNotContain("reviewer", contract);
    }

    [Fact]
    public async Task Preamble_places_contract_after_shared_text()
    {
        using var tmp = new TempDir();
        var agentsDir = Path.Combine(tmp.Path, ".agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(Path.Combine(agentsDir, "preamble.md"), "COMMON {agent}");
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "contracts.json"),
            """{"programmer":{"terminalStates":["Done","Blocked"]}}""");
        var ctx = new ClaudeRunContext
        {
            ProjectSlug = "p",
            WorkspacePath = tmp.Path,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
        };

        var preamble = await ClaudeRunner.BuildPreambleAsync(ctx, CancellationToken.None);

        var sharedAt = preamble.IndexOf("COMMON programmer", StringComparison.Ordinal);
        var contractAt = preamble.IndexOf("[Agent contract]", StringComparison.Ordinal);
        Assert.True(sharedAt >= 0);
        Assert.True(contractAt > sharedAt);
        Assert.Contains("""{"terminalStates":["Done","Blocked"]}""", preamble);
    }

    [Fact]
    public async Task Malformed_present_manifest_fails_closed()
    {
        using var tmp = new TempDir();
        var agentsDir = Path.Combine(tmp.Path, ".agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(Path.Combine(agentsDir, "contracts.json"), "{not-json");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ClaudeRunner.LoadAgentContractAsync(tmp.Path, "programmer", CancellationToken.None));
    }
}

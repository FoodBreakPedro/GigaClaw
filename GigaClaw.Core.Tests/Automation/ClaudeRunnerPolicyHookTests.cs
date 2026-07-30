using System.Text.Json;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public class ClaudeRunnerPolicyHookTests
{
    [Fact]
    public async Task Mock_invokes_generated_settings_and_shadow_violation_is_persisted()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("policy-shadow");
        var workspace = projects.ResolveWorkspacePath(project);
        await CreateContractAsync(workspace);
        var scenarioDir = await CreateScenarioAsync(
            tmp.Path,
            "outside-write",
            """
            {"_hook":{"tool_name":"Write","tool_use_id":"toolu_outside","tool_input":{"file_path":"doc/outside.md"}}}
            """);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "outside-write");

        var store = new RunLogStore(Path.Combine(tmp.Path, "run-data"));
        var runs = new AgentRunRegistry(store);
        var runner = new ClaudeRunner(
            new SessionRegistry(),
            runs,
            new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);
        var run = await runner.RunAsync(
            NewContext(project.Slug, workspace, scenarioDir, "outside-write"),
            CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
        var violation = Assert.Single(
            run.SnapshotBuffer(),
            item => item.Kind == "policy-violation");
        using (var detail = JsonDocument.Parse(violation.Detail!))
        {
            Assert.Equal(
                "policy-violation/v1",
                detail.RootElement.GetProperty("schema").GetString());
            Assert.Equal("programmer", detail.RootElement.GetProperty("agent").GetString());
            Assert.Equal("Write", detail.RootElement.GetProperty("tool").GetString());
            Assert.Equal("doc/outside.md", detail.RootElement.GetProperty("target").GetString());
            Assert.Equal("Warn", detail.RootElement.GetProperty("decision").GetString());
            Assert.Equal("warn", detail.RootElement.GetProperty("enforcementMode").GetString());
            Assert.Equal("run-log", detail.RootElement.GetProperty("persistence").GetString());
        }

        var reloaded = new AgentRunRegistry(store)
            .AllForProject(project.Slug)
            .Single(item => item.RunId == run.RunId);
        Assert.Single(
            reloaded.SnapshotBuffer(),
            item => item.Kind == "policy-violation");
    }

    [Fact]
    public async Task Acknowledged_run_with_no_violations_adds_no_policy_noise()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("policy-clean");
        var workspace = projects.ResolveWorkspacePath(project);
        await CreateContractAsync(workspace);
        var scenarioDir = await CreateScenarioAsync(
            tmp.Path,
            "inside-write",
            """
            {"_hook":{"tool_name":"Edit","tool_input":{"file_path":"src/Program.cs"}}}
            """);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "inside-write");
        var runner = new ClaudeRunner(
            new SessionRegistry(),
            new AgentRunRegistry(),
            new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        var run = await runner.RunAsync(
            NewContext(project.Slug, workspace, scenarioDir, "inside-write"),
            CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.DoesNotContain(
            run.SnapshotBuffer(),
            item => item.Kind == "policy-violation");
        Assert.DoesNotContain(
            run.SnapshotBuffer(),
            item => item.Kind == "policy");
    }

    [Fact]
    public async Task Ignored_settings_fail_run_visibly_instead_of_failing_open()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("policy-unacknowledged");
        var workspace = projects.ResolveWorkspacePath(project);
        await CreateContractAsync(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");
        var runner = new ClaudeRunner(
            new SessionRegistry(),
            new AgentRunRegistry(),
            new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);
        var context = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
            MaxTurns = 1,
            Env = new Dictionary<string, string>
            {
                ["GIGACLAW_MOCK_IGNORE_POLICY_SETTINGS"] = "1",
            },
        };

        var run = await runner.RunAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(-1, run.ExitCode);
        Assert.Contains(
            run.SnapshotBuffer(),
            item => item.Kind == "error" &&
                    item.Text.Contains(
                        "did not acknowledge",
                        StringComparison.OrdinalIgnoreCase));
    }

    private static ClaudeRunContext NewContext(
        string projectSlug,
        string workspace,
        string scenarioDir,
        string scenario) =>
        new()
        {
            ProjectSlug = projectSlug,
            WorkspacePath = workspace,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
            MaxTurns = 1,
            Env = new Dictionary<string, string>
            {
                ["GIGACLAW_MOCK_SCENARIOS_DIR"] = scenarioDir,
                ["GIGACLAW_MOCK_SCENARIO"] = scenario,
                ["GIGACLAW_MOCK_REQUIRE_POLICY_SETTINGS"] = "1",
            },
        };

    private static async Task<string> CreateScenarioAsync(
        string root,
        string name,
        string hookLine)
    {
        var directory = Path.Combine(root, "policy-scenarios");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{name}.ndjson"),
            string.Join(
                '\n',
                """{"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}""",
                hookLine,
                """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""",
                """{"type":"result","subtype":"success","is_error":false,"duration_ms":5,"num_turns":1}""",
                """{"_meta":{"exit":0}}"""));
        return directory;
    }

    private static async Task CreateContractAsync(string workspace)
    {
        var agents = Path.Combine(workspace, ".agents");
        Directory.CreateDirectory(agents);
        await File.WriteAllTextAsync(
            Path.Combine(agents, "contracts.json"),
            """
            {
              "version": 1,
              "defaults": {
                "maxDispatchAttempts": 3,
                "retryBackoffSeconds": 300,
                "requireAtomicHandoff": true,
                "requireAuthorOnBoardWrites": true
              },
              "agents": {
                "programmer": {
                  "dispatches": ["assignment"],
                  "riskClass": "code-write",
                  "allowedWriteGlobs": ["src/**"],
                  "ticketExit": ["Review", "Blocked"]
                }
              }
            }
            """);
    }
}

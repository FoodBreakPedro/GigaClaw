using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Tests.Helpers;
using Xunit.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

public class ClaudeHookSettingsTests
{
    [Fact]
    public void Generated_settings_round_trip_through_strict_schema()
    {
        var endpoint = new Uri("http://127.0.0.1:43123/policy/token");
        var json = ClaudeHookSettings.Serialize(endpoint);
        using var document = JsonDocument.Parse(json);

        var parsed = ClaudeHookSettings.Validate(document.RootElement);

        Assert.Equal(endpoint, parsed);
        Assert.Contains("\"UserPromptSubmit\"", json);
        Assert.Contains(ClaudeHookSettings.PreToolUseMatcher, json);
        Assert.Contains("\"type\":\"http\"", json);
        Assert.Contains("\"timeout\":5", json);
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Validator_rejects_malformed_or_expanded_settings(
        string json,
        string diagnostic)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<InvalidDataException>(
            () => ClaudeHookSettings.Validate(document.RootElement));

        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> InvalidSettings => new()
    {
        { """{}""", "hooks" },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}],"PreToolUse":[]}}""",
            "exactly 1"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}],"PreToolUse":[{"matcher":"Write","hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}]}}""",
            "matcher"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}],"PreToolUse":[{"matcher":"Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch","hooks":[{"type":"command","url":"http://127.0.0.1:1234/p","timeout":5}]}]}}""",
            "type"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"https://example.test/p","timeout":5}]}],"PreToolUse":[{"matcher":"Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch","hooks":[{"type":"http","url":"https://example.test/p","timeout":5}]}]}}""",
            "loopback"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":0}]}],"PreToolUse":[{"matcher":"Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch","hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":0}]}]}}""",
            "timeout"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}],"PreToolUse":[{"matcher":"Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch","hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5,"extra":true}]}]}}""",
            "extra"
        },
        {
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}],"PreToolUse":[{"matcher":"Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch","hooks":[{"type":"http","url":"http://127.0.0.1:1234/p","timeout":5}]}]},"permissions":{}}""",
            "permissions"
        },
    };
}

public class PolicyHookTransportTests
{
    [Fact]
    public async Task Probe_acknowledges_settings_without_creating_noise()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);

        using var client = NewClient();
        var response = await PostAcknowledgementAsync(client, transport.Endpoint);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.True(transport.WasAcknowledged);
        Assert.Empty(transport.SnapshotObservations());
    }

    [Fact]
    public async Task File_write_violation_is_observed_but_http_response_always_allows()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);

        using var client = NewClient();
        var response = await PostHookAsync(
            client,
            transport.Endpoint,
            "Write",
            new { file_path = "doc/outside.md" },
            "toolu_write");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var violation = Assert.Single(transport.SnapshotObservations());
        Assert.Equal("programmer", violation.Agent);
        Assert.Equal("Write", violation.Tool);
        Assert.Equal("toolu_write", violation.ToolUseId);
        Assert.Equal(PolicyToolOperation.FileWrite, violation.Operation);
        Assert.Equal("doc/outside.md", violation.Target);
        Assert.Equal(PolicyDecisionKind.Warn, violation.Decision);
        Assert.Contains("outside allowedWriteGlobs", violation.Reason);
    }

    [Fact]
    public async Task Allowed_write_acknowledges_without_empty_violation_observation()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);

        using var client = NewClient();
        var response = await PostHookAsync(
            client,
            transport.Endpoint,
            "Edit",
            new { file_path = "src/Program.cs" });

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(transport.WasAcknowledged);
        Assert.Empty(transport.SnapshotObservations());
    }

    [Fact]
    public async Task Bash_capability_adapter_observes_git_network_and_file_violations()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);

        using var client = NewClient();
        var response = await PostHookAsync(
            client,
            transport.Endpoint,
            "Bash",
            new
            {
                command =
                    "git commit -m test; curl https://example.test/data; echo hi > outside.txt",
            });

        Assert.True(response.IsSuccessStatusCode);
        var observations = transport.SnapshotObservations();
        Assert.Equal(3, observations.Count);
        Assert.Contains(observations, item => item.Operation == PolicyToolOperation.GitWrite);
        Assert.Contains(observations, item => item.Operation == PolicyToolOperation.Network);
        Assert.Contains(
            observations,
            item => item.Operation == PolicyToolOperation.FileWrite &&
                    item.Target == "outside.txt");
    }

    [Theory]
    [MemberData(nameof(ConservativeBashCases))]
    public async Task Bash_adapter_conservatively_classifies_wrapped_and_indirect_capabilities(
        string command,
        PolicyToolOperation expectedOperation)
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);
        using var client = NewClient();

        var response = await PostHookAsync(
            client,
            transport.Endpoint,
            "Bash",
            new { command });

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains(
            transport.SnapshotObservations(),
            observation => observation.Operation == expectedOperation);
    }

    public static TheoryData<string, PolicyToolOperation> ConservativeBashCases => new()
    {
        { "cp src/a.cs outside.cs", PolicyToolOperation.FileWrite },
        { "mv src/a.cs outside.cs", PolicyToolOperation.FileWrite },
        { "install src/tool outside-tool", PolicyToolOperation.FileWrite },
        { "sed -i 's/a/b/' outside.txt", PolicyToolOperation.FileWrite },
        { "wget https://example.test/archive.tgz", PolicyToolOperation.Network },
        { "ssh deploy@example.test uptime", PolicyToolOperation.Network },
        { "npm install package", PolicyToolOperation.Network },
        { "pnpm add package", PolicyToolOperation.Network },
        { "pip install package", PolicyToolOperation.Network },
        { "dotnet restore", PolicyToolOperation.Network },
        { "curl \"$REMOTE_URL\"", PolicyToolOperation.Network },
        {
            "env CI=1 sudo /usr/bin/git -c user.name=test commit -m test",
            PolicyToolOperation.GitWrite
        },
        { "sudo /opt/homebrew/bin/git add src/a.cs", PolicyToolOperation.GitWrite },
        { "PATH=/usr/bin /usr/bin/git commit -m test", PolicyToolOperation.GitWrite },
        { "echo inspection-required", PolicyToolOperation.Bash },
    };

    [Fact]
    public async Task Malformed_hook_input_is_a_block_observation_not_a_transport_denial()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);
        using var client = NewClient();
        using var content = new StringContent(
            """{"hook_event_name":"PreToolUse","tool_name":"Write"}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(transport.Endpoint, content);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var violation = Assert.Single(transport.SnapshotObservations());
        Assert.Equal(PolicyDecisionKind.Block, violation.Decision);
        Assert.Equal(PolicyToolOperation.HookTransport, violation.Operation);
        Assert.Contains("Malformed policy hook", violation.Reason);
    }

    [Fact]
    public async Task Random_path_rejects_unauthenticated_requests()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);
        using var client = NewClient();
        var wrong = new UriBuilder(transport.Endpoint) { Path = "/wrong" }.Uri;

        var response = await PostHookAsync(client, wrong, "PolicyProbe", new { });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(transport.WasAcknowledged);
        Assert.Empty(transport.SnapshotObservations());
    }

    internal static HttpClient NewClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    internal static async Task<HttpResponseMessage> PostHookAsync(
        HttpClient client,
        Uri endpoint,
        string toolName,
        object toolInput,
        string toolUseId = "toolu_test")
    {
        return await client.PostAsJsonAsync(endpoint, new
        {
            session_id = "session-test",
            cwd = "/workspace",
            hook_event_name = "PreToolUse",
            tool_name = toolName,
            tool_input = toolInput,
            tool_use_id = toolUseId,
        });
    }

    internal static async Task<HttpResponseMessage> PostAcknowledgementAsync(
        HttpClient client,
        Uri endpoint)
    {
        return await client.PostAsJsonAsync(endpoint, new
        {
            session_id = "session-test",
            cwd = "/workspace",
            hook_event_name = "UserPromptSubmit",
            prompt = "test",
        });
    }

    internal static async Task<ContractPolicy> LoadPolicyAsync(
        string workspacePath,
        string[] globs)
    {
        var manifest = Path.Combine(workspacePath, "contracts.json");
        await File.WriteAllTextAsync(
            manifest,
            $$"""
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
                    "allowedWriteGlobs": {{JsonSerializer.Serialize(globs)}},
                    "ticketExit": ["Review", "Blocked"]
                  }
                }
              }
              """);
        var policy = await ContractPolicyLoader.LoadManifestAsync(
            manifest,
            workspacePath,
            "programmer",
            caseSensitivity: PathCaseSensitivity.Sensitive);
        Assert.True(policy.IsValid, policy.Diagnostic);
        return policy;
    }
}

/// <summary>
/// Reproducible transport benchmark. Run with:
/// dotnet test GigaClaw.Core.Tests/GigaClaw.Core.Tests.csproj -c Release
///   --filter FullyQualifiedName~PolicyHookTransportBenchmarkTests
///   --logger "console;verbosity=detailed"
/// </summary>
public class PolicyHookTransportBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PolicyHookTransportBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Warm_loopback_transport_p95_is_within_shadow_target()
    {
        const int warmupSamples = 50;
        const int measuredSamples = 500;
        using var tmp = new TempDir();
        var policy = await PolicyHookTransportTests.LoadPolicyAsync(tmp.Path, ["src/**"]);
        await using var transport = PolicyHookTransport.Start(policy);
        using var client = PolicyHookTransportTests.NewClient();

        for (var i = 0; i < warmupSamples; i++)
        {
            using var response = await PolicyHookTransportTests.PostHookAsync(
                client,
                transport.Endpoint,
                "Edit",
                new { file_path = "src/Program.cs" });
            response.EnsureSuccessStatusCode();
            _ = await response.Content.ReadAsStringAsync();
        }

        var samples = new double[measuredSamples];
        for (var i = 0; i < measuredSamples; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await PolicyHookTransportTests.PostHookAsync(
                client,
                transport.Endpoint,
                "Edit",
                new { file_path = "src/Program.cs" });
            response.EnsureSuccessStatusCode();
            _ = await response.Content.ReadAsStringAsync();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        var p50 = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        _output.WriteLine(
            $"transport=loopback-http warmup={warmupSamples} samples={measuredSamples} " +
            $"p50_ms={p50:F3} p95_ms={p95:F3} target_p95_ms=50.000");

        Assert.True(
            p95 <= 50,
            $"Warm policy-hook p95 {p95:F3} ms exceeded the 50 ms shadow target.");
        Assert.Empty(transport.SnapshotObservations());
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

public class PolicyInventoryArtifactTests
{
    [Fact]
    public void Sp1_inventory_has_one_sorted_not_exercised_row_per_template_agent()
    {
        var root = FindRepositoryRoot();
        var agentsDirectory = Path.Combine(root, "ProjectTemplate", "Agents");
        var inventoryPath = Path.Combine(
            root,
            "GigaClaw.Core",
            "Automation",
            "Policy",
            "sp1-glob-failure-inventory.jsonl");
        var lines = File.ReadAllLines(inventoryPath);
        using var contracts = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(agentsDirectory, "contracts.json")));
        var contractAgents = contracts.RootElement
            .GetProperty("agents")
            .EnumerateObject()
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var skillAgents = Directory
            .EnumerateDirectories(agentsDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => new DirectoryInfo(directory).Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(33, lines.Length);
        Assert.Equal(skillAgents, contractAgents.Select(item => item.Name));
        for (var i = 0; i < lines.Length; i++)
        {
            using var row = JsonDocument.Parse(lines[i]);
            var value = row.RootElement;
            Assert.Equal(contractAgents[i].Name, value.GetProperty("agent").GetString());
            Assert.Equal(
                "not-exercised",
                value.GetProperty("exerciseState").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                value.GetProperty("observedViolationCount").ValueKind);
            Assert.Equal(
                contractAgents[i].Value
                    .GetProperty("allowedWriteGlobs")
                    .EnumerateArray()
                    .Select(item => item.GetString()),
                value.GetProperty("allowedWriteGlobs")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
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

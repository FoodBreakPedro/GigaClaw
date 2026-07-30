using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R3: enforcement is per-agent and resolved from the manifest on every load, so flipping an agent
/// from shadow mode to blocking is a contracts.json edit rather than a redeploy.
/// </summary>
public class PolicyEnforcementModeTests
{
    [Fact]
    public async Task An_agent_with_no_enforcement_key_stays_in_shadow_mode()
    {
        // Pre-R3 workspaces have no key at all. Promoting them to blocking would enforce a
        // per-agent decision nobody made for that workspace.
        var policy = await LoadAsync(defaultsEnforcement: null, agentEnforcement: null);

        Assert.True(policy.IsValid, policy.Diagnostic);
        Assert.Equal(PolicyEnforcementMode.Warn, policy.Enforcement);
    }

    [Theory]
    [InlineData("block", PolicyEnforcementMode.Block)]
    [InlineData("BLOCK", PolicyEnforcementMode.Block)]
    [InlineData("warn", PolicyEnforcementMode.Warn)]
    public async Task The_manifest_default_sets_the_floor(string value, PolicyEnforcementMode expected)
    {
        var policy = await LoadAsync(defaultsEnforcement: value, agentEnforcement: null);

        Assert.True(policy.IsValid, policy.Diagnostic);
        Assert.Equal(expected, policy.Enforcement);
    }

    [Theory]
    [InlineData("warn", "block", PolicyEnforcementMode.Block)]
    [InlineData("block", "warn", PolicyEnforcementMode.Warn)]
    public async Task The_per_agent_value_decides_in_both_directions(
        string manifestDefault,
        string agentValue,
        PolicyEnforcementMode expected)
    {
        // SP-1 signed enforcement off agent by agent, and two agents stay in warn while the rest
        // flip — so the override has to work downward as well as upward.
        var policy = await LoadAsync(manifestDefault, agentValue);

        Assert.True(policy.IsValid, policy.Diagnostic);
        Assert.Equal(expected, policy.Enforcement);
    }

    [Theory]
    [InlineData("\"blocked\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("1")]
    public async Task A_malformed_enforcement_value_invalidates_the_policy_rather_than_defaulting(
        string rawJsonValue)
    {
        // The dangerous reading is the silent one: "blocked" falling back to warn would read as
        // shadow mode on an agent the owner believed was enforcing. An unreadable contract blocks.
        var policy = await LoadRawAsync($"\"enforcement\": {rawJsonValue},");

        Assert.False(policy.IsValid);
        Assert.Contains("enforcement", policy.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            PolicyDecisionKind.Block,
            policy.Evaluate(PolicyToolCall.FileWrite("src/app.cs")).Kind);
    }

    [Fact]
    public async Task An_unreadable_contract_enforces_even_though_it_never_said_to()
    {
        // Enforces() must not consult a mode parsed out of a manifest we could not read.
        var policy = await LoadRawAsync("\"enforcement\": \"nonsense\",");

        Assert.False(policy.IsValid);
        Assert.True(policy.Enforces(policy.Evaluate(PolicyToolCall.FileWrite("etc/passwd"))));
    }

    [Fact]
    public async Task Shadow_mode_records_the_violation_without_enforcing_it()
    {
        // The SP-1 inventory keeps filling for agents that have not been flipped yet; that is the
        // evidence the next flip is signed off against.
        var policy = await LoadAsync(defaultsEnforcement: "warn", agentEnforcement: null);

        var decision = policy.Evaluate(PolicyToolCall.FileWrite("outside/scope.cs"));

        Assert.True(decision.IsViolation);
        Assert.False(policy.Enforces(decision));
    }

    [Fact]
    public async Task Block_mode_enforces_the_same_violation()
    {
        var policy = await LoadAsync(defaultsEnforcement: null, agentEnforcement: "block");

        var decision = policy.Evaluate(PolicyToolCall.FileWrite("outside/scope.cs"));

        Assert.True(decision.IsViolation);
        Assert.True(policy.Enforces(decision));
    }

    [Fact]
    public async Task Block_mode_never_enforces_a_call_that_is_allowed()
    {
        var policy = await LoadAsync(defaultsEnforcement: null, agentEnforcement: "block");

        var decision = policy.Evaluate(PolicyToolCall.FileWrite("src/app.cs"));

        Assert.False(decision.IsViolation);
        Assert.False(policy.Enforces(decision));
    }

    [Fact]
    public async Task Reloading_after_an_edit_picks_up_the_new_mode_without_a_restart()
    {
        // "Hot-reloadable" in practice: the manifest is re-read per dispatch, so an owner flipping
        // an agent mid-session affects the next run rather than requiring a redeploy.
        using var tmp = new TempDir();
        var manifest = Path.Combine(tmp.Path, "contracts.json");
        await File.WriteAllTextAsync(manifest, Manifest(null, "warn"));

        var before = await LoadManifestAsync(manifest, tmp.Path);
        Assert.Equal(PolicyEnforcementMode.Warn, before.Enforcement);

        await File.WriteAllTextAsync(manifest, Manifest(null, "block"));
        var after = await LoadManifestAsync(manifest, tmp.Path);

        Assert.Equal(PolicyEnforcementMode.Block, after.Enforcement);
    }

    private static Task<ContractPolicy> LoadAsync(
        string? defaultsEnforcement,
        string? agentEnforcement) =>
        LoadTextAsync(Manifest(defaultsEnforcement, agentEnforcement));

    private static Task<ContractPolicy> LoadRawAsync(string rawAgentProperty) =>
        LoadTextAsync(Manifest(null, null, rawAgentProperty));

    private static async Task<ContractPolicy> LoadTextAsync(string manifestText)
    {
        using var tmp = new TempDir();
        var manifest = Path.Combine(tmp.Path, "contracts.json");
        await File.WriteAllTextAsync(manifest, manifestText);
        return await LoadManifestAsync(manifest, tmp.Path);
    }

    private static Task<ContractPolicy> LoadManifestAsync(string manifest, string workspace) =>
        ContractPolicyLoader.LoadManifestAsync(
            manifest,
            workspace,
            "programmer",
            caseSensitivity: PathCaseSensitivity.Sensitive);

    private static string Manifest(
        string? defaultsEnforcement,
        string? agentEnforcement,
        string? rawAgentProperty = null)
    {
        var defaultsLine = defaultsEnforcement is null
            ? ""
            : $",\n    \"enforcement\": {JsonSerializer.Serialize(defaultsEnforcement)}";
        var agentLine = rawAgentProperty ?? (agentEnforcement is null
            ? ""
            : $"\"enforcement\": {JsonSerializer.Serialize(agentEnforcement)},");

        return $$"""
                 {
                   "version": 1,
                   "defaults": {
                     "maxDispatchAttempts": 3,
                     "retryBackoffSeconds": 300,
                     "requireAtomicHandoff": true,
                     "requireAuthorOnBoardWrites": true{{defaultsLine}}
                   },
                   "agents": {
                     "programmer": {
                       {{agentLine}}
                       "dispatches": ["assignment"],
                       "riskClass": "code-write",
                       "allowedWriteGlobs": ["src/**"],
                       "ticketExit": ["Review", "Blocked"]
                     }
                   }
                 }
                 """;
    }
}

/// <summary>
/// The shipped template's enforcement state, pinned against SP1-REVIEW.md's per-agent sign-off so
/// the two cannot drift silently.
/// </summary>
public class TemplateEnforcementStateTests
{
    private static readonly string[] SignedOffHolds = ["programmer", "code-janitor"];

    [Fact]
    public async Task Every_template_agent_resolves_to_the_mode_sp1_signed_off()
    {
        var agentsDir = Path.Combine(FindRepositoryRoot(), "ProjectTemplate", "Agents");
        var manifest = Path.Combine(agentsDir, "contracts.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var slugs = document.RootElement.GetProperty("agents").EnumerateObject()
            .Select(p => p.Name).ToArray();

        Assert.Equal(33, slugs.Length);

        foreach (var slug in slugs)
        {
            var policy = await ContractPolicyLoader.LoadManifestAsync(
                manifest,
                agentsDir,
                slug,
                caseSensitivity: PathCaseSensitivity.Sensitive);

            Assert.True(policy.IsValid, $"{slug}: {policy.Diagnostic}");
            var expected = SignedOffHolds.Contains(slug)
                ? PolicyEnforcementMode.Warn
                : PolicyEnforcementMode.Block;
            Assert.Equal(expected, policy.Enforcement);
        }
    }

    [Fact]
    public async Task The_two_holds_are_exactly_the_agents_that_declare_repo_wide_write_globs()
    {
        // This is *why* they are held, not just that they are: an agent whose globs are `**` has no
        // out-of-glob write to block, so flipping it would enforce nothing while risking its work.
        var agentsDir = Path.Combine(FindRepositoryRoot(), "ProjectTemplate", "Agents");
        var manifest = Path.Combine(agentsDir, "contracts.json");

        foreach (var slug in SignedOffHolds)
        {
            var policy = await ContractPolicyLoader.LoadManifestAsync(
                manifest, agentsDir, slug, caseSensitivity: PathCaseSensitivity.Sensitive);
            Assert.Contains("**", policy.AllowedWriteGlobs);
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

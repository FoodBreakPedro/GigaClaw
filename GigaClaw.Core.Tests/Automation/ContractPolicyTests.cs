using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

public class ContractPolicyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string TemplateAgents =
        Path.Combine(RepositoryRoot, "ProjectTemplate", "Agents");
    private static readonly string TemplateContracts =
        Path.Combine(TemplateAgents, "contracts.json");

    [Fact]
    public async Task Real_template_contracts_load_for_all_33_agents()
    {
        var agentSlugs = Directory
            .EnumerateDirectories(TemplateAgents)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => new DirectoryInfo(directory).Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(33, agentSlugs.Length);
        foreach (var slug in agentSlugs)
        {
            var policy = await ContractPolicyLoader.LoadManifestAsync(
                TemplateContracts,
                RepositoryRoot,
                slug);

            Assert.True(policy.IsValid, $"{slug}: {policy.Diagnostic}");
            Assert.Equal(slug, policy.AgentName);
            Assert.Equal(1, policy.ManifestVersion);
            Assert.NotNull(policy.Defaults);
            Assert.NotEmpty(policy.Dispatches);
            Assert.False(string.IsNullOrWhiteSpace(policy.RiskClass));
            Assert.Equal(
                PolicyDecisionKind.Warn,
                policy.Evaluate(PolicyToolCall.Network("https://example.test")).Kind);
        }
    }

    [Fact]
    public async Task Loader_exposes_version_defaults_dispatches_and_optional_review_cycles()
    {
        var programmer = await ContractPolicyLoader.LoadManifestAsync(
            TemplateContracts,
            RepositoryRoot,
            "programmer");
        var blogWriter = await ContractPolicyLoader.LoadManifestAsync(
            TemplateContracts,
            RepositoryRoot,
            "blog-writer");

        Assert.Equal(1, programmer.ManifestVersion);
        Assert.Equal(
            new ContractPolicyDefaults(3, 300, true, true),
            programmer.Defaults);
        Assert.Equal(
            ["assignment", "resume", "owner-feedback"],
            programmer.Dispatches);
        Assert.Null(programmer.MaxReviewCycles);
        Assert.Equal(2, blogWriter.MaxReviewCycles);
    }

    [Fact]
    public async Task Missing_manifest_returns_a_policy_that_blocks_with_a_diagnostic()
    {
        using var tmp = new TempDir();

        var policy = await ContractPolicyLoader.LoadAsync(tmp.Path, "programmer");
        var result = policy.Evaluate(PolicyToolCall.FileWrite("src/Program.cs"));

        Assert.False(policy.IsValid);
        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains("not found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_agent_returns_a_policy_that_blocks_with_a_diagnostic()
    {
        using var tmp = new TempDir();
        var manifest = await WriteManifestAsync(tmp.Path, """
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
                  "allowedWriteGlobs": ["**"],
                  "ticketExit": ["Review"]
                }
              }
            }
            """);

        var policy = await ContractPolicyLoader.LoadManifestAsync(
            manifest,
            tmp.Path,
            "not-an-agent");
        var result = policy.Evaluate(PolicyToolCall.BoardWrite());

        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains("missing", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-an-agent", result.Reason);
    }

    [Theory]
    [MemberData(nameof(MalformedContractCases))]
    public async Task Malformed_contracts_fail_closed(string json, string diagnostic)
    {
        using var tmp = new TempDir();
        var manifest = await WriteManifestAsync(tmp.Path, json);

        var policy = await ContractPolicyLoader.LoadManifestAsync(
            manifest,
            tmp.Path,
            "a");
        var result = policy.Evaluate(PolicyToolCall.BoardWrite());

        Assert.False(policy.IsValid);
        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains(diagnostic, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> MalformedContractCases => new()
    {
        { "{not-json", "malformed JSON" },
        { ValidManifest("""{"a":[]}""", version: 2), "unsupported manifest version" },
        { """{"version":1,"defaults":[],"agents":{}}""", "defaults" },
        { ValidManifest("""{"a":[]}"""), "must be a JSON object" },
        {
            ValidManifest(
                """{"a":{"riskClass":"code-write","allowedWriteGlobs":[],"ticketExit":[]}}"""),
            "dispatches"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":[],"riskClass":"code-write","allowedWriteGlobs":[],"ticketExit":[]}}"""),
            "at least one"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"allowedWriteGlobs":[],"ticketExit":[]}}"""),
            "riskClass"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"riskClass":"code-write","allowedWriteGlobs":"**","ticketExit":[]}}"""),
            "allowedWriteGlobs"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"riskClass":"code-write","allowedWriteGlobs":[],"ticketExit":[null]}}"""),
            "ticketExit"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"riskClass":"future-root","allowedWriteGlobs":[],"ticketExit":[]}}"""),
            "unknown riskClass"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"riskClass":"code-write","allowedWriteGlobs":["../**"],"ticketExit":[]}}"""),
            "invalid allowedWriteGlobs"
        },
        {
            ValidManifest(
                """{"a":{"dispatches":["assignment"],"riskClass":"code-write","allowedWriteGlobs":[],"ticketExit":[],"maxReviewCycles":0}}"""),
            "maxReviewCycles"
        },
    };

    [Theory]
    [MemberData(nameof(GlobCases))]
    public void Glob_matching_uses_documented_gitignore_style_rules(
        string[] patterns,
        string path,
        bool expected)
    {
        var matcher = new GitIgnoreGlobSet(patterns, PathCaseSensitivity.Sensitive);

        Assert.Equal(expected, matcher.IsMatch(path));
    }

    public static TheoryData<string[], string, bool> GlobCases => new()
    {
        { ["docs/*.md"], "docs/readme.md", true },
        { ["docs/*.md"], "docs/guides/readme.md", false },
        { ["docs/**"], "docs/guides/readme.md", true },
        { ["*.md"], "docs/guides/readme.md", true },
        { ["/readme.md"], "readme.md", true },
        { ["/readme.md"], "docs/readme.md", false },
        { ["src/**/generated/*.cs"], "src/generated/A.cs", true },
        { ["src/**/generated/*.cs"], "src/a/b/generated/A.cs", true },
        { ["assets/file?.[jt]s"], "assets/file1.js", true },
        { ["assets/file?.[jt]s"], "assets/file12.js", false },
        { ["**", "!secrets/**"], "src/a.cs", true },
        { ["**", "!secrets/**"], "secrets/token.txt", false },
        { ["**", "!secrets/**", "secrets/public/**"], "secrets/public/example.txt", true },
        { [".agents/*/memory/MEMORY.md"], ".agents/programmer/memory/MEMORY.md", true },
        { [".agents/*/memory/MEMORY.md"], ".agents/a/nested/memory/MEMORY.md", false },
        { ["media/renders/"], "media/renders/job/image.png", true },
        { ["media/renders/"], "media/rendering/image.png", false },
    };

    [Fact]
    public void Glob_case_behavior_can_follow_sensitive_or_insensitive_platform_rules()
    {
        var sensitive = new GitIgnoreGlobSet(
            ["Content/**"],
            PathCaseSensitivity.Sensitive);
        var insensitive = new GitIgnoreGlobSet(
            ["Content/**"],
            PathCaseSensitivity.Insensitive);

        Assert.False(sensitive.IsMatch("content/post.md"));
        Assert.True(insensitive.IsMatch("content/post.md"));
    }

    [Fact]
    public async Task File_evaluation_allows_in_scope_and_warns_out_of_scope()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["src/**"],
            ["Review", "Blocked"]);

        var inside = policy.Evaluate(PolicyToolCall.FileWrite("src/Program.cs"));
        var outside = policy.Evaluate(PolicyToolCall.FileWrite("doc/Program.md"));

        Assert.Equal(PolicyDecisionKind.Allow, inside.Kind);
        Assert.Equal(PolicyDecisionKind.Warn, outside.Kind);
        Assert.Contains("outside allowedWriteGlobs", outside.Reason);
    }

    [Fact]
    public async Task File_evaluation_normalizes_absolute_paths_inside_the_workspace()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["src/**"],
            ["Review"]);
        var absolute = Path.Combine(tmp.Path, "src", "..", "src", "Program.cs");

        var result = policy.Evaluate(PolicyToolCall.FileWrite(absolute));

        Assert.Equal(PolicyDecisionKind.Allow, result.Kind);
        Assert.Contains("src/Program.cs", result.Reason);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    public async Task Traversal_attempts_block_even_while_contract_violations_warn(string target)
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["**"],
            ["Review"]);

        var result = policy.Evaluate(PolicyToolCall.FileWrite(target));

        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains("escapes workspace", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Absolute_path_outside_workspace_blocks()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["**"],
            ["Review"]);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");

        var result = policy.Evaluate(PolicyToolCall.FileWrite(outside));

        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains("escapes workspace", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Symbolic_link_escape_blocks()
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        Directory.CreateSymbolicLink(
            Path.Combine(workspace.Path, "linked"),
            outside.Path);
        var policy = await LoadPolicyAsync(
            workspace.Path,
            "programmer",
            "code-write",
            ["**"],
            ["Review"]);

        var result = policy.Evaluate(PolicyToolCall.FileWrite("linked/escape.txt"));

        Assert.Equal(PolicyDecisionKind.Block, result.Kind);
        Assert.Contains("symbolic link", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ticket_exit_is_checked_separately_from_general_board_write()
    {
        using var tmp = new TempDir();
        var policy = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["**"],
            ["Review", "Blocked"]);

        Assert.Equal(
            PolicyDecisionKind.Allow,
            policy.Evaluate(PolicyToolCall.BoardWrite("comment")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Allow,
            policy.Evaluate(PolicyToolCall.TicketExit("review")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Warn,
            policy.Evaluate(PolicyToolCall.TicketExit("Done")).Kind);
    }

    [Fact]
    public async Task Risk_class_capabilities_are_explicit_and_network_is_not_invented()
    {
        using var tmp = new TempDir();
        var research = await LoadPolicyAsync(
            tmp.Path,
            "researcher",
            "research",
            ["research/**"],
            ["Review"]);
        var writer = await LoadPolicyAsync(
            tmp.Path,
            "writer",
            "content-write",
            ["content/**"],
            ["Review"]);
        var boardOnly = await LoadPolicyAsync(
            tmp.Path,
            "groomer",
            "board-write",
            [],
            ["Todo"]);

        Assert.Equal(
            PolicyDecisionKind.Warn,
            research.Evaluate(PolicyToolCall.Network("https://example.test")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Warn,
            writer.Evaluate(PolicyToolCall.Network("https://example.test")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Allow,
            boardOnly.Evaluate(PolicyToolCall.BoardWrite()).Kind);
        Assert.Equal(
            PolicyDecisionKind.Warn,
            boardOnly.Evaluate(PolicyToolCall.FileWrite("anything.txt")).Kind);
    }

    [Fact]
    public async Task Git_and_agent_memory_paths_require_their_specific_capability()
    {
        using var tmp = new TempDir();
        var programmer = await LoadPolicyAsync(
            tmp.Path,
            "programmer",
            "code-write",
            ["**"],
            ["Review"]);
        var evaluator = await LoadPolicyAsync(
            tmp.Path,
            "evaluator",
            "memory-write",
            [".agents/*/memory/MEMORY.md"],
            []);

        Assert.Equal(
            PolicyDecisionKind.Warn,
            programmer.Evaluate(PolicyToolCall.FileWrite(".git/config")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Warn,
            programmer.Evaluate(PolicyToolCall.FileWrite(".agents/programmer/memory/MEMORY.md")).Kind);
        Assert.Equal(
            PolicyDecisionKind.Allow,
            evaluator.Evaluate(PolicyToolCall.FileWrite(".agents/programmer/memory/MEMORY.md")).Kind);
    }

    private static async Task<ContractPolicy> LoadPolicyAsync(
        string workspacePath,
        string agentName,
        string riskClass,
        string[] globs,
        string[] ticketExit)
    {
        var manifest = await WriteManifestAsync(
            workspacePath,
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
                  "{{agentName}}": {
                    "dispatches": ["assignment"],
                    "riskClass": "{{riskClass}}",
                    "allowedWriteGlobs": {{System.Text.Json.JsonSerializer.Serialize(globs)}},
                    "ticketExit": {{System.Text.Json.JsonSerializer.Serialize(ticketExit)}}
                  }
                }
              }
              """);
        var policy = await ContractPolicyLoader.LoadManifestAsync(
            manifest,
            workspacePath,
            agentName,
            caseSensitivity: PathCaseSensitivity.Sensitive);
        Assert.True(policy.IsValid, policy.Diagnostic);
        return policy;
    }

    private static async Task<string> WriteManifestAsync(string directory, string json)
    {
        var manifest = Path.Combine(directory, "contracts.json");
        await File.WriteAllTextAsync(manifest, json);
        return manifest;
    }

    private static string ValidManifest(string agents, int version = 1) =>
        $$"""
          {
            "version": {{version}},
            "defaults": {
              "maxDispatchAttempts": 3,
              "retryBackoffSeconds": 300,
              "requireAtomicHandoff": true,
              "requireAuthorOnBoardWrites": true
            },
            "agents": {{agents}}
          }
          """;

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

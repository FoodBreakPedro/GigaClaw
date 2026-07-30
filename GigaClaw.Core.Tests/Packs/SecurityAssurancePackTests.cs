using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// Content tests for the <c>security-assurance</c> pack (G6). They assert the five-binding rule of
/// doc/pack-infrastructure.md §7 against the pack's own files — a contract entry, a model default
/// with a stated criterion, a team membership, at least one enabled dispatching automation, and an
/// eval fixture, per agent.
/// <para>
/// This is deliberately narrow: it checks pack <b>content</b>, not the composer. The generic
/// composition rules (slug collisions, cross-pack references, permission closure) belong to
/// <c>PackComposer</c> and to the catalog's <c>--strict-packs</c> gate. What no generic gate can
/// check is that a criterion says something, so that is asserted here too.
/// </para>
/// </summary>
public class SecurityAssurancePackTests
{
    private const string PackId = "security-assurance";

    private static readonly string PackRoot =
        Path.Combine(PythonContractRunner.RepositoryRoot, "Packs", PackId);
    private static readonly string PackAgents = Path.Combine(PackRoot, "Agents");

    private static readonly string[] ExpectedAgents =
        ["secrets-reviewer", "security-auditor", "supply-chain-reviewer", "threat-modeler"];

    /// <summary>The two lanes whose input is not a workspace file (doc/verdict-contract.md): they
    /// must cite hash evidence and no path evidence, and anything gating on them must turn
    /// <c>requireFreshArtifact</c> off.</summary>
    private static readonly string[] NonFileLanes = ["supply-chain-reviewer", "threat-modeler"];

    private static JsonDocument Read(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PackAgents, relativePath)));

    private static AutomationConfig Automations() =>
        JsonSerializer.Deserialize<AutomationConfig>(
            File.ReadAllText(Path.Combine(PackAgents, "automations.json")),
            AutomationStore.JsonOptions)
        ?? throw new InvalidDataException("automations.json deserialized to null.");

    private static JsonDocument Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PackRoot, "pack.json")));

    private static string[] ManifestArray(JsonElement parent, params string[] path)
    {
        var element = parent;
        foreach (var segment in path)
            element = element.GetProperty(segment);
        return [.. element.EnumerateArray().Select(e => e.GetString()!)];
    }

    [Fact]
    public void Provides_agents_matches_the_skill_directories_on_disk()
    {
        var onDisk = Directory
            .EnumerateDirectories(PackAgents)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => new DirectoryInfo(directory).Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedAgents, onDisk);

        using var manifest = Manifest();
        Assert.Equal(ExpectedAgents, ManifestArray(manifest.RootElement, "provides", "agents").Order(StringComparer.Ordinal));
        Assert.Equal(PackId, manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal(PackId, new DirectoryInfo(PackRoot).Name);
    }

    [Fact]
    public void Binding_1_every_agent_has_a_contract_entry_inside_the_manifest_write_ceiling()
    {
        using var contracts = Read("contracts.json");
        using var manifest = Manifest();
        var ceiling = ManifestArray(manifest.RootElement, "permissions", "allowedWriteGlobs").ToHashSet(StringComparer.Ordinal);
        var riskClasses = ManifestArray(manifest.RootElement, "permissions", "riskClasses").ToHashSet(StringComparer.Ordinal);
        var agents = contracts.RootElement.GetProperty("agents");

        // A non-core pack may not carry contract defaults (§4 merge table).
        Assert.False(contracts.RootElement.TryGetProperty("defaults", out _));

        foreach (var slug in ExpectedAgents)
        {
            Assert.True(agents.TryGetProperty(slug, out var contract), $"{slug} has no contracts.json entry.");
            Assert.Contains(contract.GetProperty("riskClass").GetString()!, riskClasses);

            foreach (var glob in contract.GetProperty("allowedWriteGlobs").EnumerateArray().Select(e => e.GetString()!))
            {
                // §7.5 is implemented as literal set membership in PackComposer.CheckPermissions,
                // not as glob-subset semantics, so each per-agent glob must appear verbatim.
                Assert.True(ceiling.Contains(glob),
                    $"{slug} allowedWriteGlobs '{glob}' is not verbatim in permissions.allowedWriteGlobs.");
            }

            // A reviewer that can write source has not reviewed anything.
            Assert.DoesNotContain("**", contract.GetProperty("allowedWriteGlobs")
                .EnumerateArray().Select(e => e.GetString()!));
        }

        Assert.Equal(ExpectedAgents.Length, agents.EnumerateObject().Count());
    }

    [Fact]
    public void Binding_2_every_agent_has_a_model_default_with_a_stated_criterion()
    {
        using var models = Read("models.json");

        foreach (var slug in ExpectedAgents)
        {
            Assert.True(models.RootElement.TryGetProperty(slug, out var mapping), $"{slug} has no models.json entry.");

            // §7.2's object form: `slug -> {model, criterion}`. A bare string would satisfy the
            // catalog's "model mapping" check while stating no criterion at all.
            Assert.Equal(JsonValueKind.Object, mapping.ValueKind);

            var model = mapping.GetProperty("model").GetString()!;
            Assert.Contains(model, ClaudeModelCatalog.Models);

            // The wshobson caveat: safety classifiers route security prompts off Fable and back to
            // Opus at a higher price, so a Fable mapping is a cost trap, not a saving.
            Assert.NotEqual("claude-fable-5", model);

            var criterion = mapping.GetProperty("criterion").GetString()!;
            Assert.True(criterion.Length >= 120,
                $"{slug}'s criterion must state real reasoning, not a label: '{criterion}'");
        }

        // Owner decision Q3 (2026-07-30): the auditor runs on every code ticket, so it is Sonnet.
        Assert.Equal("claude-sonnet-4-6", models.RootElement.GetProperty("security-auditor").GetProperty("model").GetString());
    }

    [Fact]
    public void Binding_3_every_agent_holds_a_seat_on_the_packs_team()
    {
        using var teams = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackAgents, "teams.json")));

        // PackComposer.ReadTeams requires a bare JSON array of team objects.
        Assert.Equal(JsonValueKind.Array, teams.RootElement.ValueKind);
        var team = Assert.Single(teams.RootElement.EnumerateArray());
        Assert.Equal("security-review", team.GetProperty("slug").GetString());

        var seats = team.GetProperty("agentSlugs").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var slug in ExpectedAgents)
            Assert.True(seats.Contains(slug), $"{slug} holds no seat on security-review.");

        using var manifest = Manifest();
        Assert.Equal(["security-review"], ManifestArray(manifest.RootElement, "provides", "teams"));

        // teamMembership only ever adds a pack's own agents to somebody else's team.
        foreach (var entry in manifest.RootElement.GetProperty("teamMembership").EnumerateObject())
        {
            Assert.NotEqual("security-review", entry.Name);
            foreach (var slug in entry.Value.EnumerateArray().Select(e => e.GetString()!))
                Assert.Contains(slug, ExpectedAgents);
        }
    }

    [Fact]
    public void Binding_4_every_agent_has_at_least_one_enabled_dispatching_automation()
    {
        var config = Automations();

        foreach (var slug in ExpectedAgents)
        {
            var dispatching = config.Automations
                .Where(automation => automation.Enabled)
                .Where(automation => automation.Actions.OfType<RunAgentActionSpec>()
                    .Any(action => action.Agent == slug))
                .Select(automation => automation.Id)
                .ToArray();

            Assert.True(dispatching.Length > 0, $"{slug} has no enabled dispatching automation in the pack.");
        }

        Assert.Equal(
            config.Automations.Select(a => a.Id).Order(StringComparer.Ordinal),
            config.Automations.Select(a => a.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Binding_5_every_agent_is_named_by_an_eval_fixture_that_has_a_scenario()
    {
        var fixtureRoot = Path.Combine(PackRoot, "eval", "fixtures");
        var fixtures = Directory.GetFiles(fixtureRoot, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        using var manifest = Manifest();
        var declared = ManifestArray(manifest.RootElement, "evalFixtures").ToHashSet(StringComparer.Ordinal);

        var agentsCovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in fixtures)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(path));
            var id = fixture.RootElement.GetProperty("Id").GetString()!;

            // ReplayRunner.ReadFixture requires <Id>.json, Version 1, and a scenario beside it.
            Assert.Equal(Path.GetFileNameWithoutExtension(path), id);
            Assert.Equal(1, fixture.RootElement.GetProperty("Version").GetInt32());
            Assert.Contains(id, declared);

            var scenario = fixture.RootElement.GetProperty("Scenario").GetString()!;
            Assert.True(File.Exists(Path.Combine(fixtureRoot, "scenarios", scenario + ".ndjson")),
                $"fixture '{id}' references a missing scenario '{scenario}.ndjson'.");

            agentsCovered.Add(fixture.RootElement.GetProperty("Agent").GetString()!);
        }

        Assert.Equal(declared.Count, fixtures.Length);
        foreach (var slug in ExpectedAgents)
            Assert.True(agentsCovered.Contains(slug), $"{slug} is named by no eval fixture.");
    }

    [Fact]
    public void Automations_use_only_action_types_the_manifest_declares_and_agents_the_pack_or_core_provides()
    {
        var config = Automations();
        using var manifest = Manifest();
        var declaredActions = ManifestArray(manifest.RootElement, "permissions", "actions").ToHashSet(StringComparer.Ordinal);

        var coreAgents = Directory
            .EnumerateDirectories(Path.Combine(PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents"))
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => new DirectoryInfo(directory).Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var automation in config.Automations)
        {
            foreach (var action in automation.Actions)
            {
                Assert.True(declaredActions.Contains(action.UiTypeKey),
                    $"automation '{automation.Id}' uses action '{action.UiTypeKey}', absent from permissions.actions.");

                // permissions.network is "declared" for advisory reads only; the pack must own no
                // host-side outbound call, which is what keeps mutations dry-run by construction.
                Assert.NotEqual("httpRequest", action.UiTypeKey);

                if (action is RunAgentActionSpec run)
                {
                    Assert.True(ExpectedAgents.Contains(run.Agent) || coreAgents.Contains(run.Agent),
                        $"automation '{automation.Id}' runs unknown agent '{run.Agent}'.");
                }
            }
        }
    }

    [Fact]
    public void Every_block_gate_escalates_and_matches_its_lanes_freshness_model()
    {
        var config = Automations();

        foreach (var slug in ExpectedAgents)
        {
            var gates = config.Automations
                .Where(automation => automation.Enabled)
                .Where(automation => automation.Conditions.OfType<VerdictIsConditionSpec>()
                    .Any(condition => condition.Agent == slug))
                .ToArray();

            var gate = Assert.Single(gates);

            var condition = gate.Conditions.OfType<VerdictIsConditionSpec>().Single(c => c.Agent == slug);
            Assert.Contains("BLOCK", condition.Verdicts);
            Assert.Contains("INVALID", condition.Verdicts);
            Assert.Contains("STALE", condition.Verdicts);

            // A verdict whose input is not a workspace file can never re-hash to its inputDigest,
            // so freshness must be off for those lanes and on for the file-based ones.
            Assert.Equal(!NonFileLanes.Contains(slug), condition.RequireFreshArtifact);

            var move = Assert.Single(gate.Actions.OfType<MoveTicketStatusActionSpec>());
            Assert.Equal("Blocked", move.To);
            Assert.NotEmpty(gate.Actions.OfType<AddCommentActionSpec>().Single().Content);
        }
    }

    [Fact]
    public void Every_worked_verdict_validates_and_cites_the_evidence_kind_its_lane_requires()
    {
        // `\r?$` rather than `$`: with RegexOptions.Multiline, .NET anchors `$` immediately before
        // a `\n`, which on a CRLF checkout is the position *after* the `\r` — and `\S+` cannot
        // consume the `\r` because it is whitespace. So the marker never matches on Windows, where
        // git checks these files out with CRLF. The block pattern below already allowed for it.
        var markerPattern = new Regex(@"^GIGACLAW-VERDICT v1 (\S+) (\S+) artifact-(\S+)\r?$", RegexOptions.Multiline);
        var blockPattern = new Regex(@"```json\r?\n(\{.*?\r?\n\})\r?\n```", RegexOptions.Singleline);

        foreach (var slug in ExpectedAgents)
        {
            var skill = File.ReadAllText(Path.Combine(PackAgents, slug, "SKILL.md"));

            var marker = Assert.Single(markerPattern.Matches(skill).Cast<Match>());
            var block = Assert.Single(blockPattern.Matches(skill).Cast<Match>());

            using var verdict = JsonDocument.Parse(block.Groups[1].Value);
            var root = verdict.RootElement;
            var digest = root.GetProperty("inputDigest").GetString()!;

            // The marker and the body must agree, or the comment is not a verdict.
            Assert.Equal(slug, root.GetProperty("agent").GetString());
            Assert.Equal(slug, marker.Groups[1].Value);
            Assert.Equal(root.GetProperty("verdict").GetString(), marker.Groups[2].Value);
            Assert.Equal(digest, marker.Groups[3].Value);

            var kinds = root.GetProperty("evidence").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("hash", kinds);
            if (NonFileLanes.Contains(slug))
                Assert.DoesNotContain("path", kinds);
            else
                Assert.Contains("path", kinds);

            // verdict_contract.py is the single implementation of the rules; run it rather than
            // re-deriving them here.
            var temp = Path.Combine(Path.GetTempPath(), $"gigaclaw-verdict-{slug}-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(temp, block.Groups[1].Value);
                var (exitCode, output) = PythonContractRunner.RunScript(
                    "verdict_contract.py", temp,
                    "--expect-agent", slug,
                    "--expect-ticket", root.GetProperty("ticketId").GetRawText().Trim('"'),
                    "--expect-digest", digest);
                Assert.True(exitCode == 0, $"{slug}'s worked verdict failed the contract:{Environment.NewLine}{output}");
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void Every_skill_stays_under_the_prompt_budget_and_ships_a_memory_index()
    {
        // GigaClaw.Eval/evalconfig.json: warning at 12288 UTF-8 bytes, maximum at 16384.
        foreach (var slug in ExpectedAgents)
        {
            var skill = Path.Combine(PackAgents, slug, "SKILL.md");
            var bytes = Encoding.UTF8.GetByteCount(File.ReadAllText(skill));
            Assert.True(bytes < 12288, $"{slug}/SKILL.md is {bytes} UTF-8 bytes; move detail into references/.");

            var memory = Path.Combine(PackAgents, slug, "memory", "MEMORY.md");
            Assert.True(File.Exists(memory), $"{slug} ships no memory index.");
            Assert.Contains("Memory index — " + slug, File.ReadAllText(memory), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Declared_scripts_and_root_files_match_the_tree()
    {
        using var manifest = Manifest();

        foreach (var script in ManifestArray(manifest.RootElement, "provides", "scripts"))
            Assert.True(File.Exists(Path.Combine(PackAgents, script)), $"provides.scripts names missing '{script}'.");

        var scriptsDir = Path.Combine(PackAgents, "scripts");
        Assert.Equal(
            ManifestArray(manifest.RootElement, "provides", "scripts").Order(StringComparer.Ordinal),
            Directory.GetFiles(scriptsDir, "*", SearchOption.AllDirectories)
                .Select(path => "scripts/" + Path.GetRelativePath(scriptsDir, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal));

        // DirectoryPackSource.RootRelativePaths() skips pack.json and Agents/ and eval/; everything
        // else at the pack root is a workspace root file and must be declared.
        var rootFiles = Directory
            .GetFiles(PackRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(PackRoot, path).Replace('\\', '/'))
            .Where(rel => rel != "pack.json"
                && !rel.StartsWith("Agents/", StringComparison.Ordinal)
                && !rel.StartsWith("eval/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ManifestArray(manifest.RootElement, "provides", "rootFiles").Order(StringComparer.Ordinal), rootFiles);
    }

    [Fact]
    public void The_pack_is_invisible_to_the_core_template_scanners()
    {
        // CatalogGenerator and StaticEvalRunner discover agents by scanning ProjectTemplate/Agents,
        // and ReplayRunner reads one non-recursive fixture root. A pack under Packs/ must therefore
        // not appear in either until the pack-aware composer lands — that is what keeps the core
        // catalog and the eval suite green while this pack sits in the tree.
        var templateAgents = Path.Combine(PythonContractRunner.RepositoryRoot, "ProjectTemplate", "Agents");
        foreach (var slug in ExpectedAgents)
            Assert.False(Directory.Exists(Path.Combine(templateAgents, slug)), $"{slug} leaked into ProjectTemplate.");

        var coreFixtures = Path.Combine(PythonContractRunner.RepositoryRoot, "GigaClaw.Eval", "fixtures");
        using var manifest = Manifest();
        foreach (var id in ManifestArray(manifest.RootElement, "evalFixtures"))
            Assert.False(File.Exists(Path.Combine(coreFixtures, id + ".json")), $"{id} leaked into the core fixture root.");
    }
}

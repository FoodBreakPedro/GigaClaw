using System.Text.Json;
using GigaClaw.Catalog;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// The five-binding CI gate (doc/pack-infrastructure.md §7) as it applies to a pack.
///
/// <para>
/// The rule: a pack agent ships with a contract entry, a model mapping with a stated criterion, a
/// team membership, at least one enabled dispatching automation, and an eval fixture — or the
/// catalog rejects the pack. These tests build a pack tree on disk, hand the catalog the composed
/// view of it, and assert the gate refuses each missing binding. Without them a pack could ship
/// four of five bindings and the catalog would call it green, which is precisely how a 203-agent
/// catalog of unbound, undispatchable agents accumulates.
/// </para>
/// </summary>
public sealed class PackBindingGateTests
{
    [Fact]
    public void A_fully_bound_pack_agent_passes_the_gate_in_strict_pack_mode()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true);

        var build = repo.Build();

        Assert.Empty(build.PackPolicyViolations);
        var agent = Assert.Single(build.Catalog.Agents, entry => entry.Slug == "security-auditor");
        Assert.Equal("security-assurance", agent.Pack);
        Assert.True(agent.EvalFixturePresent);
        Assert.Empty(GatedGaps(build, strict: false, strictPacks: true));
    }

    /// <summary>
    /// §7.1. The fifth reason. <c>EvalBaselinePresent</c> was already computed and was never one of
    /// the gap reasons, and a baseline is not a fixture anyway — this is the check that did not
    /// exist anywhere before T6.
    /// </summary>
    [Fact]
    public void A_pack_agent_without_a_replay_fixture_fails_the_gate()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: false);

        var build = repo.Build();

        var gap = Assert.Single(GatedGaps(build, strict: false, strictPacks: true));
        Assert.Equal(["eval fixture"], gap.Missing);
        Assert.Equal("security-assurance", gap.Pack);
    }

    /// <summary>
    /// §7.4 and owner decision Q2: a pack is gated in strict mode from its first commit, whatever
    /// mode core is in. If this regresses to "packs follow core's mode", a pack can land unbound
    /// while core is in baseline mode — the anti-pattern the rule exists to prevent.
    /// </summary>
    [Fact]
    public void Pack_gaps_are_gated_even_when_core_is_not_strict()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: false);

        var build = repo.Build();

        Assert.NotEmpty(GatedGaps(build, strict: false, strictPacks: true));
        Assert.Empty(GatedGaps(build, strict: false, strictPacks: false));
        Assert.NotEmpty(GatedGaps(build, strict: true, strictPacks: false));
    }

    /// <summary>§7.2: a mapping without a stated criterion is a gap, not a pass.</summary>
    [Fact]
    public void A_bare_string_model_mapping_fails_for_want_of_a_criterion()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true, modelCriterion: null);

        var build = repo.Build();

        var gap = Assert.Single(GatedGaps(build, strict: false, strictPacks: true));
        Assert.Equal([CatalogGenerator.ModelCriterionReason], gap.Missing);
        Assert.Equal(
            "claude-sonnet-4-6",
            Assert.Single(build.Catalog.Agents, entry => entry.Slug == "security-auditor").ExplicitModelMapping);
    }

    /// <summary>
    /// §7.5. Read as exact string membership, deliberately: inferring that a ceiling glob subsumes
    /// a narrower one means writing a containment engine the spec never asked for, and its false
    /// negatives would silently widen write scope.
    /// </summary>
    [Fact]
    public void A_write_glob_outside_the_manifest_ceiling_is_a_policy_violation()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true, writeGlobs: ["doc/security/**", "src/**"]);

        var build = repo.Build();

        var violation = Assert.Single(build.PackPolicyViolations);
        Assert.Equal("permissions.allowedWriteGlobs", violation.Rule);
        Assert.Contains("src/**", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrower_glob_that_the_ceiling_does_not_list_verbatim_is_still_a_violation()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true, writeGlobs: ["doc/security/findings/*.md"]);

        var build = repo.Build();

        var violation = Assert.Single(build.PackPolicyViolations);
        Assert.Contains("doc/security/findings/*.md", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>§7.6: every action type the pack's automations use must be declared.</summary>
    [Fact]
    public void An_undeclared_automation_action_type_is_a_policy_violation()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true, extraAction: "httpRequest");

        var build = repo.Build();

        var violation = Assert.Single(build.PackPolicyViolations);
        Assert.Equal("permissions.actions", violation.Rule);
        Assert.Contains("httpRequest", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The composer's view is what the gate reads, so a pack's agents, contracts, models,
    /// automations and teams all have to land in the catalog attributed to that pack. Core's
    /// entries must be untouched by a pack's presence.
    /// </summary>
    [Fact]
    public void Pack_content_composes_into_the_catalog_without_disturbing_core()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true);

        var build = repo.Build();

        Assert.Equal(2, build.Catalog.Packs.Count);
        Assert.Equal(["core", "security-assurance"], build.Catalog.Packs.Select(pack => pack.Id).ToArray());
        Assert.Equal(1, build.Catalog.Packs.Single(pack => pack.Id == "security-assurance").AgentCount);
        Assert.Equal(1, build.Catalog.Packs.Single(pack => pack.Id == "core").AgentCount);
        Assert.Contains(build.Catalog.Teams, team => team.Slug == "security-review");
        Assert.Contains(build.Catalog.Automations, automation => automation.Id == "security-gate-on-review");
    }

    /// <summary>
    /// §7.3: <c>receiptEmitters</c> is pack data now. A pack introducing an emitter of a family core
    /// already owns unions in, and core's own owners survive — a pack may add an emitter, never
    /// revoke one, or it could silently break a chain core's agents depend on.
    /// </summary>
    [Fact]
    public void Receipt_emitters_union_across_packs_and_never_subtract()
    {
        using var repo = new PackRepo();
        repo.AddPackAgent("security-auditor", fixture: true);

        var table = ReceiptEmitterTable.Compose(repo.Sources());

        Assert.Equal(
            ["blog-reviewer", "evaluator", "security-auditor"],
            table["GIGACLAW-VERDICT"]);
        Assert.Equal(["security-auditor"], table["SECURITY-AUDIT"]);
    }

    private static IReadOnlyList<BindingGap> GatedGaps(CatalogBuild build, bool strict, bool strictPacks) =>
        CatalogGenerator.FindBindingGaps(build.Catalog)
            .Select(gap => new BindingGap(
                gap.Pack,
                gap.Slug,
                CatalogGenerator.GatedReasons(gap, strict, strictPacks, build.Catalog)))
            .Where(gap => gap.Missing.Count > 0)
            .ToArray();

    /// <summary>
    /// A minimal two-pack repository: a fully bound core agent so the gate has something known-good
    /// to contrast against, plus one pack whose bindings the test varies.
    /// </summary>
    private sealed class PackRepo : IDisposable
    {
        private const string PackId = "security-assurance";
        private readonly TempDir _tmp = new();
        private readonly List<string> _writeGlobs = ["doc/security/**"];
        private readonly List<string> _actions = ["runAgent"];

        public PackRepo()
        {
            Directory.CreateDirectory(Path.Combine(_tmp.Path, "doc"));
            var core = Path.Combine(_tmp.Path, "ProjectTemplate", "Agents");
            Directory.CreateDirectory(Path.Combine(core, "core-agent"));
            File.WriteAllText(Path.Combine(core, "core-agent", "SKILL.md"), "# core-agent");
            File.WriteAllText(
                Path.Combine(core, "contracts.json"),
                """{"agents":{"core-agent":{"riskClass":"code-write"}}}""");
            File.WriteAllText(
                Path.Combine(core, "models.json"),
                """{"core-agent":{"model":"claude-haiku-4-5","criterion":"Mechanical."}}""");
            File.WriteAllText(Path.Combine(core, "automations.json"), """{"automations":[]}""");
            File.WriteAllText(
                Path.Combine(_tmp.Path, "ProjectTemplate", "pack.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    id = "core",
                    version = "1.0.0",
                    kind = "core",
                    permissions = new { actions = Array.Empty<string>(), allowedWriteGlobs = Array.Empty<string>() },
                    receiptEmitters = new Dictionary<string, string[]>
                    {
                        ["GIGACLAW-VERDICT"] = ["blog-reviewer", "evaluator"]
                    }
                }));
        }

        public string PackRoot => Path.Combine(_tmp.Path, "Packs", PackId);

        public void AddPackAgent(
            string slug,
            bool fixture,
            string? modelCriterion = "Adversarial reasoning over untrusted input; a miss is a shipped vulnerability.",
            IReadOnlyList<string>? writeGlobs = null,
            string? extraAction = null)
        {
            var agents = Path.Combine(PackRoot, "Agents");
            Directory.CreateDirectory(Path.Combine(agents, slug));
            File.WriteAllText(Path.Combine(agents, slug, "SKILL.md"), $"# {slug}");

            var globs = writeGlobs ?? ["doc/security/**"];
            File.WriteAllText(
                Path.Combine(agents, "contracts.json"),
                JsonSerializer.Serialize(new
                {
                    agents = new Dictionary<string, object>
                    {
                        [slug] = new { riskClass = "security-review", allowedWriteGlobs = globs }
                    }
                }));

            File.WriteAllText(
                Path.Combine(agents, "models.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [slug] = modelCriterion is null
                        ? "claude-sonnet-4-6"
                        : new { model = "claude-sonnet-4-6", criterion = modelCriterion }
                }));

            var actions = new List<object>
            {
                new { type = "runAgent", agent = slug, model = "claude-sonnet-4-6" }
            };
            if (extraAction is not null) actions.Add(new { type = extraAction, agent = slug, model = (string?)null });
            File.WriteAllText(
                Path.Combine(agents, "automations.json"),
                JsonSerializer.Serialize(new
                {
                    automations = new[]
                    {
                        new { id = "security-gate-on-review", enabled = true, actions = actions.ToArray() }
                    }
                }));

            File.WriteAllText(
                Path.Combine(agents, "teams.json"),
                JsonSerializer.Serialize(new[] { new { slug = "security-review", agentSlugs = new[] { slug } } }));

            if (fixture)
            {
                var fixtures = Path.Combine(PackRoot, "eval", "fixtures");
                Directory.CreateDirectory(fixtures);
                File.WriteAllText(
                    Path.Combine(fixtures, "security-injection-in-review.json"),
                    JsonSerializer.Serialize(new { Version = 1, Id = "security-injection-in-review", Agent = slug }));
            }

            File.WriteAllText(
                Path.Combine(PackRoot, "pack.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    id = PackId,
                    version = "1.0.0",
                    kind = "specialist",
                    dependsOn = new[] { new { id = "core", minVersion = "1.0.0" } },
                    permissions = new
                    {
                        actions = _actions.ToArray(),
                        allowedWriteGlobs = _writeGlobs.ToArray()
                    },
                    receiptEmitters = new Dictionary<string, string[]>
                    {
                        ["GIGACLAW-VERDICT"] = [slug],
                        ["SECURITY-AUDIT"] = [slug]
                    }
                }));
        }

        public IReadOnlyList<PackCatalogSource> Sources() => PackCatalogSourceReader.Discover(_tmp.Path);

        public CatalogBuild Build() => new CatalogGenerator().Build(_tmp.Path);

        public void Dispose() => _tmp.Dispose();
    }
}

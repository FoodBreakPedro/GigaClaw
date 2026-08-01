using System.Security.Cryptography;
using System.Text.Json;
using GigaClaw.Catalog;
using GigaClaw.Core.Models;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The infrastructure proof for T6: the <c>security-assurance</c> pack installed into a real
/// workspace, verified, and uninstalled again.
///
/// <para>Every other pack test is deliberately synthetic — <c>PackFixture</c> builds minimal packs
/// so a composition failure means the composer is wrong rather than that someone edited a SKILL.
/// This file is the opposite on purpose: it composes the shipped core pack with the shipped
/// Security pack and asserts against the bytes that land in a workspace. A green
/// <c>PackInstallerTests</c> proves the installer; only this proves the product.</para>
///
/// <para>The five bindings (§7) are asserted <b>in the workspace</b>, not in the repository.
/// <c>SecurityAssurancePackTests</c> already reads the pack's own files; what is unproven until
/// here is that composition carries each binding through to the place the runtime reads it —
/// which for the team binding is the whole point of D8.</para>
/// </summary>
public sealed class SecurityPackRoundTripTests
{
    private const string PackId = "security-assurance";
    private const string TeamSlug = "security-review";

    private static readonly string RepositoryRoot = PythonContractRunner.RepositoryRoot;

    private static readonly string[] PackAgents =
        ["secrets-reviewer", "security-auditor", "supply-chain-reviewer", "threat-modeler"];

    /// <summary>The production shape: both packs read out of the embedded image (Q1).</summary>
    private static IReadOnlyList<IPackSource> Sources() =>
        [CorePack.Source(), PackSources.Embedded(PackId)];

    private static async Task<(string Workspace, PackInstallResult Result)> InstallAsync(TempDir tmp)
    {
        var workspace = Path.Combine(tmp.Path, "ws");
        var result = await new PackInstaller().InstallAsync(workspace, Sources());
        return (workspace, result);
    }

    private static string Read(string workspace, string relative) =>
        File.ReadAllText(Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(string workspace, string relative) =>
        File.Exists(Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static void Write(string workspace, string relative, string content)
    {
        var full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static JsonDocument ReadJson(string workspace, string relative) =>
        JsonDocument.Parse(Read(workspace, relative));

    private static AutomationConfig Automations(string workspace) =>
        JsonSerializer.Deserialize<AutomationConfig>(
            Read(workspace, ".agents/automations.json"), AutomationStore.JsonOptions)!;

    // ------------------------------------------------------------------ the five bindings

    [Fact]
    public async Task Binding_1_and_2_every_pack_agent_reaches_the_workspace_with_a_contract_and_a_model_criterion()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        using var contracts = ReadJson(workspace, ".agents/contracts.json");
        using var models = ReadJson(workspace, ".agents/models.json");
        var agentContracts = contracts.RootElement.GetProperty("agents");

        foreach (var slug in PackAgents)
        {
            Assert.True(Exists(workspace, $".agents/{slug}/SKILL.md"), $"{slug}: SKILL.md missing");

            Assert.True(agentContracts.TryGetProperty(slug, out var contract), $"{slug}: no contract entry");
            Assert.False(string.IsNullOrWhiteSpace(contract.GetProperty("riskClass").GetString()),
                $"{slug}: contract carries no riskClass");

            Assert.True(models.RootElement.TryGetProperty(slug, out var model), $"{slug}: no model mapping");
            // §7.2: models.json values are string | {model, criterion}, and the criterion half of
            // binding 2 is the half a generic gate cannot invent.
            Assert.Equal(JsonValueKind.Object, model.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("model").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("criterion").GetString()),
                $"{slug}: model mapping states no criterion");
        }

        // Core's own agents are still there: composition is a union, not a replacement.
        Assert.True(agentContracts.TryGetProperty("programmer", out _));
    }

    [Fact]
    public async Task Binding_4_every_pack_agent_has_an_enabled_dispatching_automation_in_the_workspace()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);
        var automations = Automations(workspace);

        foreach (var slug in PackAgents)
        {
            var dispatches = automations.Automations
                .Where(rule => rule.Enabled)
                .Where(rule =>
                    rule.Actions.OfType<RunAgentActionSpec>().Any(action =>
                        string.Equals(action.Agent, slug, StringComparison.Ordinal)) ||
                    rule.Conditions.OfType<AssignedToConditionSpec>().Any(condition =>
                        condition.Slugs.Contains(slug, StringComparer.Ordinal)))
                .ToList();

            Assert.True(dispatches.Count > 0, $"{slug}: no enabled dispatching automation in the workspace");
        }

        // The three core rosters the pack patches: the patch is what makes its agents assignable,
        // and it has to survive into the workspace file the automation editor writes back to.
        foreach (var id in new[] { "assignee-dispatch", "assignee-resume", "owner-feedback" })
        {
            var rule = automations.Automations.Single(a => a.Id == id);
            var assignees = rule.Conditions.OfType<AssignedToConditionSpec>().Single().Slugs;
            Assert.All(PackAgents, slug => Assert.Contains(slug, assignees));
        }
    }

    [Fact]
    public void Binding_5_every_pack_agent_is_named_by_a_fixture_that_ships_with_the_pack()
    {
        var fixtures = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "Packs", PackId, "eval", "fixtures"), "*.json")
            .Select(path =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.GetProperty("Agent").GetString();
            })
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(PackAgents, slug => Assert.Contains(slug, fixtures));
    }

    // --------------------------------------------------- binding 3: teams as data, end to end

    /// <summary>
    /// The 2026-07-30 structural finding said a pack could not add a team without recompiling
    /// <c>GigaClaw.Core</c>, which made binding 3 unenforceable for packs. D8 moved the roster into
    /// <c>teams.json</c>; this is the proof that the move actually closed the finding — the pack's
    /// team is composed by the installer, resolved by the runtime team filter, and seeded into a
    /// project database, with no C# edit anywhere in the path.
    /// </summary>
    [Fact]
    public async Task Binding_3_the_packs_team_reaches_the_runtime_roster_without_recompiling()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        var roster = new AgentTeamService();
        var team = roster.GetDefinitionBySlug(TeamSlug, workspace);

        Assert.NotNull(team);
        Assert.All(PackAgents, slug => Assert.Contains(slug, team.AgentSlugs));

        // The same service with no workspace still only knows the compiled-in built-ins, which is
        // what makes the assertion above about composed data rather than about a C# constant.
        Assert.Null(roster.GetDefinitionBySlug(TeamSlug));

        // teamMembership: additive seats on teams core owns.
        var engineering = roster.GetDefinitionBySlug(AgentTeamService.SoftwareEngineeringSlug, workspace);
        Assert.NotNull(engineering);
        Assert.Contains("security-auditor", engineering.AgentSlugs);

        var governance = roster.GetDefinitionBySlug(AgentTeamService.GovernanceOpsSlug, workspace);
        Assert.NotNull(governance);
        Assert.Contains("threat-modeler", governance.AgentSlugs);

        // The board's member filter is the surface the owner actually sees.
        var members = PackAgents.Concat(["programmer"])
            .Select(slug => new Member { Slug = slug, Name = slug })
            .ToList();
        var filtered = roster.FilterMembersByTeam(TeamSlug, members, workspace).Select(m => m.Slug).ToList();
        Assert.All(PackAgents, slug => Assert.Contains(slug, filtered));
        Assert.DoesNotContain("programmer", filtered);
    }

    [Fact]
    public async Task Binding_3_the_packs_team_is_seeded_into_a_project_database()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        var projects = new ProjectService(Path.Combine(tmp.Path, "data"));
        var project = await projects.CreateProjectAsync("security-pack-round-trip");
        await projects.UpdateProjectAsync(project.Slug, workspace);

        var teams = new TeamStore(projects, new TicketService(projects, new MemberService(projects)));
        var seeded = await teams.SeedDefinitionsAsync(project.Slug);

        Assert.Contains(TeamSlug, seeded);
        var stored = await teams.GetDefinitionAsync(project.Slug, TeamSlug);
        Assert.NotNull(stored);
        Assert.All(PackAgents, slug => Assert.Contains(slug, stored.AgentSlugs));
    }

    [Fact]
    public void Binding_3_the_catalog_sees_the_packs_team_and_finds_no_gap_in_the_pack()
    {
        var build = new CatalogGenerator().Build(RepositoryRoot);
        var catalog = build.Catalog;

        var team = catalog.Teams.SingleOrDefault(entry => entry.Slug == TeamSlug);
        Assert.NotNull(team);
        Assert.All(PackAgents, slug => Assert.Contains(slug, team.Agents));

        foreach (var slug in PackAgents)
        {
            var entry = catalog.Agents.Single(agent => agent.Slug == slug);
            Assert.Equal(PackId, entry.Pack);
            Assert.Contains(TeamSlug, entry.Teams);
            Assert.True(entry.EvalFixturePresent, $"{slug}: catalog sees no eval fixture");
            Assert.False(string.IsNullOrWhiteSpace(entry.ModelCriterion), $"{slug}: catalog sees no model criterion");
        }

        // The gate the CI workflow runs as `check --strict-packs`.
        var gaps = CatalogGenerator.FindBindingGaps(catalog).Where(gap => gap.Pack == PackId).ToList();
        Assert.Empty(gaps);
        Assert.DoesNotContain(build.PackPolicyViolations, violation => violation.Pack == PackId);
    }

    // ------------------------------------------------------------------------- the round trip

    [Fact]
    public async Task Install_records_every_pack_owned_file_in_the_lockfile()
    {
        using var tmp = new TempDir();
        var (workspace, result) = await InstallAsync(tmp);

        var entry = result.Lock.Find(PackId);
        Assert.NotNull(entry);
        Assert.Equal("specialist", entry.Kind == PackKind.Core ? "core" : "specialist");
        Assert.True(entry.Removable);
        Assert.Equal(PackAgents, entry.Agents.OrderBy(slug => slug, StringComparer.Ordinal));

        foreach (var (relative, hash) in entry.FileHashes)
        {
            Assert.True(Exists(workspace, relative), $"lockfile records {relative}, which is not on disk");
            Assert.Equal(hash, PackFileHash.OfBytes(File.ReadAllBytes(
                Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar)))));
        }

        Assert.Contains(".agents/security-auditor/SKILL.md", entry.FileHashes.Keys);
        Assert.Contains("SECURITY-REVIEW.md", entry.FileHashes.Keys);
        Assert.Contains(".agents/scripts/sbom_diff.py", entry.FileHashes.Keys);

        // Core is in the same lockfile and is refused removal by §4 step 1.
        var core = result.Lock.Find(CorePack.Id);
        Assert.NotNull(core);
        Assert.False(core.Removable);

        // The lockfile on disk is what the next run reads, not the in-memory result.
        var onDisk = PackInstaller.ReadWorkspaceLock(workspace);
        Assert.NotNull(onDisk);
        Assert.Equal(result.InstallId, onDisk.InstallId);
        Assert.Empty(result.QuarantinedPacks);
    }

    [Fact]
    public async Task Uninstall_leaves_an_owner_edited_pack_file_in_place_and_removes_the_rest()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        const string edited = "SECURITY-REVIEW.md";
        const string untouched = ".agents/security-auditor/SKILL.md";
        Write(workspace, edited, "# Our own security review process\n");

        var result = await new PackInstaller().UninstallAsync(workspace, PackId);

        Assert.Contains(edited, result.OrphanedFiles);
        Assert.True(Exists(workspace, edited), "an owner-edited pack file must never be deleted");
        Assert.Equal("# Our own security review process\n", Read(workspace, edited));

        Assert.Contains(untouched, result.DeletedFiles);
        Assert.False(Exists(workspace, untouched));

        // §4 step 5: members are reported orphaned, never deleted.
        Assert.Equal(PackAgents, result.OrphanedMemberSlugs.OrderBy(slug => slug, StringComparer.Ordinal));

        // Merge artifacts lose exactly the pack's keys, and core keeps everything.
        using var contracts = ReadJson(workspace, ".agents/contracts.json");
        var agentContracts = contracts.RootElement.GetProperty("agents");
        Assert.All(PackAgents, slug => Assert.False(agentContracts.TryGetProperty(slug, out _)));
        Assert.True(agentContracts.TryGetProperty("programmer", out _));

        var automations = Automations(workspace);
        Assert.DoesNotContain(automations.Automations, rule => rule.Id == "security-gate-on-review");
        foreach (var id in new[] { "assignee-dispatch", "assignee-resume", "owner-feedback" })
        {
            var assignees = automations.Automations.Single(a => a.Id == id)
                .Conditions.OfType<AssignedToConditionSpec>().Single().Slugs;
            Assert.All(PackAgents, slug => Assert.DoesNotContain(slug, assignees));
            // The subtraction is exactly the pack's slugs — core's roster survives it. That it
            // survives *byte for byte* is what Install_then_uninstall_returns_the_workspace_to_its
            // _core_only_bytes proves; here it is enough that the reversal did not empty it.
            Assert.NotEmpty(assignees);
        }

        // The team goes with the pack; the seats it took on core's teams are subtracted.
        var roster = new AgentTeamService();
        Assert.Null(roster.GetDefinitionBySlug(TeamSlug, workspace));
        var engineering = roster.GetDefinitionBySlug(AgentTeamService.SoftwareEngineeringSlug, workspace);
        Assert.NotNull(engineering);
        Assert.All(PackAgents, slug => Assert.DoesNotContain(slug, engineering.AgentSlugs));
        Assert.NotEmpty(engineering.AgentSlugs);

        // The lockfile is consistent throughout: the pack is gone, core is not.
        Assert.Null(result.Lock.Find(PackId));
        Assert.NotNull(result.Lock.Find(CorePack.Id));
        var onDisk = PackInstaller.ReadWorkspaceLock(workspace);
        Assert.NotNull(onDisk);
        Assert.Null(onDisk.Find(PackId));
    }

    /// <summary>
    /// The round trip proper. Everything the pack wrote is gone and everything core wrote is
    /// byte-identical — which is a stronger claim than "uninstall deleted its files", because it
    /// also catches a merge artifact rewritten with a stray key, a reordered array, or a changed
    /// indentation.
    /// </summary>
    [Fact]
    public async Task Install_then_uninstall_returns_the_workspace_to_its_core_only_bytes()
    {
        using var tmp = new TempDir();
        var installer = new PackInstaller();

        var coreOnly = Path.Combine(tmp.Path, "core-only");
        await installer.InstallAsync(coreOnly, [CorePack.Source()]);
        var expected = Snapshot(coreOnly);

        var workspace = Path.Combine(tmp.Path, "ws");
        await installer.InstallAsync(workspace, Sources());
        await installer.UninstallAsync(workspace, PackId);

        Assert.Equal(expected, Snapshot(workspace));
    }

    /// <summary>
    /// The per-project drift checker is the tool an owner runs to ask "is this workspace still the
    /// one the template describes?". A workspace with a pack installed must answer yes: pack
    /// content is declared in the lockfile, so it is installed state, not drift.
    /// </summary>
    [Fact]
    public async Task The_workspace_drift_check_stays_green_with_the_pack_installed()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        var report = WorkspaceDriftChecker.Check(workspace);

        Assert.False(report.HasDrift, string.Join("\n", report.Drift));
    }

    /// <summary>
    /// The widening is <em>declared</em>, not assumed: strip the lockfile and the same workspace
    /// reads as drifted again. Without this, "no drift" would only prove the checker had stopped
    /// looking at pack-shaped content.
    /// </summary>
    [Fact]
    public async Task Pack_content_reads_as_drift_again_once_the_lockfile_no_longer_vouches_for_it()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        File.Delete(Path.Combine(workspace, ".agents", PackLockFile.FileName));
        var report = WorkspaceDriftChecker.Check(workspace);

        Assert.True(report.HasDrift);
        Assert.Contains(report.Drift, drift =>
            drift.Kind == DriftKind.Extra &&
            drift.Detail.Contains("security-gate-on-review", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_workspace_drift_check_still_reports_a_real_edit_to_a_pack_patched_automation()
    {
        using var tmp = new TempDir();
        var (workspace, _) = await InstallAsync(tmp);

        var automations = Automations(workspace);
        automations.Automations.Single(rule => rule.Id == "assignee-dispatch").Enabled = false;
        Write(workspace, ".agents/automations.json",
            JsonSerializer.Serialize(automations, AutomationStore.JsonOptions));

        var report = WorkspaceDriftChecker.Check(workspace);

        Assert.Contains(report.Drift, drift =>
            drift.Kind == DriftKind.Modified && drift.Detail.Contains("assignee-dispatch", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------- helpers

    private static SortedDictionary<string, string> Snapshot(string workspace)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            // The lockfile carries a fresh installId and timestamp on every install, so its bytes
            // can never match across two workspaces. Its *content* is asserted elsewhere.
            if (relative == ".agents/" + PackLockFile.FileName) continue;
            snapshot[relative] = Sha256(file);
        }
        return snapshot;
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

}

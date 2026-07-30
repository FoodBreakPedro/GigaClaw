using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;
using Xunit;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// doc/pack-infrastructure.md §5 describes quarantine as load-time behaviour, but until now nothing
/// read the inputs the install already records. These tests cover the enforcement half: a pack whose
/// <c>requiresRuntime.max</c> is below this build has its automations force-disabled at config load
/// and its agents refused at dispatch, while its files stay exactly where they are.
/// </summary>
public sealed class PackQuarantineTests
{
    private const int Newer = 2;

    private static IPackSource OldPack(string packsRoot, string id = "pack-old") =>
        PackFixture.Create(packsRoot, id, minRuntime: 1, maxRuntime: 1)
            .Agent("old-agent")
            .Permits(actions: new[] { "runAgent" }, riskClasses: new[] { "legacy" }, writeGlobs: new[] { "doc/**" })
            .Contracts(new JsonObject { ["old-agent"] = PackFixture.Contract("legacy", "doc/**") })
            .Automations(new JsonArray(
                PackFixture.RunAgentAutomation("old-gate", "old-agent", new[] { "code" })))
            .Build();

    private static IPackSource CurrentPack(string packsRoot, string id = "pack-current") =>
        PackFixture.Create(packsRoot, id, minRuntime: 1, maxRuntime: Newer)
            .Agent("current-agent")
            .Permits(actions: new[] { "runAgent" }, riskClasses: new[] { "modern" }, writeGlobs: new[] { "doc/**" })
            .Contracts(new JsonObject { ["current-agent"] = PackFixture.Contract("modern", "doc/**") })
            .Automations(new JsonArray(
                PackFixture.RunAgentAutomation("current-gate", "current-agent", new[] { "code" })))
            .Build();

    [Fact]
    public async Task A_pack_past_its_runtime_ceiling_has_its_agents_and_automations_quarantined()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");

        await new PackInstaller().InstallAsync(
            workspace,
            new[] { OldPack(packs), CurrentPack(packs) },
            new PackInstallOptions(RuntimeVersion: Newer));

        PackQuarantine.Invalidate(workspace);
        var quarantine = PackQuarantine.ForWorkspace(workspace, Newer);

        Assert.Equal(new[] { "pack-old" }, quarantine.PackIds);
        Assert.Equal("pack-old", quarantine.PackOfAgent("old-agent"));
        Assert.Equal("pack-old", quarantine.PackOfAutomation("old-gate"));

        // The compatible pack beside it is untouched — quarantine is per pack, not per workspace.
        Assert.Null(quarantine.PackOfAgent("current-agent"));
        Assert.Null(quarantine.PackOfAutomation("current-gate"));

        // §5: files stay on disk. Never auto-upgraded, never auto-removed.
        Assert.True(File.Exists(Path.Combine(workspace, ".agents", "old-agent", "SKILL.md")));
    }

    [Fact]
    public async Task The_same_packs_are_clean_when_the_runtime_still_fits()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");

        await new PackInstaller().InstallAsync(
            workspace, new[] { OldPack(packs), CurrentPack(packs) }, new PackInstallOptions(RuntimeVersion: 1));

        PackQuarantine.Invalidate(workspace);
        var quarantine = PackQuarantine.ForWorkspace(workspace, runtimeVersion: 1);

        Assert.True(quarantine.IsEmpty);
        Assert.Null(quarantine.PackOfAgent("old-agent"));
    }

    [Fact]
    public async Task Config_load_force_disables_a_quarantined_packs_automations_only()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");

        await new PackInstaller().InstallAsync(
            workspace,
            new[] { OldPack(packs), CurrentPack(packs) },
            new PackInstallOptions(RuntimeVersion: Newer));

        PackQuarantine.Invalidate(workspace);

        // Both automations are enabled on disk; the quarantine decision is applied on the way out,
        // which is what keeps the owner's file honest about what the pack shipped.
        var config = Load(Path.Combine(workspace, ".agents", "automations.json"));
        Assert.All(config.Automations, a => Assert.True(a.Enabled));

        AutomationStore.ApplyQuarantine(config, workspace, Newer);

        Assert.False(Single(config, "old-gate").Enabled);
        Assert.True(Single(config, "current-gate").Enabled);
    }

    [Fact]
    public void A_workspace_with_no_lockfile_quarantines_nothing()
    {
        using var tmp = new TempDir();
        Assert.True(PackQuarantine.ForWorkspace(tmp.Path).IsEmpty);
        Assert.True(PackQuarantine.ForWorkspace("").IsEmpty);
    }

    [Fact]
    public void An_unreadable_lockfile_degrades_to_quarantining_nothing()
    {
        using var tmp = new TempDir();
        var agents = Path.Combine(tmp.Path, ".agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, PackLockFile.FileName), "{ not json");

        // A restriction that cannot be read must not take the automation engine down; it degrades
        // to the behaviour of a workspace that predates packs entirely.
        PackQuarantine.Invalidate(tmp.Path);
        Assert.True(PackQuarantine.ForWorkspace(tmp.Path).IsEmpty);
    }

    private static AutomationConfig Load(string path) =>
        JsonSerializer.Deserialize<AutomationConfig>(File.ReadAllBytes(path), AutomationStore.JsonOptions)!;

    private static GigaClaw.Core.Automation.Automation Single(AutomationConfig config, string id) =>
        Assert.Single(config.Automations, a => a.Id == id);
}

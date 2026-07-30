using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// Uninstall semantics from doc/pack-infrastructure.md §4. Every assertion here is about the same
/// promise: the pack takes back exactly what it installed and untouched, and everything the owner
/// has since edited is left in place and reported.
/// </summary>
public sealed class PackUninstallTests
{
    private static PackFixture SecurityPack(string packsRoot, string id = "security-assurance") =>
        PackFixture.Create(packsRoot, id)
            .Agent("security-auditor")
            .Permits(
                actions: new[] { "runAgent" },
                riskClasses: new[] { "security-review" },
                writeGlobs: new[] { "doc/security/**" })
            .Contracts(new JsonObject
            {
                ["security-auditor"] = PackFixture.Contract("security-review", "doc/security/**"),
            })
            .Models(new JsonObject { ["security-auditor"] = "claude-sonnet-4-6" })
            .Automations(new JsonArray(
                PackFixture.RunAgentAutomation("security-gate-on-review", "security-auditor", new[] { "code" })))
            .RootFile("SECURITY-REVIEW.md", "# Security review\n");

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

    private static AutomationConfig Automations(string workspace) =>
        JsonSerializer.Deserialize<AutomationConfig>(
            Read(workspace, ".agents/automations.json"), AutomationStore.JsonOptions)!;

    [Fact]
    public async Task Uninstall_deletes_pack_owned_files_and_clears_the_lockfile_entry()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Contains(".agents/security-auditor/SKILL.md", result.DeletedFiles);
        Assert.Contains("SECURITY-REVIEW.md", result.DeletedFiles);
        Assert.Empty(result.OrphanedFiles);
        Assert.False(Exists(workspace, ".agents/security-auditor/SKILL.md"));
        Assert.False(Exists(workspace, "SECURITY-REVIEW.md"));

        // The emptied agent directory goes too, but .agents/ itself stays.
        Assert.False(Directory.Exists(Path.Combine(workspace, ".agents", "security-auditor")));
        Assert.True(Directory.Exists(Path.Combine(workspace, ".agents")));

        Assert.Empty(result.Lock.Packs);
        Assert.Empty(PackInstaller.ReadWorkspaceLock(workspace)!.Packs);
    }

    [Fact]
    public async Task Uninstall_leaves_an_owner_edited_file_in_place_and_reports_it_as_orphaned()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        Write(workspace, ".agents/security-auditor/SKILL.md", "# my heavily customised auditor\n");

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Contains(".agents/security-auditor/SKILL.md", result.OrphanedFiles);
        Assert.DoesNotContain(".agents/security-auditor/SKILL.md", result.DeletedFiles);
        Assert.Equal("# my heavily customised auditor\n", Read(workspace, ".agents/security-auditor/SKILL.md"));
    }

    [Fact]
    public async Task Uninstall_never_touches_agent_memory_written_at_runtime()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        // Written by the consolidation pass after install; claimed by no manifest.
        Write(workspace, ".agents/security-auditor/memory/topic-injection-paths.md", "# learned");

        await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Equal("# learned", Read(workspace, ".agents/security-auditor/memory/topic-injection-paths.md"));
        // MEMORY.md was pack-owned and untouched, so it went; the runtime topic file kept the dir.
        Assert.False(Exists(workspace, ".agents/security-auditor/memory/MEMORY.md"));
    }

    [Fact]
    public async Task Uninstall_refuses_a_pack_that_declares_itself_not_removable()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var core = PackFixture.Create(tmp.Path, "core", kind: "core", removable: false)
            .Agent("qa-tester").Build();
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { core });

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => installer.UninstallAsync(workspace, "core"));

        Assert.Contains("removable:false", error.Message);
        Assert.True(Exists(workspace, ".agents/qa-tester/SKILL.md"));
    }

    [Fact]
    public async Task Uninstall_refuses_while_another_installed_pack_depends_on_it()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");
        var baseline = PackFixture.Create(packs, "pack-base").Agent("base-agent").Build();
        var dependent = PackFixture.Create(packs, "pack-dependent")
            .Agent("dependent-agent").DependsOn("pack-base").Build();

        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { baseline, dependent });

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => installer.UninstallAsync(workspace, "pack-base"));

        Assert.Contains("pack-dependent still depend on it", error.Message);
        Assert.True(Exists(workspace, ".agents/base-agent/SKILL.md"));

        // Removing the dependent first unblocks it.
        await installer.UninstallAsync(workspace, "pack-dependent");
        await installer.UninstallAsync(workspace, "pack-base");
        Assert.False(Exists(workspace, ".agents/base-agent/SKILL.md"));
    }

    [Fact]
    public async Task Uninstall_removes_an_untouched_pack_automation()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Equal(new[] { "security-gate-on-review" }, result.RemovedAutomations);
        Assert.Empty(result.DisabledAutomations);
        Assert.Empty(Automations(workspace).Automations);
    }

    [Fact]
    public async Task Uninstall_disables_rather_than_deletes_an_owner_edited_pack_automation()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        // The owner retunes the automation in the UI; AutomationStore.SaveAsync writes it back.
        var edited = Automations(workspace);
        edited.Automations[0].Name = "Security: my tuned gate";
        Write(workspace, ".agents/automations.json",
            JsonSerializer.Serialize(edited, AutomationStore.JsonOptions));

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Equal(new[] { "security-gate-on-review" }, result.DisabledAutomations);
        Assert.Empty(result.RemovedAutomations);

        var after = Assert.Single(Automations(workspace).Automations);
        Assert.Equal("Security: my tuned gate", after.Name);
        // Disabled, because a dangling automation referencing a removed agent would fire and fail.
        Assert.False(after.Enabled);
    }

    [Fact]
    public async Task Uninstall_reverses_an_automationPatch_as_a_set_subtraction()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Write(workspace, ".agents/automations.json", new JsonObject
        {
            ["automations"] = new JsonArray(
                PackFixture.AssigneeDispatchAutomation("assignee-dispatch", "programmer", "qa-tester")),
        }.ToJsonString());
        Write(workspace, ".agents/programmer/SKILL.md", "# programmer");
        Write(workspace, ".agents/qa-tester/SKILL.md", "# qa");

        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[]
        {
            SecurityPack(Path.Combine(tmp.Path, "packs"))
                .Patch("assignee-dispatch", "addAssignees", "security-auditor")
                .Build(),
        });

        await installer.UninstallAsync(workspace, "security-assurance");

        var dispatch = Assert.Single(Automations(workspace).Automations, a => a.Id == "assignee-dispatch");
        var condition = Assert.Single(dispatch.Conditions.OfType<AssignedToConditionSpec>());
        Assert.Equal(new[] { "programmer", "qa-tester" }, condition.Slugs);
        Assert.True(dispatch.Enabled);
    }

    [Fact]
    public async Task Uninstall_removes_contract_and_model_keys_but_leaves_owner_edited_entries()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");

        var pack = PackFixture.Create(packs, "pack-two")
            .Agent("kept-agent")
            .Agent("gone-agent")
            .Permits(riskClasses: new[] { "docs-write" }, writeGlobs: new[] { "doc/**" })
            .Contracts(new JsonObject
            {
                ["kept-agent"] = PackFixture.Contract("docs-write", "doc/**"),
                ["gone-agent"] = PackFixture.Contract("docs-write", "doc/**"),
            })
            .Models(new JsonObject
            {
                ["kept-agent"] = "claude-haiku-4-5",
                ["gone-agent"] = "claude-haiku-4-5",
            })
            .Build();

        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { pack });

        // The owner retunes one contract and one model mapping.
        var contracts = JsonNode.Parse(Read(workspace, ".agents/contracts.json"))!.AsObject();
        contracts["agents"]!["kept-agent"]!["ticketExit"] = new JsonArray("Done");
        Write(workspace, ".agents/contracts.json", contracts.ToJsonString());

        var models = JsonNode.Parse(Read(workspace, ".agents/models.json"))!.AsObject();
        models["kept-agent"] = "claude-opus-4-8";
        Write(workspace, ".agents/models.json", models.ToJsonString());

        var result = await installer.UninstallAsync(workspace, "pack-two");

        Assert.Equal(new[] { "gone-agent" }, result.RemovedContractKeys);
        Assert.Equal(new[] { "gone-agent" }, result.RemovedModelKeys);
        Assert.Contains("contracts.json#kept-agent", result.OrphanedMergeEntries);
        Assert.Contains("models.json#kept-agent", result.OrphanedMergeEntries);

        var contractsAfter = JsonNode.Parse(Read(workspace, ".agents/contracts.json"))!.AsObject();
        Assert.True(contractsAfter["agents"]!.AsObject().ContainsKey("kept-agent"));
        Assert.False(contractsAfter["agents"]!.AsObject().ContainsKey("gone-agent"));

        var modelsAfter = JsonNode.Parse(Read(workspace, ".agents/models.json"))!.AsObject();
        Assert.Equal("claude-opus-4-8", modelsAfter["kept-agent"]!.GetValue<string>());
        Assert.False(modelsAfter.ContainsKey("gone-agent"));
    }

    [Fact]
    public async Task Uninstall_removes_the_packs_team_and_drops_its_slugs_from_teams_it_joined()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");

        var core = PackFixture.Create(packs, "core", kind: "core", removable: false)
            .Agent("qa-tester")
            .Teams(new JsonArray(new JsonObject
            {
                ["slug"] = "software-engineering",
                ["name"] = "Software Engineering",
                ["agentSlugs"] = new JsonArray("qa-tester"),
            }))
            .Build();
        var pack = SecurityPack(packs)
            .Teams(new JsonArray(new JsonObject
            {
                ["slug"] = "security-review",
                ["name"] = "Security Review",
                ["agentSlugs"] = new JsonArray("security-auditor"),
            }))
            .TeamMembership("software-engineering", "security-auditor")
            .Build();

        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { pack, core });

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        Assert.Equal(new[] { "security-review" }, result.RemovedTeams);
        var teams = PackComposer.TeamsArrayOf(JsonNode.Parse(Read(workspace, ".agents/teams.json")))!;
        var engineering = Assert.Single(teams.OfType<JsonObject>());
        Assert.Equal("software-engineering", engineering["slug"]!.GetValue<string>());
        // teamMembership reversed as a set subtraction; the core member is untouched.
        Assert.Equal(new[] { "qa-tester" }, engineering["agentSlugs"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task Uninstall_reports_agents_as_orphaned_members_rather_than_deleting_them()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        var result = await installer.UninstallAsync(workspace, "security-assurance");

        // Member rows carry DefaultModel and are referenced by run history and by assignedTo on
        // historical tickets, so uninstall reports them for the UI to mark orphaned.
        Assert.Equal(new[] { "security-auditor" }, result.OrphanedMemberSlugs);
    }

    [Fact]
    public async Task Uninstall_refuses_a_pack_that_is_not_installed()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => installer.UninstallAsync(workspace, "never-installed"));
        Assert.Contains("is not installed", error.Message);
    }

    [Fact]
    public async Task Uninstall_refuses_a_workspace_with_no_lockfile()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Directory.CreateDirectory(workspace);

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => new PackInstaller().UninstallAsync(workspace, "security-assurance"));
        Assert.Contains("nothing is installed", error.Message);
    }

    [Fact]
    public async Task Install_uninstall_install_returns_the_workspace_to_the_same_bytes()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();
        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();

        await installer.InstallAsync(workspace, new[] { pack });
        var afterFirst = Snapshot(workspace);

        await installer.UninstallAsync(workspace, "security-assurance");
        await installer.InstallAsync(workspace, new[] { pack });

        Assert.Equal(afterFirst, Snapshot(workspace));
    }

    /// <summary>Every workspace path except the lockfile, whose installId is a fresh guid.</summary>
    private static SortedDictionary<string, string> Snapshot(string workspace)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            if (relative.EndsWith(PackLockFile.FileName, StringComparison.Ordinal)) continue;
            map[relative] = PackFileHash.OfBytes(File.ReadAllBytes(file));
        }
        return map;
    }
}

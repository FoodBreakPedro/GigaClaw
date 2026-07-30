using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The staged install of doc/pack-infrastructure.md §4 (D5): validate in memory, stage, merge per
/// file with pre-images, write the lockfile last. The tests that matter most here are the ones
/// proving the installer never touches a path no manifest claims — <c>.agents/**</c> also holds
/// live runtime state and the owner's edited <c>automations.json</c>.
/// </summary>
public sealed class PackInstallerTests
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

    [Fact]
    public async Task Install_writes_exactly_the_paths_the_manifest_claims()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Directory.CreateDirectory(workspace);
        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();

        var result = await new PackInstaller().InstallAsync(workspace, new[] { pack });

        Assert.Equal(
            new[]
            {
                ".agents/contracts.json",
                ".agents/models.json",
                ".agents/security-auditor/SKILL.md",
                ".agents/security-auditor/memory/MEMORY.md",
                ".agents/automations.json",
                "SECURITY-REVIEW.md",
            }.Order(StringComparer.Ordinal),
            result.Written.Order(StringComparer.Ordinal));

        // eval/** is build-time only and never reaches a workspace; the staging directory is gone.
        Assert.Empty(Directory.EnumerateDirectories(workspace, PackInstaller.StagingPrefix + "*"));
        Assert.True(Exists(workspace, ".agents/packs.lock.json"));
    }

    [Fact]
    public async Task Install_records_a_hash_for_every_pack_owned_file_in_the_lockfile()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();

        var result = await new PackInstaller().InstallAsync(workspace, new[] { pack });

        var entry = Assert.Single(result.Lock.Packs);
        Assert.Equal("security-assurance", entry.Id);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(new[] { "security-auditor" }, entry.Agents);
        Assert.Equal(new[] { "security-gate-on-review" }, entry.Automations);
        Assert.Equal(new[] { "security-auditor" }, entry.ContractKeys);
        Assert.Equal(new[] { "security-auditor" }, entry.ModelKeys);

        // fileHashes covers the opaque files only. The four merge artifacts are tracked as key
        // lists, because no single pack owns those files on disk.
        Assert.Equal(
            new[] { ".agents/security-auditor/SKILL.md", ".agents/security-auditor/memory/MEMORY.md", "SECURITY-REVIEW.md" }
                .Order(StringComparer.Ordinal),
            entry.FileHashes.Keys.Order(StringComparer.Ordinal));
        Assert.All(entry.FileHashes.Values, h => Assert.StartsWith("sha256:", h));
        Assert.DoesNotContain(".agents/automations.json", entry.FileHashes.Keys);

        var onDisk = PackInstaller.ReadWorkspaceLock(workspace);
        Assert.NotNull(onDisk);
        Assert.Equal(result.InstallId, onDisk!.InstallId);
        Assert.Equal(PackRuntime.Version, onDisk.PackRuntimeVersion);
    }

    [Fact]
    public async Task Install_leaves_live_runtime_state_and_unclaimed_paths_untouched()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");

        // The exact runtime state §4 says a wholesale .agents swap would destroy.
        Write(workspace, ".agents/evaluator/memory/scores.json", "{\"evaluator\":42}");
        Write(workspace, ".agents/documentalist/memory/state.json", "{\"lastCommit\":\"abc\"}");
        Write(workspace, ".agents/producer/memory/topic-release-cadence.md", "# consolidated topic");
        Write(workspace, ".agents/preamble.md", "core preamble");
        Write(workspace, "CLAUDE.md", "workspace guide");

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();
        await new PackInstaller().InstallAsync(workspace, new[] { pack });

        Assert.Equal("{\"evaluator\":42}", Read(workspace, ".agents/evaluator/memory/scores.json"));
        Assert.Equal("{\"lastCommit\":\"abc\"}", Read(workspace, ".agents/documentalist/memory/state.json"));
        Assert.Equal("# consolidated topic", Read(workspace, ".agents/producer/memory/topic-release-cadence.md"));
        Assert.Equal("core preamble", Read(workspace, ".agents/preamble.md"));
        Assert.Equal("workspace guide", Read(workspace, "CLAUDE.md"));
    }

    [Fact]
    public async Task Install_merges_into_an_owner_edited_automations_json_rather_than_replacing_it()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");

        // What AutomationStore.SaveAsync leaves behind when the owner edits in the UI.
        var owned = new AutomationConfig
        {
            DailyBudgetUsd = 12.5m,
            Automations =
            {
                new AutomationRule
                {
                    Id = "owner-nightly",
                    Name = "Owner's nightly job",
                    Trigger = new IntervalTriggerSpec { Cron = "0 3 * * *" },
                },
            },
        };
        Write(workspace, ".agents/automations.json",
            JsonSerializer.Serialize(owned, AutomationStore.JsonOptions));

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();
        await new PackInstaller().InstallAsync(workspace, new[] { pack });

        var merged = JsonSerializer.Deserialize<AutomationConfig>(
            Read(workspace, ".agents/automations.json"), AutomationStore.JsonOptions)!;

        Assert.Equal(12.5m, merged.DailyBudgetUsd);
        Assert.Equal(
            new[] { "owner-nightly", "security-gate-on-review" },
            merged.Automations.Select(a => a.Id));
    }

    [Fact]
    public async Task Install_applies_an_addAssignees_patch_to_an_automation_already_in_the_workspace()
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

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs"))
            .Patch("assignee-dispatch", "addAssignees", "security-auditor")
            .Build();

        await new PackInstaller().InstallAsync(workspace, new[] { pack });

        var merged = JsonSerializer.Deserialize<AutomationConfig>(
            Read(workspace, ".agents/automations.json"), AutomationStore.JsonOptions)!;
        var dispatch = Assert.Single(merged.Automations, a => a.Id == "assignee-dispatch");
        var condition = Assert.Single(dispatch.Conditions.OfType<AssignedToConditionSpec>());

        // Set addition, in place, order preserved — which is what makes uninstall a subtraction.
        Assert.Equal(new[] { "programmer", "qa-tester", "security-auditor" }, condition.Slugs);
    }

    [Fact]
    public async Task Install_refuses_a_patch_whose_target_automation_exists_nowhere()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var pack = SecurityPack(Path.Combine(tmp.Path, "packs"))
            .Patch("no-such-automation", "addAssignees", "security-auditor")
            .Build();

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => new PackInstaller().InstallAsync(workspace, new[] { pack }));

        Assert.Contains(error.Errors, e => e.Contains("in neither the selection nor the workspace"));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".agents")));
    }

    [Fact]
    public async Task Install_preserves_an_owner_edited_pack_file_and_reports_it()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var packs = Path.Combine(tmp.Path, "packs");
        var installer = new PackInstaller();

        await installer.InstallAsync(workspace, new[] { SecurityPack(packs).Build() });
        Write(workspace, "SECURITY-REVIEW.md", "# my own notes\n");

        // Rebuild the same pack from a fresh directory so its bytes differ from the edit.
        var again = SecurityPack(Path.Combine(tmp.Path, "packs2")).Build();
        var result = await installer.InstallAsync(workspace, new[] { again });

        Assert.Contains("SECURITY-REVIEW.md", result.PreservedOwnerEdits);
        Assert.Equal("# my own notes\n", Read(workspace, "SECURITY-REVIEW.md"));

        // Not recorded as pack-owned, so uninstall will not delete it either.
        Assert.DoesNotContain("SECURITY-REVIEW.md", result.Lock.Find("security-assurance")!.FileHashes.Keys);
    }

    [Fact]
    public async Task Install_overwrites_an_owner_edited_pack_file_only_when_asked()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();

        await installer.InstallAsync(workspace, new[] { SecurityPack(Path.Combine(tmp.Path, "packs")).Build() });
        Write(workspace, "SECURITY-REVIEW.md", "# my own notes\n");

        var result = await installer.InstallAsync(
            workspace,
            new[] { SecurityPack(Path.Combine(tmp.Path, "packs2")).Build() },
            new PackInstallOptions(OverwriteConflicts: true));

        Assert.Empty(result.PreservedOwnerEdits);
        Assert.Equal("# Security review\n", Read(workspace, "SECURITY-REVIEW.md"));
    }

    [Fact]
    public async Task Install_is_refused_when_a_slug_already_exists_in_the_workspace_unowned()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Write(workspace, ".agents/security-auditor/SKILL.md", "# someone else's auditor");

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();

        var error = await Assert.ThrowsAsync<PackValidationException>(
            () => new PackInstaller().InstallAsync(workspace, new[] { pack }));

        Assert.Contains(error.Errors, e => e.Contains("one flat global namespace"));
        Assert.Equal("# someone else's auditor", Read(workspace, ".agents/security-auditor/SKILL.md"));
        Assert.False(Exists(workspace, ".agents/packs.lock.json"));
    }

    [Fact]
    public async Task Install_that_fails_mid_merge_leaves_no_residue()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Write(workspace, "SECURITY-REVIEW.md", "# pre-existing\n");
        Write(workspace, ".agents/automations.json", new JsonObject { ["automations"] = new JsonArray() }.ToJsonString());
        var before = Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToList();

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();
        var installer = new PackInstaller();
        var merged = new List<string>();
        installer.BeforeFileMerge = destination =>
        {
            merged.Add(destination);
            if (merged.Count == 3) throw new IOException("disk full halfway through the merge");
        };

        await Assert.ThrowsAsync<IOException>(
            () => installer.InstallAsync(workspace, new[] { pack }, new PackInstallOptions(OverwriteConflicts: true)));

        // Pre-images restored, files created by the failed merge removed, staging swept, and the
        // lockfile — written last — never appeared, so the install is uncommitted.
        Assert.Equal(
            before,
            Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal));
        Assert.Equal("# pre-existing\n", Read(workspace, "SECURITY-REVIEW.md"));
        Assert.False(Exists(workspace, ".agents/packs.lock.json"));
        Assert.Empty(Directory.EnumerateDirectories(workspace, PackInstaller.StagingPrefix + "*"));
    }

    [Fact]
    public async Task Install_sweeps_a_staging_directory_left_by_an_interrupted_install()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        Write(workspace, PackInstaller.StagingPrefix + "abandoned/.agents/ghost.md", "leftover");

        var pack = SecurityPack(Path.Combine(tmp.Path, "packs")).Build();
        await new PackInstaller().InstallAsync(workspace, new[] { pack });

        Assert.Empty(Directory.EnumerateDirectories(workspace, PackInstaller.StagingPrefix + "*"));
    }

    [Fact]
    public async Task Install_is_idempotent_and_reinstall_drops_files_the_new_version_no_longer_claims()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var installer = new PackInstaller();

        var v1 = SecurityPack(Path.Combine(tmp.Path, "packs1"))
            .Agent("secrets-reviewer")
            .Build();
        await installer.InstallAsync(workspace, new[] { v1 });
        Assert.True(Exists(workspace, ".agents/secrets-reviewer/SKILL.md"));

        // The same pack id built again without secrets-reviewer.
        var v2 = SecurityPack(Path.Combine(tmp.Path, "packs2")).Build();
        var result = await installer.InstallAsync(workspace, new[] { v2 });

        Assert.False(Exists(workspace, ".agents/secrets-reviewer/SKILL.md"));
        Assert.True(Exists(workspace, ".agents/security-auditor/SKILL.md"));
        Assert.Equal(new[] { "security-auditor" }, result.Lock.Find("security-assurance")!.Agents);

        // Repeating the same install changes nothing else.
        var again = await installer.InstallAsync(workspace, new[] { v2 });
        Assert.Empty(again.PreservedOwnerEdits);
    }

    [Fact]
    public async Task Install_reports_a_quarantined_pack_instead_of_refusing_it()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var pack = PackFixture.Create(tmp.Path, "pack-old", minRuntime: 1, maxRuntime: 1)
            .Agent("old-agent").Build();

        var result = await new PackInstaller().InstallAsync(
            workspace, new[] { pack }, new PackInstallOptions(RuntimeVersion: 2));

        Assert.Equal(new[] { "pack-old" }, result.QuarantinedPacks);
        Assert.True(Exists(workspace, ".agents/old-agent/SKILL.md"));
        Assert.Equal(new PackRuntimeRequirement(1, 1), result.Lock.Find("pack-old")!.RequiresRuntime);
    }

    [Fact]
    public async Task Install_defers_a_teamMembership_whose_target_team_is_not_data_yet()
    {
        using var tmp = new TempDir();
        var workspace = Path.Combine(tmp.Path, "ws");
        var pack = SecurityPack(Path.Combine(tmp.Path, "packs"))
            .Teams(new JsonArray(new JsonObject
            {
                ["slug"] = "security-review",
                ["name"] = "Security Review",
                ["agentSlugs"] = new JsonArray("security-auditor"),
            }))
            .TeamMembership("software-engineering", "security-auditor")
            .Build();

        var result = await new PackInstaller().InstallAsync(workspace, new[] { pack });

        // The nine built-in teams are still C# constants, so the membership cannot be applied to
        // data. Reported, never silently dropped; it becomes a hard error once core ships teams.json.
        Assert.Contains(result.DeferredTeamMemberships, m => m.Contains("software-engineering"));

        var teams = JsonNode.Parse(Read(workspace, ".agents/teams.json"))!.AsArray();
        Assert.Equal("security-review", teams[0]!["slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_applies_teamMembership_to_a_team_owned_by_another_pack()
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
            .TeamMembership("software-engineering", "security-auditor")
            .Build();

        var result = await new PackInstaller().InstallAsync(workspace, new[] { pack, core });

        Assert.Empty(result.DeferredTeamMemberships);
        var teams = JsonNode.Parse(Read(workspace, ".agents/teams.json"))!.AsArray();
        var engineering = Assert.Single(teams.OfType<JsonObject>(),
            t => t["slug"]!.GetValue<string>() == "software-engineering");
        Assert.Equal(
            new[] { "qa-tester", "security-auditor" },
            engineering["agentSlugs"]!.AsArray().Select(s => s!.GetValue<string>()));
    }
}

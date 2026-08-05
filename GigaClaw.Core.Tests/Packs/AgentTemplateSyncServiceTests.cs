using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

public sealed class AgentTemplateSyncServiceTests
{
    [Fact]
    public async Task Preview_is_read_only_and_apply_updates_an_unchanged_core_file()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var path = Full(tmp.Path, ".agents/preamble.md");
        var before = await File.ReadAllTextAsync(path);
        var source = Override(agentFiles: new() { ["preamble.md"] = Encoding.UTF8.GetBytes(before + "\nnew core line\n") });
        var service = new AgentTemplateSyncService(source);

        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.Update);

        var result = await service.ApplyAsync(tmp.Path, preview.PlanToken);

        Assert.EndsWith("new core line\n", await File.ReadAllTextAsync(path));
        Assert.Contains(".agents/preamble.md", result.AppliedPaths);
    }

    [Fact]
    public async Task Apply_installs_a_new_core_file()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        const string relative = ".agents/blog-writer/references/sync-new.md";
        var source = Override(agentFiles: new()
        {
            ["blog-writer/references/sync-new.md"] = Encoding.UTF8.GetBytes("# New reference\n"),
        });
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == relative && change.Kind == AgentTemplateSyncChangeKind.Add);

        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        Assert.Equal("# New reference\n", await File.ReadAllTextAsync(Full(tmp.Path, relative)));
    }

    [Fact]
    public async Task Apply_preserves_an_owner_modified_core_file()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var path = Full(tmp.Path, ".agents/preamble.md");
        await File.WriteAllTextAsync(path, "owner copy\n");
        var source = Override(agentFiles: new() { ["preamble.md"] = Encoding.UTF8.GetBytes("new core copy\n") });
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.Conflict);

        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        Assert.Equal("owner copy\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Apply_preserves_a_locally_deleted_managed_file()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var path = Full(tmp.Path, ".agents/preamble.md");
        File.Delete(path);
        var source = Override(agentFiles: new() { ["preamble.md"] = Encoding.UTF8.GetBytes("new core copy\n") });
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.DeletedByOwner);

        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Apply_never_changes_or_recreates_memory_files()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        const string relative = ".agents/blog-writer/memory/MEMORY.md";
        var path = Full(tmp.Path, relative);
        await File.WriteAllTextAsync(path, "owner memory\n");
        var source = Override(agentFiles: new()
        {
            ["blog-writer/memory/MEMORY.md"] = Encoding.UTF8.GetBytes("new template memory\n"),
        });
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == relative && change.Kind == AgentTemplateSyncChangeKind.SkippedMemory);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);
        Assert.Equal("owner memory\n", await File.ReadAllTextAsync(path));

        File.Delete(path);
        preview = await service.PreviewAsync(tmp.Path);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Preview_ignores_non_agent_template_files()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var dashboardOutput = Full(tmp.Path, ".dashboard/content-health/output.json");
        await File.WriteAllTextAsync(dashboardOutput, "owner dashboard output\n");

        var preview = await new AgentTemplateSyncService(CorePack.Source()).PreviewAsync(tmp.Path);

        Assert.DoesNotContain(preview.Changes, change =>
            change.RelativePath.StartsWith(".dashboard/", StringComparison.Ordinal));
        Assert.All(preview.Changes, change => Assert.StartsWith(".agents/", change.RelativePath));
    }

    [Fact]
    public async Task Apply_removes_a_retired_unmodified_core_file_but_preserves_a_modified_one()
    {
        using var first = new TempDir();
        await Initialize(first.Path);
        var source = Override(removedAgentFiles: new HashSet<string>(["preamble.md"], StringComparer.Ordinal));
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(first.Path);
        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.Remove);
        await service.ApplyAsync(first.Path, preview.PlanToken);
        Assert.False(File.Exists(Full(first.Path, ".agents/preamble.md")));

        using var second = new TempDir();
        await Initialize(second.Path);
        await File.AppendAllTextAsync(Full(second.Path, ".agents/preamble.md"), "owner edit\n");
        preview = await service.PreviewAsync(second.Path);
        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.Conflict);
        await service.ApplyAsync(second.Path, preview.PlanToken);
        Assert.True(File.Exists(Full(second.Path, ".agents/preamble.md")));
    }

    [Fact]
    public async Task Automation_entries_update_independently_and_owner_entries_survive()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var automationPath = Full(tmp.Path, ".agents/automations.json");
        var templateBytes = CorePack.Source().ReadAgentAsset(PackComposer.AutomationsFile);
        var template = JsonSerializer.Deserialize<AutomationConfig>(templateBytes, AutomationStore.JsonOptions)!;
        var target = template.Automations.First();
        target.Name += " updated";
        var source = Override(agentFiles: new()
        {
            [PackComposer.AutomationsFile] = JsonSerializer.SerializeToUtf8Bytes(template, AutomationStore.JsonOptions),
        });

        var workspace = JsonSerializer.Deserialize<AutomationConfig>(
            await File.ReadAllBytesAsync(automationPath), AutomationStore.JsonOptions)!;
        workspace.Automations.Add(new GigaClaw.Core.Automation.Automation
        {
            Id = "owner-custom",
            Name = "Owner custom",
            Trigger = new IntervalTriggerSpec { Cron = "0 4 * * *" },
        });
        await File.WriteAllBytesAsync(
            automationPath,
            JsonSerializer.SerializeToUtf8Bytes(workspace, AutomationStore.JsonOptions));

        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        var synced = JsonSerializer.Deserialize<AutomationConfig>(
            await File.ReadAllBytesAsync(automationPath), AutomationStore.JsonOptions)!;
        Assert.EndsWith(" updated", synced.Automations.Single(item => item.Id == target.Id).Name);
        Assert.Contains(synced.Automations, item => item.Id == "owner-custom");
    }

    [Fact]
    public async Task Owner_modified_automation_entry_is_preserved_and_reported()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var automationPath = Full(tmp.Path, ".agents/automations.json");
        var workspace = JsonSerializer.Deserialize<AutomationConfig>(
            await File.ReadAllBytesAsync(automationPath), AutomationStore.JsonOptions)!;
        var target = workspace.Automations.First();
        var id = target.Id;
        target.Name = "owner changed";
        await File.WriteAllBytesAsync(
            automationPath,
            JsonSerializer.SerializeToUtf8Bytes(workspace, AutomationStore.JsonOptions));

        var nextTemplate = JsonSerializer.Deserialize<AutomationConfig>(
            CorePack.Source().ReadAgentAsset(PackComposer.AutomationsFile), AutomationStore.JsonOptions)!;
        nextTemplate.Automations.Single(item => item.Id == id).Name = "core changed";
        var source = Override(agentFiles: new()
        {
            [PackComposer.AutomationsFile] = JsonSerializer.SerializeToUtf8Bytes(nextTemplate, AutomationStore.JsonOptions),
        });
        var service = new AgentTemplateSyncService(source);
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/automations.json#" + id && change.Kind == AgentTemplateSyncChangeKind.Conflict);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        var synced = JsonSerializer.Deserialize<AutomationConfig>(
            await File.ReadAllBytesAsync(automationPath), AutomationStore.JsonOptions)!;
        Assert.Equal("owner changed", synced.Automations.Single(item => item.Id == id).Name);
    }

    [Fact]
    public async Task Apply_rejects_a_stale_preview()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var current = await File.ReadAllTextAsync(Full(tmp.Path, ".agents/preamble.md"));
        var service = new AgentTemplateSyncService(Override(agentFiles: new()
        {
            ["preamble.md"] = Encoding.UTF8.GetBytes(current + "new\n"),
        }));
        var preview = await service.PreviewAsync(tmp.Path);
        await File.AppendAllTextAsync(Full(tmp.Path, ".agents/preamble.md"), "owner raced\n");

        await Assert.ThrowsAsync<AgentTemplateSyncPlanChangedException>(
            () => service.ApplyAsync(tmp.Path, preview.PlanToken));
    }

    [Fact]
    public async Task Memory_writes_do_not_stale_a_safe_preview()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var current = await File.ReadAllTextAsync(Full(tmp.Path, ".agents/preamble.md"));
        var service = new AgentTemplateSyncService(Override(agentFiles: new()
        {
            ["preamble.md"] = Encoding.UTF8.GetBytes(current + "new\n"),
        }));
        var preview = await service.PreviewAsync(tmp.Path);

        await File.AppendAllTextAsync(Full(tmp.Path, ".agents/blog-writer/memory/MEMORY.md"), "runtime write\n");
        await service.ApplyAsync(tmp.Path, preview.PlanToken);

        Assert.EndsWith("new\n", await File.ReadAllTextAsync(Full(tmp.Path, ".agents/preamble.md")));
    }

    [Fact]
    public async Task Invalid_length_token_is_reported_as_a_stale_preview()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var service = new AgentTemplateSyncService();

        await Assert.ThrowsAsync<AgentTemplateSyncPlanChangedException>(
            () => service.ApplyAsync(tmp.Path, "short"));
    }

    [Fact]
    public async Task Conflict_baseline_survives_an_unrelated_update_and_protects_a_later_delete()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var preamble = Full(tmp.Path, ".agents/preamble.md");
        await File.WriteAllTextAsync(preamble, "owner copy\n");
        var service = new AgentTemplateSyncService(Override(agentFiles: new()
        {
            ["preamble.md"] = Encoding.UTF8.GetBytes("new core copy\n"),
            ["blog-writer/references/new-unrelated.md"] = Encoding.UTF8.GetBytes("new\n"),
        }));
        var preview = await service.PreviewAsync(tmp.Path);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);
        Assert.Equal("owner copy\n", await File.ReadAllTextAsync(preamble));

        File.Delete(preamble);
        preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.DeletedByOwner);
    }

    [Fact]
    public async Task Contract_defaults_update_only_after_a_safe_baseline_exists()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var baseline = new AgentTemplateSyncService();
        var preview = await baseline.PreviewAsync(tmp.Path);
        await baseline.ApplyAsync(tmp.Path, preview.PlanToken);

        var contracts = (JsonObject)JsonNode.Parse(CorePack.Source().ReadAgentAsset(PackComposer.ContractsFile))!;
        contracts["defaults"]!["maxTurns"] = 987;
        var service = new AgentTemplateSyncService(Override(agentFiles: new()
        {
            [PackComposer.ContractsFile] = Encoding.UTF8.GetBytes(contracts.ToJsonString()),
        }));
        preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/contracts.json#defaults" && change.Kind == AgentTemplateSyncChangeKind.Update);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);
        var synced = (JsonObject)JsonNode.Parse(await File.ReadAllBytesAsync(Full(tmp.Path, ".agents/contracts.json")))!;
        Assert.Equal(987, synced["defaults"]!["maxTurns"]!.GetValue<int>());
    }

    [Fact]
    public async Task Duplicate_merge_entries_are_preserved_as_conflicts()
    {
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var automationsPath = Full(tmp.Path, ".agents/automations.json");
        var automations = (JsonObject)JsonNode.Parse(await File.ReadAllBytesAsync(automationsPath))!;
        var array = automations["automations"]!.AsArray();
        array.Add(array[0]!.DeepClone());
        await File.WriteAllTextAsync(automationsPath, automations.ToJsonString());

        var teamsPath = Full(tmp.Path, ".agents/teams.json");
        var teams = (JsonObject)JsonNode.Parse(await File.ReadAllBytesAsync(teamsPath))!;
        var teamArray = teams["teams"]!.AsArray();
        teamArray.Add(teamArray[0]!.DeepClone());
        await File.WriteAllTextAsync(teamsPath, teams.ToJsonString());

        var preview = await new AgentTemplateSyncService().PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/automations.json" && change.Kind == AgentTemplateSyncChangeKind.Conflict);
        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/teams.json" && change.Kind == AgentTemplateSyncChangeKind.Conflict);
    }

    [Fact]
    public async Task Apply_refuses_a_broken_destination_symlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        await Initialize(tmp.Path);
        var preamble = Full(tmp.Path, ".agents/preamble.md");
        File.Delete(preamble);
        var outside = Path.Combine(tmp.Path, "outside", "escaped.md");
        File.CreateSymbolicLink(preamble, outside);
        var service = new AgentTemplateSyncService(Override(agentFiles: new()
        {
            ["preamble.md"] = Encoding.UTF8.GetBytes("new core copy\n"),
        }));
        var preview = await service.PreviewAsync(tmp.Path);

        Assert.Contains(preview.Changes, change =>
            change.RelativePath == ".agents/preamble.md" && change.Kind == AgentTemplateSyncChangeKind.Conflict);
        await service.ApplyAsync(tmp.Path, preview.PlanToken);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Missing_or_invalid_lock_fails_closed()
    {
        using var tmp = new TempDir();
        var service = new AgentTemplateSyncService();
        var missing = await service.PreviewAsync(tmp.Path);
        Assert.False(missing.CanApply);
        Assert.Contains(missing.Changes, change => change.Kind == AgentTemplateSyncChangeKind.ManualReviewRequired);

        Directory.CreateDirectory(Full(tmp.Path, ".agents"));
        await File.WriteAllTextAsync(Full(tmp.Path, ".agents/packs.lock.json"), "not json");
        var invalid = await service.PreviewAsync(tmp.Path);
        Assert.False(invalid.CanApply);
        Assert.Contains(invalid.Changes, change => change.Kind == AgentTemplateSyncChangeKind.ManualReviewRequired);
    }

    [Fact]
    public async Task Explicit_destructive_initialize_still_overwrites_owner_edits()
    {
        using var tmp = new TempDir();
        var template = new GigaClaw.Core.Services.AgentsTemplateService();
        await template.InitializeAsync(tmp.Path, overwriteConflicts: true);
        var path = Full(tmp.Path, ".agents/preamble.md");
        var expected = Encoding.UTF8.GetString(template.ReadAgentAsset("preamble.md"));
        await File.WriteAllTextAsync(path, "owner copy\n");

        await template.InitializeAsync(tmp.Path, overwriteConflicts: true);

        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    private static Task Initialize(string workspace) =>
        new PackInstaller().InstallAsync(
            workspace,
            [CorePack.Source()],
            new PackInstallOptions(OverwriteConflicts: true));

    private static IPackSource Override(
        Dictionary<string, byte[]>? agentFiles = null,
        IReadOnlySet<string>? removedAgentFiles = null) =>
        new OverridePackSource(CorePack.Source(), agentFiles, removedAgentFiles);

    private static string Full(string workspace, string relative) =>
        Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class OverridePackSource : IPackSource
    {
        private readonly IPackSource _inner;
        private readonly IReadOnlyDictionary<string, byte[]> _agentFiles;
        private readonly IReadOnlySet<string> _removed;

        public OverridePackSource(
            IPackSource inner,
            IReadOnlyDictionary<string, byte[]>? agentFiles,
            IReadOnlySet<string>? removed)
        {
            _inner = inner;
            _agentFiles = agentFiles ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _removed = removed ?? new HashSet<string>(StringComparer.Ordinal);
        }

        public string Id => _inner.Id;
        public string ReadManifest() => _inner.ReadManifest();
        public IReadOnlyList<string> AgentRelativePaths() => _inner.AgentRelativePaths()
            .Where(path => !_removed.Contains(path))
            .Concat(_agentFiles.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        public IReadOnlyList<string> RootRelativePaths() => _inner.RootRelativePaths();
        public byte[] ReadAgentAsset(string relativePath) =>
            _agentFiles.TryGetValue(relativePath, out var content) ? content : _inner.ReadAgentAsset(relativePath);
        public byte[] ReadRootAsset(string relativePath) => _inner.ReadRootAsset(relativePath);
    }
}

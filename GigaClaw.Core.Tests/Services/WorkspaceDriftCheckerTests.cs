using System.Text.Json;
using GigaClaw.Catalog;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// The per-project successor to <c>tools/check-automation-drift.sh</c> (retired alongside
/// <see cref="WorkspaceDriftChecker"/>). Each test drives a real
/// <see cref="AgentsTemplateService.InitializeAsync"/> into a temp workspace and then perturbs it,
/// mirroring how the script was actually used against real venture workspaces.
/// </summary>
public sealed class WorkspaceDriftCheckerTests
{
    [Fact]
    public async Task Freshly_initialized_workspace_reports_zero_drift()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.False(report.HasDrift, Describe(report));
        Assert.Empty(report.Drift);
        Assert.Empty(report.Allowlisted);
    }

    [Fact]
    public async Task Missing_agent_file_is_reported_as_missing_and_exits_non_zero()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var skillFile = Path.Combine(tmp.Path, ".agents", "programmer", "SKILL.md");
        Assert.True(File.Exists(skillFile));
        File.Delete(skillFile);

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.True(report.HasDrift);
        var missing = Assert.Single(report.Drift, d => d.RelativePath == ".agents/programmer/SKILL.md");
        Assert.Equal(DriftKind.Missing, missing.Kind);
    }

    [Fact]
    public async Task Modified_shared_template_file_is_reported_as_modified()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var preamble = Path.Combine(tmp.Path, ".agents", "preamble.md");
        await File.AppendAllTextAsync(preamble, "\nowner-added line that the template does not have.\n");

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.True(report.HasDrift);
        var modified = Assert.Single(report.Drift, d => d.RelativePath == ".agents/preamble.md");
        Assert.Equal(DriftKind.Modified, modified.Kind);
        Assert.Contains("shared template", modified.Detail);
    }

    [Fact]
    public async Task Missing_workspace_root_file_is_reported_with_its_section()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        File.Delete(Path.Combine(tmp.Path, "CLAUDE.md"));

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        var missing = Assert.Single(report.Drift, d => d.RelativePath == "CLAUDE.md");
        Assert.Equal(DriftKind.Missing, missing.Kind);
        Assert.Contains("workspace root", missing.Detail);
    }

    /// <summary>
    /// automations.json is the one file the retired script actually compared, at automation-id
    /// granularity. A changed trigger field on an existing id must surface as MODIFIED and must name
    /// which fields changed — richer than the script's bare "CHANGED: id".
    /// </summary>
    [Fact]
    public async Task Changed_automation_field_is_reported_as_modified_naming_the_differing_keys()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var automationsPath = Path.Combine(tmp.Path, ".agents", "automations.json");
        MutateAutomations(automationsPath, root =>
        {
            var automation = root["automations"]!.AsArray()
                .Single(node => node!["id"]!.GetValue<string>() == "code-janitor-nightly");
            automation!["enabled"] = false;
        });

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        var modified = Assert.Single(report.Drift, d =>
            d.RelativePath == ".agents/automations.json" && d.Detail.Contains("code-janitor-nightly"));
        Assert.Equal(DriftKind.Modified, modified.Kind);
        Assert.Contains("enabled", modified.Detail);
    }

    /// <summary>
    /// MISSING at automation-id granularity: an automation the template ships is deleted from the
    /// workspace copy without being allowlisted.
    /// </summary>
    [Fact]
    public async Task Deleted_automation_id_is_reported_as_missing()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var automationsPath = Path.Combine(tmp.Path, ".agents", "automations.json");
        MutateAutomations(automationsPath, root =>
        {
            var array = root["automations"]!.AsArray();
            array.Remove(array.Single(node => node!["id"]!.GetValue<string>() == "code-janitor-nightly"));
        });

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        var missing = Assert.Single(report.Drift, d =>
            d.RelativePath == ".agents/automations.json" && d.Kind == DriftKind.Missing
            && d.Detail.Contains("code-janitor-nightly"));
        Assert.NotNull(missing);
    }

    /// <summary>
    /// EXTRA — the one drift category the script scoped to automations.json alone (an id present in
    /// the project but absent from the template). This checker keeps that same scope: it does not
    /// generalize "extra" to arbitrary files elsewhere in <c>.agents/</c>.
    /// </summary>
    [Fact]
    public async Task Extra_automation_id_is_reported_as_extra()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var automationsPath = Path.Combine(tmp.Path, ".agents", "automations.json");
        MutateAutomations(automationsPath, root =>
        {
            var array = root["automations"]!.AsArray();
            array.Add(System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "id": "owner-custom-automation",
                  "enabled": true,
                  "trigger": { "type": "interval", "seconds": 60 },
                  "conditions": [],
                  "actions": []
                }
                """));
        });

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        var extra = Assert.Single(report.Drift, d =>
            d.RelativePath == ".agents/automations.json" && d.Detail.Contains("owner-custom-automation"));
        Assert.Equal(DriftKind.Extra, extra.Kind);
    }

    /// <summary>
    /// automation-overrides.json exempts an id from being counted as drift, exactly like the
    /// retired script's allowlist — reported separately, and not double-counted in <see cref="WorkspaceDriftReport.Drift"/>.
    /// </summary>
    [Fact]
    public async Task Allowlisted_automation_id_is_exempted_and_reported_separately()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var automationsPath = Path.Combine(tmp.Path, ".agents", "automations.json");
        MutateAutomations(automationsPath, root =>
        {
            var array = root["automations"]!.AsArray();
            array.Remove(array.Single(node => node!["id"]!.GetValue<string>() == "code-janitor-nightly"));
        });
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, ".agents", "automation-overrides.json"),
            """["code-janitor-nightly"]""");

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.False(report.HasDrift, Describe(report));
        Assert.Empty(report.Drift);
        Assert.Equal(["code-janitor-nightly"], report.Allowlisted);
    }

    /// <summary>An extra file elsewhere in <c>.agents/</c> — e.g. a per-topic memory note the
    /// template never shipped — must never be reported. Only automations.json gets EXTRA treatment
    /// (see <see cref="Extra_automation_id_is_reported_as_extra"/>); everything else is compared as
    /// missing/modified only, exactly as established from the retired script's scope.</summary>
    [Fact]
    public async Task Extra_file_elsewhere_in_agents_directory_is_not_reported_as_drift()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var topicNote = Path.Combine(tmp.Path, ".agents", "programmer", "memory", "some-runtime-topic.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicNote)!);
        await File.WriteAllTextAsync(topicNote, "runtime-written topic note, not part of the template.");

        var report = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.False(report.HasDrift, Describe(report));
    }

    [Fact]
    public async Task Template_memory_files_are_excluded_from_drift()
    {
        using var tmp = new TempDir();
        await new AgentsTemplateService().InitializeAsync(tmp.Path, overwriteConflicts: true);
        var memory = Path.Combine(tmp.Path, ".agents", "blog-writer", "memory", "MEMORY.md");
        await File.WriteAllTextAsync(memory, "owner-maintained memory index");

        var modified = WorkspaceDriftChecker.Check(tmp.Path);

        Assert.DoesNotContain(modified.Drift, drift => drift.RelativePath.Contains("/memory/", StringComparison.Ordinal));

        File.Delete(memory);
        var deleted = WorkspaceDriftChecker.Check(tmp.Path);
        Assert.DoesNotContain(deleted.Drift, drift => drift.RelativePath.Contains("/memory/", StringComparison.Ordinal));
    }

    [Fact]
    public void Template_version_matches_the_committed_core_pack_manifest()
    {
        var version = WorkspaceDriftChecker.ReadTemplateVersion();
        Assert.Equal("1.0.0", version);
    }

    private static void MutateAutomations(string path, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        mutate(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Describe(WorkspaceDriftReport report) =>
        string.Join("\n", report.Drift.Select(d => d.ToString()));
}

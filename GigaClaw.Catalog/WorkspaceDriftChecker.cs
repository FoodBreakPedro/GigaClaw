using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Services;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Catalog;

/// <summary>
/// The per-project successor to <c>tools/check-automation-drift.sh</c> (retired alongside this
/// type). The script compared exactly one file — a project's <c>.agents/automations.json</c>
/// against <c>ProjectTemplate/Agents/automations.json</c> — at automation-id granularity: MISSING
/// (template id absent from the project), EXTRA (project id absent from the template), CHANGED (id
/// present on both sides with different content), with an <c>automation-overrides.json</c> allowlist
/// exempting specific ids from being counted as drift.
///
/// <para>
/// This checker keeps that exact contract for <c>automations.json</c> — including the allowlist —
/// and extends the same "missing / modified" comparison to every other file
/// <see cref="AgentsTemplateService"/> writes on Initialize: every <c>.agents/**</c> template path
/// plus the workspace-root files (<c>CLAUDE.md</c>, <c>.gitignore</c>, <c>.dashboard/**</c>). It
/// deliberately does <b>not</b> generalize EXTRA to arbitrary files: <c>.agents/</c> legitimately
/// accumulates files the template never shipped (per-topic memory notes, <c>packs.lock.json</c>,
/// the allowlist file itself, an owner's <c>automation-overrides.json</c>), and flagging all of
/// those as drift would just be noise the script never produced either.
/// </para>
///
/// <para>
/// Four of the template's <c>.agents/</c> files are pack <em>merge artifacts</em>
/// (<see cref="PackComposer.AutomationsFile"/>, <see cref="PackComposer.ContractsFile"/>,
/// <see cref="PackComposer.ModelsFile"/>, <see cref="PackComposer.TeamsFile"/>): Initialize
/// re-serializes them through a model rather than copying them verbatim, so a byte comparison
/// against the template flags drift on a workspace that was never touched. Each gets a semantic,
/// per-entry comparison instead — matching the technique
/// <c>CoreInitManifestTests.Merge_artifacts_keep_the_template_content_they_had_before_the_extraction</c>
/// already relies on. Every other file is copied verbatim by <c>PackInstaller.PlanOpaqueFiles</c>
/// (the golden manifest in <c>CoreInitManifestTests</c> proves this), so a raw byte comparison is
/// exact for those.
/// </para>
/// </summary>
public enum DriftKind { Missing, Modified, Extra }

/// <summary>One drifted path (or one drifted entry within a merge artifact), readable on its own.</summary>
public sealed record FileDrift(string RelativePath, DriftKind Kind, string Detail)
{
    public override string ToString() => $"{Kind.ToString().ToUpperInvariant()} {RelativePath}: {Detail}";
}

/// <summary>One workspace's drift report against the current template.</summary>
public sealed record WorkspaceDriftReport(
    string Workspace,
    string TemplateVersion,
    IReadOnlyList<FileDrift> Drift,
    IReadOnlyList<string> Allowlisted)
{
    public bool HasDrift => Drift.Count > 0;
}

public static class WorkspaceDriftChecker
{
    public const string OverridesFileName = "automation-overrides.json";

    /// <summary>
    /// Compares <c>&lt;workspace&gt;/.agents/**</c> and the workspace-root template files against
    /// the current embedded template (the same bytes <see cref="AgentsTemplateService.InitializeAsync"/>
    /// would write today). Never touches the workspace.
    /// </summary>
    public static WorkspaceDriftReport Check(string workspacePath, AgentsTemplateService? templateService = null)
    {
        var template = templateService ?? new AgentsTemplateService();
        var agentsDir = Path.Combine(workspacePath, ".agents");
        var drift = new List<FileDrift>();
        var allowlisted = new List<string>();

        var allowlist = ReadAllowlist(Path.Combine(agentsDir, OverridesFileName));

        foreach (var relative in template.RelativePaths().OrderBy(path => path, StringComparer.Ordinal))
        {
            var displayPath = ".agents/" + relative;
            var workspaceFile = Path.Combine(agentsDir, ToNativePath(relative));

            if (relative == PackComposer.AutomationsFile)
            {
                CompareAutomations(displayPath, workspaceFile, template.ReadAgentAsset(relative), allowlist, drift, allowlisted);
            }
            else if (relative == PackComposer.ContractsFile || relative == PackComposer.ModelsFile)
            {
                CompareJsonByTopLevelKey(displayPath, workspaceFile, template.ReadAgentAsset(relative), drift);
            }
            else if (relative == PackComposer.TeamsFile)
            {
                CompareTeams(displayPath, workspaceFile, template.ReadAgentAsset(relative), drift);
            }
            else
            {
                CompareOpaqueFile(displayPath, SectionOf(relative), workspaceFile, template.ReadAgentAsset(relative), drift);
            }
        }

        foreach (var relative in template.RootRelativePaths().OrderBy(path => path, StringComparer.Ordinal))
        {
            var workspaceFile = Path.Combine(workspacePath, ToNativePath(relative));
            CompareOpaqueFile(relative, RootSectionOf(relative), workspaceFile, template.ReadRootAsset(relative), drift);
        }

        return new WorkspaceDriftReport(
            workspacePath,
            ReadTemplateVersion(),
            drift.OrderBy(d => d.RelativePath, StringComparer.Ordinal).ThenBy(d => d.Kind).ToArray(),
            allowlisted.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The <c>core</c> pack's declared version, read from the same embedded manifest
    /// <see cref="AgentsTemplateService"/> installs from — "which template version" a report is
    /// comparing against.</summary>
    public static string ReadTemplateVersion(System.Reflection.Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(AgentsTemplateService).Assembly;
        using var stream = asm.GetManifestResourceStream(CorePack.ManifestResourceName);
        if (stream is null) return PackCatalogSource.UnmanifestedVersion;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString()!
            : PackCatalogSource.UnmanifestedVersion;
    }

    // --------------------------------------------------------------------- automations.json

    private static void CompareAutomations(
        string displayPath,
        string workspaceFile,
        byte[] templateBytes,
        IReadOnlySet<string> allowlist,
        List<FileDrift> drift,
        List<string> allowlisted)
    {
        if (!File.Exists(workspaceFile))
        {
            drift.Add(new FileDrift(displayPath, DriftKind.Missing, "present in template, absent from workspace"));
            return;
        }

        var templateAutomations = DeserializeAutomations(templateBytes);
        var workspaceAutomations = DeserializeAutomations(File.ReadAllBytes(workspaceFile));

        foreach (var id in templateAutomations.Keys.Except(workspaceAutomations.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            if (allowlist.Contains(id)) { allowlisted.Add(id); continue; }
            drift.Add(new FileDrift(displayPath, DriftKind.Missing,
                $"automation '{id}' present in template, absent from workspace"));
        }

        foreach (var id in workspaceAutomations.Keys.Except(templateAutomations.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            if (allowlist.Contains(id)) { allowlisted.Add(id); continue; }
            drift.Add(new FileDrift(displayPath, DriftKind.Extra,
                $"automation '{id}' present in workspace, not in template"));
        }

        foreach (var id in templateAutomations.Keys.Intersect(workspaceAutomations.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var templateJson = JsonSerializer.Serialize(templateAutomations[id], AutomationStore.JsonOptions);
            var workspaceJson = JsonSerializer.Serialize(workspaceAutomations[id], AutomationStore.JsonOptions);
            if (templateJson == workspaceJson) continue;
            if (allowlist.Contains(id)) { allowlisted.Add(id); continue; }

            var keys = DifferingKeys(
                JsonDocument.Parse(templateJson).RootElement,
                JsonDocument.Parse(workspaceJson).RootElement);
            drift.Add(new FileDrift(displayPath, DriftKind.Modified,
                $"automation '{id}' differs from template in: {string.Join(", ", keys)}"));
        }
    }

    /// <summary>
    /// Deserializes through <see cref="AutomationConfig"/> — the same model
    /// <c>PackInstaller.LoadAutomations</c> writes back through — so a freshly initialized workspace
    /// (whose <c>automations.json</c> was re-serialized at install time) normalizes identically to
    /// the raw template bytes and produces no spurious drift.
    /// </summary>
    private static Dictionary<string, AutomationRule> DeserializeAutomations(byte[] bytes)
    {
        var config = JsonSerializer.Deserialize<AutomationConfig>(bytes, AutomationStore.JsonOptions)
            ?? new AutomationConfig();
        return config.Automations.ToDictionary(a => a.Id, StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ReadAllowlist(string path)
    {
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new HashSet<string>(StringComparer.Ordinal);
            return document.RootElement.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------- contracts.json / models.json

    private static void CompareJsonByTopLevelKey(string displayPath, string workspaceFile, byte[] templateBytes, List<FileDrift> drift)
    {
        if (!File.Exists(workspaceFile))
        {
            drift.Add(new FileDrift(displayPath, DriftKind.Missing, "present in template, absent from workspace"));
            return;
        }

        if (JsonNode.Parse(templateBytes) is not JsonObject templateObject) return;
        if (JsonNode.Parse(File.ReadAllBytes(workspaceFile)) is not JsonObject workspaceObject)
        {
            drift.Add(new FileDrift(displayPath, DriftKind.Modified, "workspace content is not a JSON object like the template"));
            return;
        }

        foreach (var key in templateObject.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!workspaceObject.ContainsKey(key))
            {
                drift.Add(new FileDrift(displayPath, DriftKind.Missing, $"key '{key}' present in template, absent from workspace"));
                continue;
            }
            if (!JsonNode.DeepEquals(templateObject[key], workspaceObject[key]))
                drift.Add(new FileDrift(displayPath, DriftKind.Modified, $"key '{key}' differs from template"));
        }
    }

    // --------------------------------------------------------------------------- teams.json

    private static void CompareTeams(string displayPath, string workspaceFile, byte[] templateBytes, List<FileDrift> drift)
    {
        if (!File.Exists(workspaceFile))
        {
            drift.Add(new FileDrift(displayPath, DriftKind.Missing, "present in template, absent from workspace"));
            return;
        }

        var templateTeams = TeamSeed.Compose(TeamSeed.Parse(Encoding.UTF8.GetString(templateBytes), "template teams.json"))
            .ToDictionary(t => t.Slug, StringComparer.Ordinal);
        var workspaceTeams = TeamSeed.Compose(TeamSeed.Parse(File.ReadAllText(workspaceFile), "workspace teams.json"))
            .ToDictionary(t => t.Slug, StringComparer.Ordinal);

        foreach (var slug in templateTeams.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (!workspaceTeams.TryGetValue(slug, out var workspaceTeam))
            {
                drift.Add(new FileDrift(displayPath, DriftKind.Missing, $"team '{slug}' present in template, absent from workspace"));
                continue;
            }
            if (DescribeTeam(templateTeams[slug]) != DescribeTeam(workspaceTeam))
                drift.Add(new FileDrift(displayPath, DriftKind.Modified, $"team '{slug}' differs from template"));
        }
    }

    private static string DescribeTeam(TeamDefinition team) => string.Join('|',
        team.Slug, team.Name, team.Description, team.Icon,
        string.Join(',', team.Roles.Select(role => role.RoleId + ":" + role.AgentSlug)));

    // --------------------------------------------------------------------------- opaque files

    /// <summary>Every non-merge-artifact template file is copied byte-for-byte by
    /// <c>PackInstaller.PlanOpaqueFiles</c>, so an exact comparison is correct here.</summary>
    private static void CompareOpaqueFile(string displayPath, string section, string workspaceFile, byte[] templateBytes, List<FileDrift> drift)
    {
        if (!File.Exists(workspaceFile))
        {
            drift.Add(new FileDrift(displayPath, DriftKind.Missing, $"{section}: present in template, absent from workspace"));
            return;
        }
        if (!File.ReadAllBytes(workspaceFile).AsSpan().SequenceEqual(templateBytes))
            drift.Add(new FileDrift(displayPath, DriftKind.Modified, $"{section}: workspace content differs from template"));
    }

    private static string SectionOf(string agentsRelativePath)
    {
        var parts = agentsRelativePath.Split('/');
        if (parts[0] == "scripts") return "shared scripts";
        return parts.Length >= 2 ? $"agent '{parts[0]}'" : "shared template";
    }

    private static string RootSectionOf(string rootRelativePath)
    {
        if (!rootRelativePath.StartsWith(".dashboard/", StringComparison.Ordinal)) return "workspace root";
        var parts = rootRelativePath.Split('/');
        return parts.Length >= 2 ? $"dashboard tile '{parts[1]}'" : "workspace root";
    }

    // ------------------------------------------------------------------------------- helpers

    private static string ToNativePath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Order-independent object equality, order-sensitive arrays, exact scalars — the same
    /// notion of "same content" <c>jq -S</c> gave the retired script.</summary>
    private static bool JsonDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                if (aProps.Count != bProps.Count) return false;
                return aProps.All(pair => bProps.TryGetValue(pair.Key, out var bValue) && JsonDeepEquals(pair.Value, bValue));
            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToArray();
                var bItems = b.EnumerateArray().ToArray();
                return aItems.Length == bItems.Length && aItems.Zip(bItems, JsonDeepEquals).All(equal => equal);
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    /// <summary>Which top-level keys differ between two JSON objects — the "which section" detail
    /// the retired script never reported (it only ever said an id was CHANGED).</summary>
    private static IReadOnlyList<string> DifferingKeys(JsonElement a, JsonElement b)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var prop in a.EnumerateObject()) keys.Add(prop.Name);
        foreach (var prop in b.EnumerateObject()) keys.Add(prop.Name);

        var differing = new List<string>();
        foreach (var key in keys)
        {
            var hasA = a.TryGetProperty(key, out var aValue);
            var hasB = b.TryGetProperty(key, out var bValue);
            if (hasA != hasB || (hasA && hasB && !JsonDeepEquals(aValue, bValue))) differing.Add(key);
        }
        return differing;
    }
}

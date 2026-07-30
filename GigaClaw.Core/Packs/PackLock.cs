using System.Text.Json;
using System.Text.Json.Nodes;

namespace GigaClaw.Core.Packs;

/// <summary>
/// One installed pack as recorded in <c>packs.lock.json</c>.
///
/// <see cref="FileHashes"/> covers the pack's <em>opaque</em> files only — agent directories,
/// scripts and root files. It is the mechanism that makes uninstall safe: matching hash means
/// pack-owned and untouched, so the file can be deleted; a differing hash means the owner edited
/// it, so it is left alone and reported as orphaned (§4).
///
/// The four merge artifacts are recorded as key lists (<see cref="Automations"/>,
/// <see cref="ContractKeys"/>, <see cref="ModelKeys"/>, <see cref="Teams"/>) because no single pack
/// owns those files on disk. <see cref="MergeEntryHashes"/> carries the per-entry hash those key
/// lists need to answer "is this entry still byte-identical to what was installed?" — the question
/// §4's uninstall steps 3 and 4 are written in terms of.
/// </summary>
public sealed record PackLockEntry(
    string Id,
    string Version,
    PackKind Kind,
    bool Removable,
    PackRuntimeRequirement RequiresRuntime,
    IReadOnlyList<PackDependency> DependsOn,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Automations,
    IReadOnlyList<string> Teams,
    IReadOnlyList<string> ContractKeys,
    IReadOnlyList<string> ModelKeys,
    IReadOnlyList<PackAutomationPatch> AutomationPatches,
    IReadOnlyDictionary<string, string> FileHashes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergeEntryHashes);

/// <summary>
/// <c>&lt;workspace&gt;/.agents/packs.lock.json</c> — the authoritative record of what is installed.
///
/// D6 puts it in the workspace, not the registry DB: the workspace is the artifact that gets
/// cloned, copied and moved between machines, <c>.agents/</c> is already the system of record for
/// agent state, and <c>ProjectTemplate/.gitignore</c> does not ignore <c>.agents/</c>, so the
/// lockfile is committed and reviewable alongside the workspace. A registry column desyncs the
/// first time a workspace moves.
/// </summary>
public sealed record PackLockFile(
    int SchemaVersion,
    string InstallId,
    DateTimeOffset InstalledAtUtc,
    int PackRuntimeVersion,
    IReadOnlyList<PackLockEntry> Packs)
{
    public const string FileName = "packs.lock.json";

    public PackLockEntry? Find(string packId) =>
        Packs.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.Ordinal));

    public static PackLockFile Empty(int runtimeVersion) =>
        new(PackRuntime.LockSchemaVersion, string.Empty, DateTimeOffset.UnixEpoch, runtimeVersion,
            Array.Empty<PackLockEntry>());
}

/// <summary>
/// Reads and writes <c>packs.lock.json</c>. Written by hand rather than through the serializer so
/// key order is deterministic (dictionaries sorted ordinal): the lockfile is committed alongside
/// the workspace and a reshuffled diff on every install would make it unreviewable.
/// </summary>
public static class PackLockSerializer
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    public static string ToJson(PackLockFile lockFile)
    {
        var packs = new JsonArray();
        foreach (var pack in lockFile.Packs.OrderBy(p => p.Id, StringComparer.Ordinal))
            packs.Add(EntryToNode(pack));

        var root = new JsonObject
        {
            ["schemaVersion"] = lockFile.SchemaVersion,
            ["installId"] = lockFile.InstallId,
            ["installedAtUtc"] = lockFile.InstalledAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["packRuntimeVersion"] = lockFile.PackRuntimeVersion,
            ["packs"] = packs,
        };
        return root.ToJsonString(Write);
    }

    public static PackLockFile Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new PackValidationException($"{PackLockFile.FileName} is not valid JSON — {ex.Message}");
        }

        if (root is not JsonObject obj)
            throw new PackValidationException($"{PackLockFile.FileName} must be a JSON object.");

        var schemaVersion = obj["schemaVersion"]?.GetValue<int>()
            ?? throw new PackValidationException($"{PackLockFile.FileName}: schemaVersion is required.");
        if (schemaVersion != PackRuntime.LockSchemaVersion)
        {
            // Fail closed: a lockfile from a newer core describes state this build cannot reason
            // about, and guessing would risk deleting files it does not understand.
            throw new PackValidationException(
                $"{PackLockFile.FileName}: schemaVersion {schemaVersion} is not supported " +
                $"(this build reads {PackRuntime.LockSchemaVersion} only).");
        }

        var entries = new List<PackLockEntry>();
        if (obj["packs"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject entry) entries.Add(EntryFromNode(entry));
            }
        }

        return new PackLockFile(
            schemaVersion,
            obj["installId"]?.GetValue<string>() ?? string.Empty,
            obj["installedAtUtc"]?.GetValue<string>() is { } stamp
                && DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : DateTimeOffset.UnixEpoch,
            obj["packRuntimeVersion"]?.GetValue<int>() ?? PackRuntime.Version,
            entries);
    }

    private static JsonObject EntryToNode(PackLockEntry entry)
    {
        var node = new JsonObject
        {
            ["id"] = entry.Id,
            ["version"] = entry.Version,
            ["kind"] = entry.Kind == PackKind.Core ? "core" : "specialist",
            ["removable"] = entry.Removable,
            ["requiresRuntime"] = new JsonObject
            {
                ["min"] = entry.RequiresRuntime.Min,
                ["max"] = entry.RequiresRuntime.Max,
            },
            ["dependsOn"] = new JsonArray(entry.DependsOn
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => (JsonNode)new JsonObject { ["id"] = d.Id, ["minVersion"] = d.MinVersion })
                .ToArray()),
            ["agents"] = Strings(entry.Agents),
            ["automations"] = Strings(entry.Automations),
            ["teams"] = Strings(entry.Teams),
            ["contractKeys"] = Strings(entry.ContractKeys),
            ["modelKeys"] = Strings(entry.ModelKeys),
            ["automationPatches"] = new JsonArray(entry.AutomationPatches
                .Select(p =>
                {
                    var patch = new JsonObject { ["automation"] = p.Automation, ["op"] = p.Op };
                    if (p.Slugs.Count > 0) patch["slugs"] = Strings(p.Slugs);
                    if (p.Labels.Count > 0) patch["labels"] = Strings(p.Labels);
                    return (JsonNode)patch;
                })
                .ToArray()),
            ["fileHashes"] = Map(entry.FileHashes),
        };

        var merge = new JsonObject();
        foreach (var (file, hashes) in entry.MergeEntryHashes.OrderBy(e => e.Key, StringComparer.Ordinal))
            merge[file] = Map(hashes);
        node["mergeEntryHashes"] = merge;
        return node;
    }

    private static PackLockEntry EntryFromNode(JsonObject entry)
    {
        var runtime = entry["requiresRuntime"] as JsonObject;
        return new PackLockEntry(
            entry["id"]?.GetValue<string>() ?? throw new PackValidationException(
                $"{PackLockFile.FileName}: every pack entry needs an id."),
            entry["version"]?.GetValue<string>() ?? "0.0.0",
            string.Equals(entry["kind"]?.GetValue<string>(), "core", StringComparison.Ordinal)
                ? PackKind.Core
                : PackKind.Specialist,
            entry["removable"]?.GetValue<bool>() ?? true,
            new PackRuntimeRequirement(
                runtime?["min"]?.GetValue<int>() ?? PackRuntime.Version,
                runtime?["max"]?.GetValue<int>() ?? PackRuntime.Version),
            (entry["dependsOn"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(d => new PackDependency(
                    d["id"]?.GetValue<string>() ?? string.Empty,
                    d["minVersion"]?.GetValue<string>() ?? "0.0.0"))
                .Where(d => d.Id.Length > 0)
                .ToList(),
            ReadStrings(entry, "agents"),
            ReadStrings(entry, "automations"),
            ReadStrings(entry, "teams"),
            ReadStrings(entry, "contractKeys"),
            ReadStrings(entry, "modelKeys"),
            (entry["automationPatches"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(p => new PackAutomationPatch(
                    p["automation"]?.GetValue<string>() ?? string.Empty,
                    p["op"]?.GetValue<string>() ?? string.Empty,
                    (p["slugs"] as JsonArray ?? new JsonArray()).Select(s => s!.GetValue<string>()).ToList(),
                    (p["labels"] as JsonArray ?? new JsonArray()).Select(s => s!.GetValue<string>()).ToList()))
                .Where(p => p.Automation.Length > 0)
                .ToList(),
            ReadMap(entry["fileHashes"] as JsonObject),
            (entry["mergeEntryHashes"] as JsonObject ?? new JsonObject())
                .Where(e => e.Value is JsonObject)
                .ToDictionary(
                    e => e.Key,
                    e => (IReadOnlyDictionary<string, string>)ReadMap(e.Value as JsonObject),
                    StringComparer.Ordinal));
    }

    private static JsonArray Strings(IReadOnlyList<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject Map(IReadOnlyDictionary<string, string> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values.OrderBy(e => e.Key, StringComparer.Ordinal)) node[key] = value;
        return node;
    }

    private static IReadOnlyList<string> ReadStrings(JsonObject entry, string property) =>
        (entry[property] as JsonArray ?? new JsonArray())
            .Where(v => v is not null)
            .Select(v => v!.GetValue<string>())
            .ToList();

    private static Dictionary<string, string> ReadMap(JsonObject? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null) return map;
        foreach (var entry in node)
        {
            if (entry.Value is not null) map[entry.Key] = entry.Value.GetValue<string>();
        }
        return map;
    }
}

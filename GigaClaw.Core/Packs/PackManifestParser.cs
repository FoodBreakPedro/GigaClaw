using System.Text.Json;
using System.Text.RegularExpressions;

namespace GigaClaw.Core.Packs;

/// <summary>
/// Parses and validates a single <c>pack.json</c> against the schema in
/// doc/pack-infrastructure.md §3. Hand-parsed rather than deserialized so every rejection carries
/// the field that broke and so an unknown/renamed field can never be silently dropped — the same
/// hazard §5 calls out for <c>AutomationStore.JsonOptions</c>'s default
/// <c>UnmappedMemberHandling.Skip</c>.
///
/// Rules that need more than one manifest — slug collisions, cross-pack references, dependency
/// resolution — live in <see cref="PackComposer"/>; everything decidable from a single manifest
/// is decided here.
/// </summary>
public static class PackManifestParser
{
    /// <summary>§3: <c>^[a-z][a-z0-9-]{1,38}$</c>. Applies to both pack ids and agent slugs.</summary>
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{1,38}$", RegexOptions.Compiled);

    /// <summary>§3: semver MAJOR.MINOR.PATCH, no pre-release and no build metadata (D7).</summary>
    private static readonly Regex VersionPattern =
        new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.Compiled);

    public static PackManifest Parse(string json, string? expectedId = null)
    {
        if (!TryParse(json, expectedId, out var manifest, out var errors))
            throw new PackValidationException(errors);
        return manifest!;
    }

    public static bool TryParse(
        string json,
        string? expectedId,
        out PackManifest? manifest,
        out IReadOnlyList<string> errors)
    {
        manifest = null;
        var problems = new List<string>();
        var label = expectedId is null ? "pack.json" : $"pack '{expectedId}'";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            errors = new[] { $"{label}: manifest is not valid JSON — {ex.Message}" };
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors = new[] { $"{label}: manifest root must be a JSON object." };
                return false;
            }

            // schemaVersion is checked first and alone: a higher value means the rest of the
            // document is written against a field list this build does not know, so continuing to
            // validate it would produce misleading errors. §3 — refused, not best-effort parsed.
            var schemaVersion = RequiredInt(root, "schemaVersion", label, problems);
            if (schemaVersion is null || schemaVersion != PackRuntime.ManifestSchemaVersion)
            {
                if (schemaVersion is not null)
                {
                    problems.Add(
                        $"{label}: schemaVersion {schemaVersion} is not supported " +
                        $"(this build reads {PackRuntime.ManifestSchemaVersion} only).");
                }
                errors = problems;
                return false;
            }

            var id = RequiredString(root, "id", label, problems);
            if (id is not null && !IdPattern.IsMatch(id))
                problems.Add($"{label}: id '{id}' must match ^[a-z][a-z0-9-]{{1,38}}$.");
            if (id is not null && expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
                problems.Add($"{label}: id '{id}' must equal the directory name '{expectedId}'.");

            var name = RequiredString(root, "name", label, problems);
            var description = RequiredString(root, "description", label, problems);

            var version = RequiredString(root, "version", label, problems);
            if (version is not null && !VersionPattern.IsMatch(version))
            {
                problems.Add(
                    $"{label}: version '{version}' must be semver MAJOR.MINOR.PATCH " +
                    "with no pre-release or build metadata.");
            }

            var kind = ParseKind(root, label, problems);
            var removable = RequiredBool(root, "removable", label, problems);
            if (kind == PackKind.Core && removable == true)
                problems.Add($"{label}: a core pack must declare removable:false — uninstall refuses it.");

            var requiresRuntime = ParseRuntimeRequirement(root, label, problems);
            var dependsOn = ParseDependencies(root, id, label, problems);
            var provides = ParseProvides(root, label, problems);
            var teamMembership = ParseStringListMap(root, "teamMembership", label, problems);
            var automationPatches = ParsePatches(root, label, problems);
            var receiptEmitters = ParseStringListMap(root, "receiptEmitters", label, problems);
            var permissions = ParsePermissions(root, label, problems);
            // §3/§6: required, and it must cover every provided agent. The id → agent mapping
            // lives in the fixture files themselves, so the full coverage check belongs to the
            // catalog gate; what a single manifest can decide is that a pack shipping agents
            // cannot ship an empty fixture list.
            var evalFixtures = RequiredStringArray(root, "evalFixtures", label, problems, allowEmpty: true);
            RejectDuplicates(evalFixtures, $"{label}: evalFixtures", problems);
            if (evalFixtures.Count == 0 && provides is not null && provides.Agents.Count > 0)
            {
                problems.Add(
                    $"{label}: evalFixtures is empty but the pack provides " +
                    $"{provides.Agents.Count} agent(s); every agent needs a fixture.");
            }

            if (problems.Count > 0)
            {
                errors = problems;
                return false;
            }

            manifest = new PackManifest(
                schemaVersion.Value,
                id!,
                name!,
                description!,
                version!,
                kind!.Value,
                removable!.Value,
                requiresRuntime!,
                dependsOn,
                provides!,
                teamMembership,
                automationPatches,
                receiptEmitters,
                permissions!,
                evalFixtures);
            errors = Array.Empty<string>();
            return true;
        }
    }

    /// <summary>Shared with <see cref="PackComposer"/> so slug shape is validated in exactly one place.</summary>
    public static bool IsValidSlug(string value) => IdPattern.IsMatch(value);

    /// <summary>Shared with the lockfile reader.</summary>
    public static bool IsValidVersion(string value) => VersionPattern.IsMatch(value);

    private static PackKind? ParseKind(JsonElement root, string label, List<string> problems)
    {
        var raw = RequiredString(root, "kind", label, problems);
        if (raw is null) return null;
        return raw switch
        {
            "core" => PackKind.Core,
            "specialist" => PackKind.Specialist,
            _ => Fail(problems, $"{label}: kind '{raw}' must be \"core\" or \"specialist\".", (PackKind?)null),
        };
    }

    private static PackRuntimeRequirement? ParseRuntimeRequirement(
        JsonElement root, string label, List<string> problems)
    {
        if (!root.TryGetProperty("requiresRuntime", out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: requiresRuntime is required and must be an object {{min, max}}.");
            return null;
        }

        var min = RequiredInt(element, "min", $"{label} requiresRuntime", problems);
        var max = RequiredInt(element, "max", $"{label} requiresRuntime", problems);
        if (min is null || max is null) return null;
        if (min < 1)
            problems.Add($"{label}: requiresRuntime.min must be >= 1 (the first pack-runtime version).");
        if (max < min)
            problems.Add($"{label}: requiresRuntime.max ({max}) must be >= min ({min}).");
        return new PackRuntimeRequirement(min.Value, max.Value);
    }

    private static IReadOnlyList<PackDependency> ParseDependencies(
        JsonElement root, string? selfId, string label, List<string> problems)
    {
        var list = new List<PackDependency>();
        if (!root.TryGetProperty("dependsOn", out var element)) return list;
        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: dependsOn must be an array of {{id, minVersion}}.");
            return list;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: every dependsOn entry must be an object {{id, minVersion}}.");
                continue;
            }

            var depId = RequiredString(entry, "id", $"{label} dependsOn", problems);
            var minVersion = RequiredString(entry, "minVersion", $"{label} dependsOn", problems);
            if (depId is null || minVersion is null) continue;
            if (!IdPattern.IsMatch(depId))
                problems.Add($"{label}: dependsOn id '{depId}' must match ^[a-z][a-z0-9-]{{1,38}}$.");
            if (!VersionPattern.IsMatch(minVersion))
            {
                problems.Add(
                    $"{label}: dependsOn '{depId}' minVersion '{minVersion}' must be a bare " +
                    "MAJOR.MINOR.PATCH — D7 allows no range grammar.");
            }
            if (selfId is not null && string.Equals(depId, selfId, StringComparison.Ordinal))
                problems.Add($"{label}: a pack cannot depend on itself.");
            if (!seen.Add(depId))
                problems.Add($"{label}: dependsOn lists '{depId}' more than once.");
            list.Add(new PackDependency(depId, minVersion));
        }
        return list;
    }

    private static PackProvides? ParseProvides(JsonElement root, string label, List<string> problems)
    {
        if (!root.TryGetProperty("provides", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: provides is required and must be an object.");
            return null;
        }

        var agents = RequiredStringArray(element, "agents", $"{label} provides", problems);
        foreach (var slug in agents)
        {
            if (!IdPattern.IsMatch(slug))
                problems.Add($"{label}: provides.agents slug '{slug}' must match ^[a-z][a-z0-9-]{{1,38}}$.");
        }
        RejectDuplicates(agents, $"{label}: provides.agents", problems);

        var scripts = OptionalStringArray(element, "scripts", $"{label} provides", problems);
        RejectDuplicates(scripts, $"{label}: provides.scripts", problems);
        foreach (var script in scripts)
        {
            // Paths are relative to Agents/ and must stay inside the pack — a pack that could
            // write "../.." would defeat every other containment rule in this file.
            if (script.StartsWith('/') || script.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(script))
                problems.Add($"{label}: provides.scripts path '{script}' must be a relative path inside Agents/.");
        }

        var teams = OptionalStringArray(element, "teams", $"{label} provides", problems);
        RejectDuplicates(teams, $"{label}: provides.teams", problems);

        var automations = OptionalStringArray(element, "automations", $"{label} provides", problems);
        RejectDuplicates(automations, $"{label}: provides.automations", problems);

        var rootFiles = OptionalStringArray(element, "rootFiles", $"{label} provides", problems);
        RejectDuplicates(rootFiles, $"{label}: provides.rootFiles", problems);
        foreach (var file in rootFiles)
        {
            if (file.StartsWith('/') || file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file))
                problems.Add($"{label}: provides.rootFiles path '{file}' must be relative to the workspace root.");
        }

        return new PackProvides(agents, scripts, teams, automations, rootFiles);
    }

    private static IReadOnlyList<PackAutomationPatch> ParsePatches(
        JsonElement root, string label, List<string> problems)
    {
        var list = new List<PackAutomationPatch>();
        if (!root.TryGetProperty("automationPatches", out var element)) return list;
        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: automationPatches must be an array.");
            return list;
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: every automationPatches entry must be an object.");
                continue;
            }

            var automation = RequiredString(entry, "automation", $"{label} automationPatches", problems);
            var op = RequiredString(entry, "op", $"{label} automationPatches", problems);
            if (automation is null || op is null) continue;

            if (!PackAutomationPatch.SupportedOps.Contains(op))
            {
                // D4: v1 ops are set additions only. That is the property that makes uninstall a
                // set subtraction; a reorder/remove/trigger-edit op would make it unreversible.
                problems.Add(
                    $"{label}: automationPatches op '{op}' on '{automation}' is not supported. " +
                    $"v1 accepts set additions only ({string.Join(", ", PackAutomationPatch.SupportedOps.Order(StringComparer.Ordinal))}); " +
                    "a pack that needs reordering, removal or trigger edits ships its own automation.");
                continue;
            }

            var slugs = OptionalStringArray(entry, "slugs", $"{label} automationPatches", problems);
            var labels = OptionalStringArray(entry, "labels", $"{label} automationPatches", problems);

            if (op == PackAutomationPatch.OpAddAssignees)
            {
                if (slugs.Count == 0)
                    problems.Add($"{label}: automationPatches addAssignees on '{automation}' requires a non-empty slugs[].");
                if (labels.Count > 0)
                    problems.Add($"{label}: automationPatches addAssignees on '{automation}' must not carry labels[].");
                foreach (var slug in slugs)
                {
                    if (!IdPattern.IsMatch(slug))
                        problems.Add($"{label}: automationPatches slug '{slug}' must match ^[a-z][a-z0-9-]{{1,38}}$.");
                }
            }
            else
            {
                if (labels.Count == 0)
                    problems.Add($"{label}: automationPatches addLabels on '{automation}' requires a non-empty labels[].");
                if (slugs.Count > 0)
                    problems.Add($"{label}: automationPatches addLabels on '{automation}' must not carry slugs[].");
            }

            list.Add(new PackAutomationPatch(automation, op, slugs, labels));
        }
        return list;
    }

    private static PackPermissions? ParsePermissions(JsonElement root, string label, List<string> problems)
    {
        if (!root.TryGetProperty("permissions", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: permissions is required and must be an object.");
            return null;
        }

        var scope = $"{label} permissions";
        var riskClasses = RequiredStringArray(element, "riskClasses", scope, problems, allowEmpty: true);
        var actions = RequiredStringArray(element, "actions", scope, problems, allowEmpty: true);
        var network = RequiredString(element, "network", scope, problems);
        var hosts = OptionalStringArray(element, "networkHosts", scope, problems);
        var globs = RequiredStringArray(element, "allowedWriteGlobs", scope, problems, allowEmpty: true);

        if (network is not null
            && network != PackPermissions.NetworkNone
            && network != PackPermissions.NetworkDeclared)
        {
            problems.Add($"{label}: permissions.network '{network}' must be \"none\" or \"declared\".");
        }
        if (network == PackPermissions.NetworkDeclared && hosts.Count == 0)
            problems.Add($"{label}: permissions.network is \"declared\" but networkHosts is empty.");
        if (network == PackPermissions.NetworkNone && hosts.Count > 0)
            problems.Add($"{label}: permissions.network is \"none\" but networkHosts lists {hosts.Count} host(s).");

        if (network is null) return null;
        return new PackPermissions(riskClasses, actions, network, hosts, globs);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseStringListMap(
        JsonElement root, string property, string label, List<string> problems)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty(property, out var element)) return map;
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: {property} must be an object of key → string[].");
            return map;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{label}: {property}.{prop.Name} must be an array of strings.");
                continue;
            }
            var values = new List<string>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    problems.Add($"{label}: {property}.{prop.Name} must contain non-empty strings only.");
                    continue;
                }
                values.Add(item.GetString()!);
            }
            RejectDuplicates(values, $"{label}: {property}.{prop.Name}", problems);
            map[prop.Name] = values;
        }
        return map;
    }

    private static string? RequiredString(JsonElement parent, string property, string label, List<string> problems)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{label}: {property} is required and must be a string.");
            return null;
        }
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{label}: {property} must not be empty.");
            return null;
        }
        return value;
    }

    private static int? RequiredInt(JsonElement parent, string property, string label, List<string> problems)
    {
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
        {
            problems.Add($"{label}: {property} is required and must be an integer.");
            return null;
        }
        return value;
    }

    private static bool? RequiredBool(JsonElement parent, string property, string label, List<string> problems)
    {
        if (!parent.TryGetProperty(property, out var element)
            || (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            problems.Add($"{label}: {property} is required and must be a boolean.");
            return null;
        }
        return element.GetBoolean();
    }

    private static IReadOnlyList<string> RequiredStringArray(
        JsonElement parent, string property, string label, List<string> problems, bool allowEmpty = false)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: {property} is required and must be an array of strings.");
            return Array.Empty<string>();
        }
        var values = ReadStringArray(element, property, label, problems);
        if (!allowEmpty && values.Count == 0)
            problems.Add($"{label}: {property} must not be empty.");
        return values;
    }

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement parent, string property, string label, List<string> problems)
    {
        if (!parent.TryGetProperty(property, out var element)) return Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: {property} must be an array of strings.");
            return Array.Empty<string>();
        }
        return ReadStringArray(element, property, label, problems);
    }

    private static List<string> ReadStringArray(
        JsonElement element, string property, string label, List<string> problems)
    {
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                problems.Add($"{label}: {property} must contain non-empty strings only.");
                continue;
            }
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static void RejectDuplicates(IReadOnlyList<string> values, string label, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value)) problems.Add($"{label} lists '{value}' more than once.");
        }
    }

    private static T Fail<T>(List<string> problems, string message, T fallback)
    {
        problems.Add(message);
        return fallback;
    }
}

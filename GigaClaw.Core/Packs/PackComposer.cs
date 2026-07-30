using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Packs;

/// <summary>
/// The single implementation of pack composition (doc/pack-infrastructure.md §4), shared by
/// <see cref="PackInstaller"/> and by <c>GigaClaw.Catalog</c>. Sharing is not a style preference:
/// if the catalog composed independently, a green catalog would say nothing about the bytes
/// Initialize writes and the §7 gate would be decorative.
///
/// Everything here runs entirely in memory. A rejected composition throws before a single byte is
/// written (§4, D5 step 1).
/// </summary>
public static class PackComposer
{
    /// <summary>Files directly under <c>Agents/</c> that are merged rather than copied.</summary>
    public const string AutomationsFile = "automations.json";
    public const string ContractsFile = "contracts.json";
    public const string ModelsFile = "models.json";
    public const string TeamsFile = "teams.json";

    private static readonly IReadOnlySet<string> MergeArtifacts =
        new HashSet<string>(StringComparer.Ordinal) { AutomationsFile, ContractsFile, ModelsFile, TeamsFile };

    public static PackComposition Compose(IReadOnlyList<IPackSource> sources, PackComposeOptions? options = null)
    {
        var opts = options ?? new PackComposeOptions();
        var errors = new List<string>();

        var manifests = ParseManifests(sources, errors);
        Throw(errors);

        var ordered = Order(manifests, opts.RuntimeVersion, errors);
        Throw(errors);

        var composed = new List<ComposedPack>();
        foreach (var (manifest, source) in ordered)
        {
            var pack = Walk(manifest, source, opts.RuntimeVersion, errors);
            if (pack is not null) composed.Add(pack);
        }
        Throw(errors);

        CheckCollisions(composed, errors);
        CheckOwnership(composed, errors);
        CheckPermissions(composed, errors);
        CheckCrossPackReferences(composed, opts.HostProvidedAgents, errors);
        CheckTeamMembershipAndPatches(composed, errors);
        Throw(errors);

        return new PackComposition(composed, opts.RuntimeVersion);
    }

    private static void Throw(List<string> errors)
    {
        if (errors.Count > 0) throw new PackValidationException(errors.ToArray());
    }

    // ---------------------------------------------------------------- manifests and ordering

    private static List<(PackManifest Manifest, IPackSource Source)> ParseManifests(
        IReadOnlyList<IPackSource> sources, List<string> errors)
    {
        var parsed = new List<(PackManifest, IPackSource)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!seen.Add(source.Id))
            {
                errors.Add($"pack '{source.Id}' appears more than once in the selection.");
                continue;
            }
            try
            {
                var manifest = PackManifestParser.Parse(source.ReadManifest(), source.Id);
                parsed.Add((manifest, source));
            }
            catch (PackValidationException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }

        var cores = parsed.Where(p => p.Item1.Kind == PackKind.Core).Select(p => p.Item1.Id).ToList();
        if (cores.Count > 1)
            errors.Add($"exactly one pack may declare kind \"core\"; found {string.Join(", ", cores)}.");

        return parsed;
    }

    private static List<(PackManifest Manifest, IPackSource Source)> Order(
        List<(PackManifest Manifest, IPackSource Source)> parsed,
        int runtimeVersion,
        List<string> errors)
    {
        var byId = parsed.ToDictionary(p => p.Manifest.Id, p => p, StringComparer.Ordinal);

        foreach (var (manifest, _) in parsed)
        {
            // §5: a future-runtime pack is refused outright. A too-old pack is quarantined, not
            // rejected — its files stay, its automations are force-disabled, its agents are
            // refused at dispatch. Never auto-upgraded, never auto-removed.
            if (manifest.RequiresRuntime.Evaluate(runtimeVersion) == PackCompatibility.RefusedTooNew)
            {
                errors.Add(
                    $"pack '{manifest.Id}' requires pack-runtime >= {manifest.RequiresRuntime.Min} " +
                    $"but this build is {runtimeVersion}. Install refused.");
            }

            foreach (var dependency in manifest.DependsOn)
            {
                if (!byId.TryGetValue(dependency.Id, out var target))
                {
                    errors.Add(
                        $"pack '{manifest.Id}' dependsOn '{dependency.Id}', which is not in the selection.");
                    continue;
                }
                if (CompareVersions(target.Manifest.Version, dependency.MinVersion) < 0)
                {
                    errors.Add(
                        $"pack '{manifest.Id}' requires '{dependency.Id}' >= {dependency.MinVersion} " +
                        $"but {target.Manifest.Version} is selected.");
                }
            }
        }
        if (errors.Count > 0) return parsed;

        // Core first, then topological by dependsOn, ties by id ascending ordinal. Deterministic
        // ordering is required by the byte-identity invariant (§6) and by CatalogGenerator's
        // stable-output contract.
        var remaining = parsed
            .OrderBy(p => p.Manifest.Kind == PackKind.Core ? 0 : 1)
            .ThenBy(p => p.Manifest.Id, StringComparer.Ordinal)
            .ToList();

        var ordered = new List<(PackManifest, IPackSource)>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(p => p.Manifest.DependsOn.All(d => placed.Contains(d.Id)));
            if (next.Manifest is null)
            {
                errors.Add(
                    "pack dependency cycle among: " +
                    string.Join(", ", remaining.Select(p => p.Manifest.Id).Order(StringComparer.Ordinal)));
                return parsed;
            }
            ordered.Add(next);
            placed.Add(next.Manifest.Id);
            remaining.Remove(next);
        }
        return ordered;
    }

    /// <summary>
    /// D7's pack-to-pack comparison. <see cref="Services.VersionCompare"/> already gives a
    /// <see cref="Version"/>-based comparison; manifest versions are validated as bare
    /// MAJOR.MINOR.PATCH at parse time, so <see cref="Version.Parse(string)"/> is total here.
    /// </summary>
    internal static int CompareVersions(string left, string right) =>
        Version.Parse(left).CompareTo(Version.Parse(right));

    // ---------------------------------------------------------------- per-pack tree walk

    private static ComposedPack? Walk(
        PackManifest manifest, IPackSource source, int runtimeVersion, List<string> errors)
    {
        var label = $"pack '{manifest.Id}'";
        var files = new List<ComposedFile>();
        var actualAgents = new HashSet<string>(StringComparer.Ordinal);
        var agentDirs = new HashSet<string>(StringComparer.Ordinal);
        var actualScripts = new HashSet<string>(StringComparer.Ordinal);

        JsonNode? contractDefaults = null;
        var contractAgents = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var models = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var modelsPreamble = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var teams = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var teamOrder = new List<string>();
        var automations = new List<AutomationRule>();

        foreach (var rel in source.AgentRelativePaths())
        {
            var segments = rel.Split('/');
            if (segments.Length == 1)
            {
                if (MergeArtifacts.Contains(rel)) continue; // handled below
                if (manifest.Kind != PackKind.Core)
                {
                    // D3: preamble.md and the other shared Agents/-root files are core-owned. A
                    // specialist pack contributes prose through its own declared root file.
                    errors.Add(
                        $"{label}: '{rel}' sits directly under Agents/ but only the core pack may " +
                        "contribute shared Agents/-root files. Put it under the agent's own " +
                        "directory or declare it in provides.rootFiles.");
                    continue;
                }
                files.Add(new ComposedFile(".agents/" + rel, manifest.Id, source.ReadAgentAsset(rel)));
                continue;
            }

            if (segments[0] == "scripts")
            {
                actualScripts.Add(rel);
                files.Add(new ComposedFile(".agents/" + rel, manifest.Id, source.ReadAgentAsset(rel)));
                continue;
            }

            agentDirs.Add(segments[0]);
            if (segments.Length == 2 && segments[1] == "SKILL.md") actualAgents.Add(segments[0]);
            files.Add(new ComposedFile(".agents/" + rel, manifest.Id, source.ReadAgentAsset(rel)));
        }

        foreach (var rel in source.RootRelativePaths())
            files.Add(new ComposedFile(rel, manifest.Id, source.ReadRootAsset(rel)));

        var agentPathSet = source.AgentRelativePaths().ToHashSet(StringComparer.Ordinal);

        if (agentPathSet.Contains(ContractsFile))
            ReadContracts(manifest, source, ref contractDefaults, contractAgents, errors);
        if (agentPathSet.Contains(ModelsFile))
            ReadModels(manifest, source, models, modelsPreamble, errors);
        if (agentPathSet.Contains(TeamsFile))
            ReadTeams(manifest, source, teams, teamOrder, errors);
        if (agentPathSet.Contains(AutomationsFile))
            ReadAutomations(manifest, source, automations, errors);

        // `provides` is declared AND verified — a mismatch in either direction is an error (§3).
        VerifySet(label, "provides.agents", manifest.Provides.Agents, actualAgents, "Agents/<slug>/SKILL.md", errors);
        VerifySet(label, "provides.scripts", manifest.Provides.Scripts, actualScripts, "Agents/scripts/**", errors);
        VerifySet(label, "provides.rootFiles", manifest.Provides.RootFiles,
            source.RootRelativePaths().ToHashSet(StringComparer.Ordinal), "the pack root", errors);
        VerifySet(label, "provides.teams", manifest.Provides.Teams,
            teams.Keys.ToHashSet(StringComparer.Ordinal), "Agents/teams.json", errors);
        VerifySet(label, "provides.automations", manifest.Provides.Automations,
            automations.Select(a => a.Id).ToHashSet(StringComparer.Ordinal), "Agents/automations.json", errors);

        foreach (var dir in agentDirs.Except(actualAgents, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            errors.Add($"{label}: Agents/{dir}/ has no SKILL.md, so it declares no agent.");

        return new ComposedPack(
            manifest,
            manifest.RequiresRuntime.Evaluate(runtimeVersion),
            files,
            automations,
            contractDefaults,
            contractAgents,
            models,
            teams)
        { ModelsPreamble = modelsPreamble, TeamOrder = teamOrder };
    }

    private static void VerifySet(
        string label,
        string field,
        IReadOnlyList<string> declared,
        IReadOnlySet<string> actual,
        string treeDescription,
        List<string> errors)
    {
        var declaredSet = declared.ToHashSet(StringComparer.Ordinal);
        foreach (var missing in declaredSet.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            errors.Add($"{label}: {field} declares '{missing}' but it is absent from {treeDescription}.");
        foreach (var extra in actual.Except(declaredSet, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            errors.Add($"{label}: {treeDescription} contains '{extra}' but {field} does not declare it.");
    }

    private static void ReadContracts(
        PackManifest manifest,
        IPackSource source,
        ref JsonNode? defaults,
        Dictionary<string, JsonNode> agents,
        List<string> errors)
    {
        var label = $"pack '{manifest.Id}'";
        var root = ParseObject(source.ReadAgentAsset(ContractsFile), label, ContractsFile, errors);
        if (root is null) return;

        if (root.TryGetPropertyValue("defaults", out var defaultsNode) && defaultsNode is not null)
        {
            if (manifest.Kind != PackKind.Core)
            {
                // §4 merge table: contracts.json `defaults` is core-only.
                errors.Add($"{label}: contracts.json carries 'defaults', which only the core pack may define.");
            }
            else
            {
                defaults = defaultsNode.DeepClone();
            }
        }

        if (!root.TryGetPropertyValue("agents", out var agentsNode) || agentsNode is not JsonObject agentsObject)
        {
            errors.Add($"{label}: contracts.json must carry an 'agents' object.");
            return;
        }

        foreach (var entry in agentsObject)
        {
            if (entry.Value is null) continue;
            agents[entry.Key] = entry.Value.DeepClone();
        }
    }

    private static void ReadModels(
        PackManifest manifest,
        IPackSource source,
        Dictionary<string, JsonNode> models,
        Dictionary<string, JsonNode> preamble,
        List<string> errors)
    {
        var label = $"pack '{manifest.Id}'";
        var root = ParseObject(source.ReadAgentAsset(ModelsFile), label, ModelsFile, errors);
        if (root is null) return;
        foreach (var entry in root)
        {
            if (entry.Value is null) continue;
            // "_comment" is documentation, not a pseudo-agent slug — the same convention
            // AgentsTemplateService.DefaultModels() already honours. Kept aside rather than
            // dropped, so the file the owner opens still explains itself.
            if (entry.Key.StartsWith('_')) preamble[entry.Key] = entry.Value.DeepClone();
            else models[entry.Key] = entry.Value.DeepClone();
        }
    }

    private static void ReadTeams(
        PackManifest manifest,
        IPackSource source,
        Dictionary<string, JsonObject> teams,
        List<string> teamOrder,
        List<string> errors)
    {
        var label = $"pack '{manifest.Id}'";
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(source.ReadAgentAsset(TeamsFile));
        }
        catch (JsonException ex)
        {
            errors.Add($"{label}: {TeamsFile} is not valid JSON — {ex.Message}");
            return;
        }

        var array = TeamsArrayOf(root);
        if (array is null)
        {
            errors.Add(
                $"{label}: {TeamsFile} must be a JSON array of team objects, or the object form " +
                "{{ \"schemaVersion\": 1, \"teams\": [...] }}.");
            return;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject team || team["slug"]?.GetValue<string>() is not { } slug)
            {
                errors.Add($"{label}: every {TeamsFile} entry must be an object with a 'slug'.");
                continue;
            }
            if (!teams.TryAdd(slug, (JsonObject)team.DeepClone()))
                errors.Add($"{label}: {TeamsFile} defines team '{slug}' more than once.");
            else teamOrder.Add(slug);
        }
    }

    private static void ReadAutomations(
        PackManifest manifest, IPackSource source, List<AutomationRule> automations, List<string> errors)
    {
        var label = $"pack '{manifest.Id}'";
        try
        {
            var config = JsonSerializer.Deserialize<AutomationConfig>(
                source.ReadAgentAsset(AutomationsFile), AutomationStore.JsonOptions);
            if (config is null)
            {
                errors.Add($"{label}: {AutomationsFile} deserialized to null.");
                return;
            }
            automations.AddRange(config.Automations);
        }
        catch (JsonException ex)
        {
            errors.Add($"{label}: {AutomationsFile} is not a valid automation config — {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            // Unknown trigger/condition/action discriminator: the pack is written against a
            // vocabulary this runtime does not have. Fail closed rather than drop the member.
            errors.Add($"{label}: {AutomationsFile} uses an automation type this runtime does not know — {ex.Message}");
        }
    }

    /// <summary>
    /// The teams array of either <c>teams.json</c> shape, or null if the node is neither.
    ///
    /// <para>Both shapes are real and both must work here. Core ships the object form —
    /// <c>{ "_comment": …, "schemaVersion": 1, "teams": [...], "teamMembership": {…} }</c> — which is
    /// what <see cref="Models.TeamSeed.Parse"/> reads and what Initialize writes to
    /// <c>.agents/teams.json</c>; a pack fragment may ship the bare array. Accepting only the array
    /// (as this did before the core-pack extraction) would reject core's own roster.</para>
    /// </summary>
    internal static JsonArray? TeamsArrayOf(JsonNode? root) => root switch
    {
        JsonArray array => array,
        JsonObject obj => obj["teams"] as JsonArray,
        _ => null,
    };

    private static JsonObject? ParseObject(byte[] bytes, string label, string file, List<string> errors)
    {
        try
        {
            if (JsonNode.Parse(bytes) is JsonObject obj) return obj;
            errors.Add($"{label}: {file} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            errors.Add($"{label}: {file} is not valid JSON — {ex.Message}");
        }
        return null;
    }

    // ---------------------------------------------------------------- cross-pack rules

    private static void CheckCollisions(IReadOnlyList<ComposedPack> packs, List<string> errors)
    {
        // D2: agent slugs are one flat global namespace and collisions are refused, never
        // namespaced. The slug is simultaneously the Member.Slug row, the contracts.json key, the
        // models.json key, the runAgent.agent value, the .agents/<slug>/memory/ directory, the
        // assignedTo condition value and the receipt-chain emitter identity.
        DetectDuplicates(packs, p => p.Manifest.Provides.Agents, "agent slug", errors);
        DetectDuplicates(packs, p => p.Files.Select(f => f.DestinationPath), "workspace path", errors);
        DetectDuplicates(packs, p => p.Teams.Keys, "team slug", errors);
        DetectDuplicates(packs, p => p.Automations.Select(a => a.Id), "automation id", errors);
        DetectDuplicates(packs, p => p.ContractAgents.Keys, "contracts.json key", errors);
        DetectDuplicates(packs, p => p.Models.Keys, "models.json key", errors);
    }

    private static void DetectDuplicates(
        IReadOnlyList<ComposedPack> packs,
        Func<ComposedPack, IEnumerable<string>> selector,
        string what,
        List<string> errors)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            foreach (var key in selector(pack))
            {
                if (owner.TryGetValue(key, out var existing))
                {
                    errors.Add(
                        $"{what} '{key}' is claimed by both pack '{existing}' and pack '{pack.Id}'. " +
                        "Two packs claiming one name is a packaging bug; install is refused.");
                    continue;
                }
                owner[key] = pack.Id;
            }
        }
    }

    private static void CheckOwnership(IReadOnlyList<ComposedPack> packs, List<string> errors)
    {
        foreach (var pack in packs)
        {
            var own = pack.Manifest.Provides.Agents.ToHashSet(StringComparer.Ordinal);
            foreach (var key in pack.ContractAgents.Keys.Order(StringComparer.Ordinal))
            {
                if (!own.Contains(key))
                    errors.Add($"pack '{pack.Id}': contracts.json contracts for '{key}', which it does not provide.");
            }
            foreach (var key in pack.Models.Keys.Order(StringComparer.Ordinal))
            {
                if (!own.Contains(key))
                    errors.Add($"pack '{pack.Id}': models.json maps '{key}', which it does not provide.");
            }
        }
    }

    private static void CheckPermissions(IReadOnlyList<ComposedPack> packs, List<string> errors)
    {
        var byId = packs.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var pack in packs)
        {
            var label = $"pack '{pack.Id}'";
            var ceiling = pack.Manifest.Permissions.AllowedWriteGlobs.ToHashSet(StringComparer.Ordinal);
            var riskClasses = new HashSet<string>(pack.Manifest.Permissions.RiskClasses, StringComparer.Ordinal);
            foreach (var visible in VisiblePacks(pack, byId))
                riskClasses.UnionWith(visible.Manifest.Permissions.RiskClasses);

            foreach (var (slug, contract) in pack.ContractAgents.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (contract is not JsonObject obj) continue;

                if (obj["riskClass"]?.GetValue<string>() is { } riskClass && !riskClasses.Contains(riskClass))
                {
                    // Unknown risk classes fail closed in P3 enforcement, so they must be declared.
                    errors.Add(
                        $"{label}: agent '{slug}' uses riskClass '{riskClass}', which is not in " +
                        "permissions.riskClasses nor declared by core or a declared dependency.");
                }

                if (obj["allowedWriteGlobs"] is JsonArray globs)
                {
                    foreach (var glob in globs)
                    {
                        var value = glob?.GetValue<string>();
                        if (value is null || ceiling.Contains(value)) continue;
                        // §7.5: the manifest ceiling exists so a reviewer reading only pack.json
                        // cannot be surprised by a per-agent "**".
                        errors.Add(
                            $"{label}: agent '{slug}' allowedWriteGlobs contains '{value}', which is " +
                            "not in the manifest's permissions.allowedWriteGlobs ceiling.");
                    }
                }
            }

            var declaredActions = pack.Manifest.Permissions.Actions.ToHashSet(StringComparer.Ordinal);
            foreach (var automation in pack.Automations)
            {
                foreach (var action in automation.Actions)
                {
                    if (declaredActions.Contains(action.UiTypeKey)) continue;
                    // §7.6: closure check — the manifest must name every action type the pack uses.
                    errors.Add(
                        $"{label}: automation '{automation.Id}' uses action '{action.UiTypeKey}', " +
                        "which is not in permissions.actions.");
                }

                if (pack.Manifest.Permissions.Network == PackPermissions.NetworkNone
                    && automation.Actions.Any(a => a.UiTypeKey == "httpRequest"))
                {
                    errors.Add(
                        $"{label}: automation '{automation.Id}' performs an httpRequest but " +
                        "permissions.network is \"none\".");
                }
            }
        }
    }

    private static void CheckCrossPackReferences(
        IReadOnlyList<ComposedPack> packs, IReadOnlySet<string>? hostProvidedAgents, List<string> errors)
    {
        var byId = packs.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var pack in packs)
        {
            // D4: cross-pack references are allowed, but only through a declared dependency.
            var resolvable = new HashSet<string>(pack.Manifest.Provides.Agents, StringComparer.Ordinal);
            foreach (var visible in VisiblePacks(pack, byId))
                resolvable.UnionWith(visible.Manifest.Provides.Agents);
            if (hostProvidedAgents is not null) resolvable.UnionWith(hostProvidedAgents);

            foreach (var automation in pack.Automations)
            {
                foreach (var run in automation.Actions.OfType<RunAgentActionSpec>())
                {
                    if (run.Agent.Contains("{assignee}", StringComparison.Ordinal)) continue;
                    if (resolvable.Contains(run.Agent)) continue;
                    errors.Add(
                        $"pack '{pack.Id}': automation '{automation.Id}' runs agent '{run.Agent}', " +
                        "which is provided by neither this pack, core, nor a pack named in dependsOn.");
                }

                foreach (var condition in automation.Conditions.OfType<AssignedToConditionSpec>())
                {
                    foreach (var slug in condition.Slugs)
                    {
                        if (resolvable.Contains(slug)) continue;
                        errors.Add(
                            $"pack '{pack.Id}': automation '{automation.Id}' has an assignedTo condition " +
                            $"naming '{slug}', which is provided by neither this pack, core, nor a pack " +
                            "named in dependsOn.");
                    }
                }
            }
        }
    }

    private static void CheckTeamMembershipAndPatches(IReadOnlyList<ComposedPack> packs, List<string> errors)
    {
        var byId = packs.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var automationOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            foreach (var automation in pack.Automations) automationOwner.TryAdd(automation.Id, pack.Id);
        }

        foreach (var pack in packs)
        {
            var own = pack.Manifest.Provides.Agents.ToHashSet(StringComparer.Ordinal);

            foreach (var (team, slugs) in pack.Manifest.TeamMembership.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (pack.Teams.ContainsKey(team))
                {
                    errors.Add(
                        $"pack '{pack.Id}': teamMembership targets '{team}', which this pack defines " +
                        "itself — list the members in teams.json instead.");
                }
                foreach (var slug in slugs)
                {
                    if (!own.Contains(slug))
                    {
                        errors.Add(
                            $"pack '{pack.Id}': teamMembership adds '{slug}' to team '{team}', but the " +
                            "pack does not provide that agent. Membership is additive for a pack's own agents.");
                    }
                }
            }

            foreach (var patch in pack.Manifest.AutomationPatches)
            {
                if (!automationOwner.TryGetValue(patch.Automation, out var ownerId)) continue;
                if (string.Equals(ownerId, pack.Id, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"pack '{pack.Id}': automationPatches targets '{patch.Automation}', which the pack " +
                        "owns — edit the automation directly.");
                    continue;
                }
                if (!VisiblePacks(pack, byId).Any(v => string.Equals(v.Id, ownerId, StringComparison.Ordinal)))
                {
                    errors.Add(
                        $"pack '{pack.Id}': automationPatches targets '{patch.Automation}', owned by pack " +
                        $"'{ownerId}', which is neither core nor a declared dependency.");
                }
                if (patch.Op == PackAutomationPatch.OpAddAssignees)
                {
                    foreach (var slug in patch.Slugs.Where(s => !own.Contains(s)))
                    {
                        errors.Add(
                            $"pack '{pack.Id}': automationPatches adds assignee '{slug}' to " +
                            $"'{patch.Automation}', but the pack does not provide that agent.");
                    }
                }
            }
        }
    }

    /// <summary>Packs a given pack may reference: core plus its direct declared dependencies (D4).</summary>
    private static IEnumerable<ComposedPack> VisiblePacks(
        ComposedPack pack, IReadOnlyDictionary<string, ComposedPack> byId)
    {
        foreach (var candidate in byId.Values)
        {
            if (candidate.Manifest.Kind == PackKind.Core) yield return candidate;
        }
        foreach (var dependency in pack.Manifest.DependsOn)
        {
            if (byId.TryGetValue(dependency.Id, out var target) && target.Manifest.Kind != PackKind.Core)
                yield return target;
        }
    }
}

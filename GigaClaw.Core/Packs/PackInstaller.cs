using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Packs;

/// <summary>
/// The staged install / uninstall engine of doc/pack-infrastructure.md §4 (D5).
///
/// Install is a four-step transaction:
/// <list type="number">
/// <item>compose and validate entirely in memory, so a rejected install leaves the disk untouched;</item>
/// <item>stage into <c>&lt;workspace&gt;/.agents.staging-&lt;installId&gt;/</c>;</item>
/// <item>merge <em>per file</em> into <c>.agents/</c> and the workspace root, capturing a pre-image
///       of every file overwritten — any failure restores the pre-images and drops the staging dir;</item>
/// <item>write <c>.agents/packs.lock.json</c> last. Its presence with a matching installId is what
///       makes the install committed; an interrupted install leaves a stale staging directory and
///       an unchanged lockfile, and the next install sweeps it.</item>
/// </list>
///
/// The engine never touches a path no manifest claims. That is the whole reason the merge is
/// per-file rather than a rename-swap of <c>.agents/</c>: that directory also holds live runtime
/// state a swap would destroy.
/// </summary>
public sealed partial class PackInstaller
{
    public const string StagingPrefix = ".agents.staging-";

    /// <summary>Test seam for the partial-install path: invoked with each destination just before
    /// it is merged, so a test can fail the transaction mid-way and assert there is no residue.</summary>
    internal Action<string>? BeforeFileMerge { get; set; }

    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    // ------------------------------------------------------------------------------- install

    public async Task<PackInstallResult> InstallAsync(
        string workspacePath,
        IReadOnlyList<IPackSource> sources,
        PackInstallOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? new PackInstallOptions();
        var workspace = Path.GetFullPath(workspacePath);
        var agentsRelative = ".agents";
        var agentsDir = Path.Combine(workspace, agentsRelative);
        var lockRelative = agentsRelative + "/" + PackLockFile.FileName;

        var previous = ReadLock(Path.Combine(workspace, lockRelative));

        // Step 1 — compose and validate entirely in memory. Throws before anything is written.
        var composition = PackComposer.Compose(
            sources,
            new PackComposeOptions(opts.RuntimeVersion, HostProvidedAgents(agentsDir, previous)));

        var plan = new List<(string Destination, byte[] Content)>();
        var fileHashes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var mergeHashes = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var pack in composition.Packs)
        {
            fileHashes[pack.Id] = new Dictionary<string, string>(StringComparer.Ordinal);
            mergeHashes[pack.Id] = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }

        RefuseSlugsAlreadyInWorkspace(agentsDir, composition, previous);

        var preserved = new List<string>();
        PlanOpaqueFiles(workspace, composition, previous, opts, plan, fileHashes, preserved);

        var deferredMemberships = new List<string>();
        PlanMergeArtifacts(agentsDir, agentsRelative, composition, previous, plan, mergeHashes, deferredMemberships);

        var stale = PlanRemovalsFromPreviousVersion(workspace, composition, previous, fileHashes);

        // Step 2 — stage.
        var installId = Guid.NewGuid().ToString("d");
        SweepStagingDirectories(workspace);
        var stagingDir = Path.Combine(workspace, StagingPrefix + installId);
        try
        {
            foreach (var (destination, content) in plan)
            {
                var staged = Path.Combine(stagingDir, destination.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, content, ct);
            }

            // Step 3 — merge per file, pre-images captured.
            var transaction = new WorkspaceMergeTransaction(workspace);
            var lockFile = BuildLock(installId, opts.RuntimeVersion, composition, previous, fileHashes, mergeHashes);
            try
            {
                foreach (var relative in stale) transaction.Delete(relative);

                foreach (var (destination, content) in plan)
                {
                    BeforeFileMerge?.Invoke(destination);
                    await transaction.WriteAsync(destination, content, ct);
                }

                // Step 4 — the lockfile last. Its presence commits the install.
                await transaction.WriteAsync(
                    lockRelative, Encoding.UTF8.GetBytes(PackLockSerializer.ToJson(lockFile)), ct);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return new PackInstallResult(
                installId,
                plan.Select(p => p.Destination).ToList(),
                preserved,
                composition.Quarantined.Select(p => p.Id).ToList(),
                deferredMemberships,
                lockFile);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>
    /// Agent slugs already in this workspace that no installed pack owns — today, the
    /// <c>ProjectTemplate</c> agents <c>AgentsTemplateService</c> wrote. They satisfy D4's
    /// reference resolution while core is not yet a pack (see <see cref="PackComposeOptions"/>);
    /// once the core-pack extraction lands, every slug here belongs to the <c>core</c> manifest
    /// instead and this set is empty.
    /// </summary>
    private static IReadOnlySet<string> HostProvidedAgents(string agentsDir, PackLockFile? previous)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(agentsDir)) return slugs;
        foreach (var dir in Directory.EnumerateDirectories(agentsDir))
        {
            if (File.Exists(Path.Combine(dir, "SKILL.md"))) slugs.Add(new DirectoryInfo(dir).Name);
        }
        foreach (var entry in previous?.Packs ?? Array.Empty<PackLockEntry>())
            slugs.ExceptWith(entry.Agents);
        return slugs;
    }

    /// <summary>
    /// D2's flat global slug namespace, checked against the workspace rather than only across the
    /// selection: a pack may not claim a slug that already exists in <c>.agents/</c> and is not
    /// recorded as belonging to a pack in this transaction. Today that catches a pack colliding
    /// with a <c>ProjectTemplate</c> agent written by <c>AgentsTemplateService</c>; after the
    /// core-pack extraction the same check catches it against the <c>core</c> pack.
    /// </summary>
    private static void RefuseSlugsAlreadyInWorkspace(
        string agentsDir, PackComposition composition, PackLockFile? previous)
    {
        if (!Directory.Exists(agentsDir)) return;

        var reinstalling = composition.Packs.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var packOwned = (previous?.Packs ?? Array.Empty<PackLockEntry>())
            .Where(p => reinstalling.Contains(p.Id))
            .SelectMany(p => p.Agents)
            .ToHashSet(StringComparer.Ordinal);

        var errors = new List<string>();
        foreach (var pack in composition.Packs)
        {
            foreach (var slug in pack.Manifest.Provides.Agents)
            {
                if (packOwned.Contains(slug)) continue;
                if (!File.Exists(Path.Combine(agentsDir, slug, "SKILL.md"))) continue;
                errors.Add(
                    $"pack '{pack.Id}' provides agent '{slug}', but this workspace already has " +
                    $".agents/{slug}/SKILL.md that no installed pack owns. Agent slugs are one flat " +
                    "global namespace; install is refused rather than namespaced.");
            }
        }
        if (errors.Count > 0) throw new PackValidationException(errors);
    }

    private void PlanOpaqueFiles(
        string workspace,
        PackComposition composition,
        PackLockFile? previous,
        PackInstallOptions options,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, string>> fileHashes,
        List<string> preserved)
    {
        foreach (var pack in composition.Packs)
        {
            var previousHashes = previous?.Find(pack.Id)?.FileHashes;
            foreach (var file in pack.Files.OrderBy(f => f.DestinationPath, StringComparer.Ordinal))
            {
                var intended = PackFileHash.OfBytes(file.Content);
                var full = Path.Combine(workspace, file.DestinationPath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(full))
                {
                    var current = PackFileHash.OfBytes(File.ReadAllBytes(full));
                    var packOwned = current == intended
                        || (previousHashes is not null
                            && previousHashes.TryGetValue(file.DestinationPath, out var was)
                            && was == current);

                    if (!packOwned && !options.OverwriteConflicts)
                    {
                        // The owner edited a file this pack wants to write. Never silently
                        // overwritten, and deliberately not recorded in fileHashes so uninstall
                        // will not delete it either.
                        preserved.Add(file.DestinationPath);
                        continue;
                    }
                }

                plan.Add((file.DestinationPath, file.Content));
                fileHashes[pack.Id][file.DestinationPath] = intended;
            }
        }
    }

    /// <summary>
    /// Files a previous version of a pack in this transaction wrote and the new version no longer
    /// claims. Deleted only when still byte-identical to what was installed — the same owner-edit
    /// protection uninstall applies, since an upgrade is an uninstall and an install inside one
    /// staged transaction (§5).
    /// </summary>
    private static List<string> PlanRemovalsFromPreviousVersion(
        string workspace,
        PackComposition composition,
        PackLockFile? previous,
        Dictionary<string, Dictionary<string, string>> fileHashes)
    {
        var stale = new List<string>();
        if (previous is null) return stale;

        foreach (var pack in composition.Packs)
        {
            var entry = previous.Find(pack.Id);
            if (entry is null) continue;
            foreach (var (relative, hash) in entry.FileHashes.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (fileHashes[pack.Id].ContainsKey(relative)) continue;
                var full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                if (PackFileHash.OfBytes(File.ReadAllBytes(full)) != hash) continue;
                stale.Add(relative);
            }
        }
        return stale;
    }

    // --------------------------------------------------------------------- the merge artifacts

    private static void PlanMergeArtifacts(
        string agentsDir,
        string agentsRelative,
        PackComposition composition,
        PackLockFile? previous,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes,
        List<string> deferredMemberships)
    {
        PlanContracts(agentsDir, agentsRelative, composition, plan, mergeHashes);
        PlanModels(agentsDir, agentsRelative, composition, plan, mergeHashes);
        PlanTeams(agentsDir, agentsRelative, composition, previous, plan, mergeHashes, deferredMemberships);
        PlanAutomations(agentsDir, agentsRelative, composition, previous, plan, mergeHashes);
    }

    private static void PlanContracts(
        string agentsDir,
        string agentsRelative,
        PackComposition composition,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes)
    {
        if (!composition.Packs.Any(p => p.ContractAgents.Count > 0 || p.ContractDefaults is not null)) return;

        var root = LoadObject(Path.Combine(agentsDir, PackComposer.ContractsFile))
            ?? new JsonObject { ["version"] = 1, ["defaults"] = new JsonObject(), ["agents"] = new JsonObject() };
        if (root["agents"] is not JsonObject agents)
        {
            agents = new JsonObject();
            root["agents"] = agents;
        }

        foreach (var pack in composition.Packs)
        {
            // §4 merge table: `defaults` is core-only; the composer already refused a non-core
            // pack that carries it.
            if (pack.ContractDefaults is not null && pack.Manifest.Kind == PackKind.Core)
                root["defaults"] = pack.ContractDefaults.DeepClone();

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, node) in pack.ContractAgents.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var clone = node.DeepClone();
                agents[key] = clone;
                hashes[key] = PackFileHash.OfNode(clone);
            }
            if (hashes.Count > 0) mergeHashes[pack.Id][PackComposer.ContractsFile] = hashes;
        }

        plan.Add((agentsRelative + "/" + PackComposer.ContractsFile, Serialize(root)));
    }

    private static void PlanModels(
        string agentsDir,
        string agentsRelative,
        PackComposition composition,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes)
    {
        if (!composition.Packs.Any(p => p.Models.Count > 0)) return;

        var root = LoadObject(Path.Combine(agentsDir, PackComposer.ModelsFile)) ?? new JsonObject();
        foreach (var pack in composition.Packs)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, node) in pack.Models.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var clone = node.DeepClone();
                root[key] = clone;
                hashes[key] = PackFileHash.OfNode(clone);
            }
            if (hashes.Count > 0) mergeHashes[pack.Id][PackComposer.ModelsFile] = hashes;
        }

        plan.Add((agentsRelative + "/" + PackComposer.ModelsFile, Serialize(root)));
    }

    private static void PlanTeams(
        string agentsDir,
        string agentsRelative,
        PackComposition composition,
        PackLockFile? previous,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes,
        List<string> deferredMemberships)
    {
        var contributes = composition.Packs.Any(p => p.Teams.Count > 0 || p.Manifest.TeamMembership.Count > 0);
        if (!contributes) return;

        var existing = LoadArray(Path.Combine(agentsDir, PackComposer.TeamsFile)) ?? new JsonArray();
        var ordered = new List<JsonObject>();
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in existing.OfType<JsonObject>())
        {
            var clone = (JsonObject)item.DeepClone();
            var slug = clone["slug"]?.GetValue<string>();
            if (slug is null) continue;
            ordered.Add(clone);
            index[slug] = clone;
        }

        var errors = new List<string>();
        foreach (var pack in composition.Packs)
        {
            var previouslyOwned = previous?.Find(pack.Id)?.Teams.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var (slug, team) in pack.Teams.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var clone = (JsonObject)team.DeepClone();
                if (index.TryGetValue(slug, out var current))
                {
                    if (!previouslyOwned.Contains(slug))
                    {
                        errors.Add(
                            $"pack '{pack.Id}' defines team '{slug}', which this workspace already has " +
                            "and no installed pack owns.");
                        continue;
                    }
                    ordered[ordered.IndexOf(current)] = clone;
                }
                else
                {
                    ordered.Add(clone);
                }
                index[slug] = clone;
            }
        }
        if (errors.Count > 0) throw new PackValidationException(errors);

        foreach (var pack in composition.Packs)
        {
            foreach (var (team, slugs) in pack.Manifest.TeamMembership.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!index.TryGetValue(team, out var target))
                {
                    // The nine built-in teams are still C# constants in AgentTeamService, so a
                    // membership targeting one cannot be applied to data yet. Reported, not fatal —
                    // it becomes a hard error once core ships Agents/teams.json (D8).
                    deferredMemberships.Add($"{pack.Id}: {team} ← {string.Join(", ", slugs)}");
                    continue;
                }
                if (target["agentSlugs"] is not JsonArray members)
                {
                    members = new JsonArray();
                    target["agentSlugs"] = members;
                }
                var present = members.Select(m => m?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                foreach (var slug in slugs)
                {
                    if (present.Contains(slug)) continue;  // additive, and idempotent on reinstall
                    members.Add(slug);
                    present.Add(slug);
                }
            }
        }

        // Hashed only after every mutation, so a team's recorded hash matches what is written.
        foreach (var pack in composition.Packs)
        {
            if (pack.Teams.Count == 0) continue;
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slug in pack.Teams.Keys.Order(StringComparer.Ordinal))
                hashes[slug] = PackFileHash.OfNode(index[slug]);
            mergeHashes[pack.Id][PackComposer.TeamsFile] = hashes;
        }

        plan.Add((agentsRelative + "/" + PackComposer.TeamsFile,
            Serialize(new JsonArray(ordered.Select(t => (JsonNode)t).ToArray()))));
    }

    private static void PlanAutomations(
        string agentsDir,
        string agentsRelative,
        PackComposition composition,
        PackLockFile? previous,
        List<(string Destination, byte[] Content)> plan,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes)
    {
        var contributes = composition.Packs.Any(
            p => p.Automations.Count > 0 || p.Manifest.AutomationPatches.Count > 0);
        if (!contributes) return;

        var config = LoadAutomations(Path.Combine(agentsDir, PackComposer.AutomationsFile));
        var byId = new Dictionary<string, AutomationRule>(StringComparer.Ordinal);
        foreach (var automation in config.Automations) byId[automation.Id] = automation;

        var errors = new List<string>();
        foreach (var pack in composition.Packs)
        {
            var previouslyOwned = previous?.Find(pack.Id)?.Automations.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var automation in pack.Automations)
            {
                if (byId.TryGetValue(automation.Id, out var current))
                {
                    if (!previouslyOwned.Contains(automation.Id))
                    {
                        errors.Add(
                            $"pack '{pack.Id}' defines automation '{automation.Id}', which this workspace " +
                            "already has and no installed pack owns.");
                        continue;
                    }
                    config.Automations[config.Automations.IndexOf(current)] = automation;
                }
                else
                {
                    config.Automations.Add(automation);
                }
                byId[automation.Id] = automation;
            }
        }
        if (errors.Count > 0) throw new PackValidationException(errors);

        foreach (var pack in composition.Packs)
        {
            foreach (var patch in pack.Manifest.AutomationPatches)
            {
                if (!byId.TryGetValue(patch.Automation, out var target))
                {
                    errors.Add(
                        $"pack '{pack.Id}': automationPatches targets automation '{patch.Automation}', " +
                        "which is in neither the selection nor the workspace.");
                    continue;
                }
                ApplyPatch(pack.Id, patch, target, errors);
            }
        }
        if (errors.Count > 0) throw new PackValidationException(errors);

        foreach (var pack in composition.Packs)
        {
            if (pack.Automations.Count == 0) continue;
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var automation in pack.Automations.OrderBy(a => a.Id, StringComparer.Ordinal))
                hashes[automation.Id] = PackFileHash.OfAutomation(byId[automation.Id]);
            mergeHashes[pack.Id][PackComposer.AutomationsFile] = hashes;
        }

        plan.Add((agentsRelative + "/" + PackComposer.AutomationsFile,
            JsonSerializer.SerializeToUtf8Bytes(config, AutomationStore.JsonOptions)));
    }

    /// <summary>
    /// v1's two ops are both set additions. That is exactly what makes uninstall reversible as a
    /// set subtraction (D4), and it is why this method can be so small.
    /// </summary>
    private static void ApplyPatch(string packId, PackAutomationPatch patch, AutomationRule target, List<string> errors)
    {
        if (patch.Op == PackAutomationPatch.OpAddAssignees)
        {
            var conditions = target.Conditions.OfType<AssignedToConditionSpec>().ToList();
            if (conditions.Count != 1)
            {
                errors.Add(
                    $"pack '{packId}': automationPatches addAssignees needs automation " +
                    $"'{patch.Automation}' to have exactly one assignedTo condition; it has {conditions.Count}.");
                return;
            }
            foreach (var slug in patch.Slugs)
            {
                if (!conditions[0].Slugs.Contains(slug, StringComparer.Ordinal)) conditions[0].Slugs.Add(slug);
            }
            return;
        }

        var labelConditions = target.Conditions.OfType<LabelsConditionSpec>().ToList();
        if (labelConditions.Count != 1)
        {
            errors.Add(
                $"pack '{packId}': automationPatches addLabels needs automation " +
                $"'{patch.Automation}' to have exactly one labels condition; it has {labelConditions.Count}.");
            return;
        }
        foreach (var label in patch.Labels)
        {
            if (!labelConditions[0].Labels.Contains(label, StringComparer.Ordinal)) labelConditions[0].Labels.Add(label);
        }
    }

    private static PackLockFile BuildLock(
        string installId,
        int runtimeVersion,
        PackComposition composition,
        PackLockFile? previous,
        Dictionary<string, Dictionary<string, string>> fileHashes,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> mergeHashes)
    {
        var installed = composition.Packs.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var entries = (previous?.Packs ?? Array.Empty<PackLockEntry>())
            .Where(p => !installed.Contains(p.Id))
            .ToList();

        foreach (var pack in composition.Packs)
        {
            entries.Add(new PackLockEntry(
                pack.Id,
                pack.Manifest.Version,
                pack.Manifest.Kind,
                pack.Manifest.Removable,
                pack.Manifest.RequiresRuntime,
                pack.Manifest.DependsOn,
                pack.Manifest.Provides.Agents.Order(StringComparer.Ordinal).ToList(),
                pack.Automations.Select(a => a.Id).Order(StringComparer.Ordinal).ToList(),
                pack.Teams.Keys.Order(StringComparer.Ordinal).ToList(),
                pack.ContractAgents.Keys.Order(StringComparer.Ordinal).ToList(),
                pack.Models.Keys.Order(StringComparer.Ordinal).ToList(),
                pack.Manifest.AutomationPatches,
                fileHashes[pack.Id],
                mergeHashes[pack.Id].ToDictionary(
                    e => e.Key,
                    e => (IReadOnlyDictionary<string, string>)e.Value,
                    StringComparer.Ordinal)));
        }

        return new PackLockFile(
            PackRuntime.LockSchemaVersion, installId, DateTimeOffset.UtcNow, runtimeVersion, entries);
    }

    // -------------------------------------------------------------------------------- shared

    public static PackLockFile? ReadLock(string lockPath) =>
        File.Exists(lockPath) ? PackLockSerializer.Parse(File.ReadAllText(lockPath)) : null;

    public static PackLockFile? ReadWorkspaceLock(string workspacePath) =>
        ReadLock(Path.Combine(workspacePath, ".agents", PackLockFile.FileName));

    /// <summary>An interrupted install leaves a stale staging directory and an unchanged lockfile;
    /// the next install sweeps it and re-runs (§4 step 4).</summary>
    private static void SweepStagingDirectories(string workspace)
    {
        if (!Directory.Exists(workspace)) return;
        foreach (var dir in Directory.EnumerateDirectories(workspace, StagingPrefix + "*"))
            TryDeleteDirectory(dir);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort — a leftover staging dir is swept by the next install */ }
    }

    internal static JsonObject? LoadObject(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject : null;

    internal static JsonArray? LoadArray(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path)) as JsonArray : null;

    internal static AutomationConfig LoadAutomations(string path)
    {
        if (!File.Exists(path)) return new AutomationConfig();
        return JsonSerializer.Deserialize<AutomationConfig>(File.ReadAllBytes(path), AutomationStore.JsonOptions)
            ?? new AutomationConfig();
    }

    internal static byte[] Serialize(JsonNode node) =>
        Encoding.UTF8.GetBytes(node.ToJsonString(JsonWrite));
}

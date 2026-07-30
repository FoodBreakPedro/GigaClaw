using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Packs;

/// <summary>
/// Uninstall, per doc/pack-infrastructure.md §4. Every step is written around one idea: the pack
/// owns what it installed and nothing else, so anything the owner has since touched is left alone
/// and reported rather than deleted.
/// </summary>
public sealed partial class PackInstaller
{
    public async Task<PackUninstallResult> UninstallAsync(
        string workspacePath, string packId, CancellationToken ct = default)
    {
        var workspace = Path.GetFullPath(workspacePath);
        var agentsRelative = ".agents";
        var agentsDir = Path.Combine(workspace, agentsRelative);
        var lockRelative = agentsRelative + "/" + PackLockFile.FileName;

        var lockFile = ReadLock(Path.Combine(workspace, lockRelative))
            ?? throw new PackValidationException(
                $"workspace has no .agents/{PackLockFile.FileName}; nothing is installed.");

        var entry = lockFile.Find(packId)
            ?? throw new PackValidationException($"pack '{packId}' is not installed in this workspace.");

        // Step 1 — refuse if not removable, or if anything still depends on it.
        if (!entry.Removable)
        {
            throw new PackValidationException(
                $"pack '{packId}' declares removable:false; uninstall refuses it.");
        }
        var dependents = lockFile.Packs
            .Where(p => !string.Equals(p.Id, packId, StringComparison.Ordinal)
                        && p.DependsOn.Any(d => string.Equals(d.Id, packId, StringComparison.Ordinal)))
            .Select(p => p.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (dependents.Count > 0)
        {
            throw new PackValidationException(
                $"pack '{packId}' cannot be uninstalled: {string.Join(", ", dependents)} still depend on it.");
        }

        var deleted = new List<string>();
        var orphanedFiles = new List<string>();
        var orphanedMergeEntries = new List<string>();
        var removedAutomations = new List<string>();
        var disabledAutomations = new List<string>();
        var removedContracts = new List<string>();
        var removedModels = new List<string>();
        var removedTeams = new List<string>();

        var transaction = new WorkspaceMergeTransaction(workspace);
        var remaining = new PackLockFile(
            lockFile.SchemaVersion,
            lockFile.InstallId,
            lockFile.InstalledAtUtc,
            lockFile.PackRuntimeVersion,
            lockFile.Packs.Where(p => !string.Equals(p.Id, packId, StringComparison.Ordinal)).ToList());

        try
        {
            // Step 2 — hash every recorded file. Matches → pack-owned and untouched → delete.
            // Differs → the owner edited it → leave it and report it. Never silently delete owner work.
            foreach (var (relative, hash) in entry.FileHashes.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var full = transaction.FullPath(relative);
                if (!File.Exists(full)) continue;
                if (PackFileHash.OfBytes(await File.ReadAllBytesAsync(full, ct)) != hash)
                {
                    orphanedFiles.Add(relative);
                    continue;
                }
                transaction.Delete(relative);
                deleted.Add(relative);
            }

            // Step 3 — merge artifacts: remove this pack's keys, but only entries still
            // byte-identical to what was installed.
            await RemoveContractsAsync(
                transaction, agentsDir, agentsRelative, entry, removedContracts, orphanedMergeEntries, ct);
            await RemoveModelsAsync(
                transaction, agentsDir, agentsRelative, entry, removedModels, orphanedMergeEntries, ct);
            await RemoveTeamsAsync(
                transaction, agentsDir, agentsRelative, entry, removedTeams, orphanedMergeEntries, ct);

            // Step 4 — pack automations, plus the set-subtraction that reverses automationPatches.
            await RemoveAutomationsAsync(
                transaction, agentsDir, agentsRelative, entry,
                removedAutomations, disabledAutomations, ct);

            await transaction.WriteAsync(
                lockRelative, Encoding.UTF8.GetBytes(PackLockSerializer.ToJson(remaining)), ct);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        PruneEmptyDirectories(agentsDir, deleted.Select(transaction.FullPath));

        return new PackUninstallResult(
            packId,
            deleted,
            orphanedFiles,
            removedAutomations,
            disabledAutomations,
            removedContracts,
            removedModels,
            removedTeams,
            orphanedMergeEntries,
            // Step 5 — Member rows are never deleted here. They carry DefaultModel and are
            // referenced by run history and by assignedTo on historical tickets; the UI marks them
            // orphaned, and MemberService.DeleteMemberAsync already exists for the owner to remove
            // them deliberately.
            entry.Agents,
            remaining);
    }

    private static async Task RemoveContractsAsync(
        WorkspaceMergeTransaction transaction,
        string agentsDir,
        string agentsRelative,
        PackLockEntry entry,
        List<string> removed,
        List<string> orphaned,
        CancellationToken ct)
    {
        if (entry.ContractKeys.Count == 0) return;
        var path = Path.Combine(agentsDir, PackComposer.ContractsFile);
        if (LoadObject(path) is not { } root || root["agents"] is not JsonObject agents) return;

        var installed = Hashes(entry, PackComposer.ContractsFile);
        var changed = false;
        foreach (var key in entry.ContractKeys.Order(StringComparer.Ordinal))
        {
            if (!agents.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (!installed.TryGetValue(key, out var hash) || PackFileHash.OfNode(node) != hash)
            {
                orphaned.Add($"{PackComposer.ContractsFile}#{key}");
                continue;
            }
            agents.Remove(key);
            removed.Add(key);
            changed = true;
        }
        if (changed)
            await transaction.WriteAsync(agentsRelative + "/" + PackComposer.ContractsFile, Serialize(root), ct);
    }

    private static async Task RemoveModelsAsync(
        WorkspaceMergeTransaction transaction,
        string agentsDir,
        string agentsRelative,
        PackLockEntry entry,
        List<string> removed,
        List<string> orphaned,
        CancellationToken ct)
    {
        if (entry.ModelKeys.Count == 0) return;
        var path = Path.Combine(agentsDir, PackComposer.ModelsFile);
        if (LoadObject(path) is not { } root) return;

        var installed = Hashes(entry, PackComposer.ModelsFile);
        var changed = false;
        foreach (var key in entry.ModelKeys.Order(StringComparer.Ordinal))
        {
            if (!root.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (!installed.TryGetValue(key, out var hash) || PackFileHash.OfNode(node) != hash)
            {
                orphaned.Add($"{PackComposer.ModelsFile}#{key}");
                continue;
            }
            root.Remove(key);
            removed.Add(key);
            changed = true;
        }
        if (changed)
            await transaction.WriteAsync(agentsRelative + "/" + PackComposer.ModelsFile, Serialize(root), ct);
    }

    private static async Task RemoveTeamsAsync(
        WorkspaceMergeTransaction transaction,
        string agentsDir,
        string agentsRelative,
        PackLockEntry entry,
        List<string> removed,
        List<string> orphaned,
        CancellationToken ct)
    {
        var path = Path.Combine(agentsDir, PackComposer.TeamsFile);
        if (LoadArray(path) is not { } array) return;

        var installed = Hashes(entry, PackComposer.TeamsFile);
        var teams = array.OfType<JsonObject>().Select(t => (JsonObject)t.DeepClone()).ToList();
        var changed = false;

        foreach (var slug in entry.Teams.Order(StringComparer.Ordinal))
        {
            var team = teams.FirstOrDefault(t => t["slug"]?.GetValue<string>() == slug);
            if (team is null) continue;
            if (!installed.TryGetValue(slug, out var hash) || PackFileHash.OfNode(team) != hash)
            {
                orphaned.Add($"{PackComposer.TeamsFile}#{slug}");
                continue;
            }
            teams.Remove(team);
            removed.Add(slug);
            changed = true;
        }

        // Reverses `teamMembership` as a set subtraction: the pack's agents are gone, so a
        // surviving team must not keep naming them. Only the pack's own slugs are removed —
        // everything else in the team, including anything the owner added, is left alone.
        var packAgents = entry.Agents.ToHashSet(StringComparer.Ordinal);
        foreach (var team in teams)
        {
            if (team["agentSlugs"] is not JsonArray members) continue;
            var kept = members
                .Select(m => m?.GetValue<string>())
                .Where(m => m is not null && !packAgents.Contains(m))
                .Select(m => (JsonNode)JsonValue.Create(m!)!)
                .ToArray();
            if (kept.Length == members.Count) continue;
            team["agentSlugs"] = new JsonArray(kept);
            changed = true;
        }

        if (changed)
        {
            await transaction.WriteAsync(
                agentsRelative + "/" + PackComposer.TeamsFile,
                Serialize(new JsonArray(teams.Select(t => (JsonNode)t).ToArray())),
                ct);
        }
    }

    private static async Task RemoveAutomationsAsync(
        WorkspaceMergeTransaction transaction,
        string agentsDir,
        string agentsRelative,
        PackLockEntry entry,
        List<string> removed,
        List<string> disabled,
        CancellationToken ct)
    {
        var path = Path.Combine(agentsDir, PackComposer.AutomationsFile);
        if (!File.Exists(path)) return;

        var config = LoadAutomations(path);
        var installed = Hashes(entry, PackComposer.AutomationsFile);
        var changed = false;

        foreach (var id in entry.Automations.Order(StringComparer.Ordinal))
        {
            var automation = config.Automations.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (automation is null) continue;

            if (!installed.TryGetValue(id, out var hash) || PackFileHash.OfAutomation(automation) != hash)
            {
                // An edited automation is owner work. Disabled and reported, never deleted — and
                // it must be disabled, because a dangling automation referencing a removed agent
                // would fire and fail.
                if (automation.Enabled)
                {
                    automation.Enabled = false;
                    changed = true;
                }
                disabled.Add(id);
                continue;
            }

            config.Automations.Remove(automation);
            removed.Add(id);
            changed = true;
        }

        // Reverse the automationPatches set-additions, removing only entries still present.
        foreach (var patch in entry.AutomationPatches)
        {
            var target = config.Automations.FirstOrDefault(
                a => string.Equals(a.Id, patch.Automation, StringComparison.Ordinal));
            if (target is null) continue;

            if (patch.Op == PackAutomationPatch.OpAddAssignees)
            {
                foreach (var condition in target.Conditions.OfType<AssignedToConditionSpec>())
                {
                    foreach (var slug in patch.Slugs)
                    {
                        if (condition.Slugs.Remove(slug)) changed = true;
                    }
                }
            }
            else if (patch.Op == PackAutomationPatch.OpAddLabels)
            {
                foreach (var condition in target.Conditions.OfType<LabelsConditionSpec>())
                {
                    foreach (var label in patch.Labels)
                    {
                        if (condition.Labels.Remove(label)) changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            await transaction.WriteAsync(
                agentsRelative + "/" + PackComposer.AutomationsFile,
                JsonSerializer.SerializeToUtf8Bytes(config, AutomationStore.JsonOptions),
                ct);
        }
    }

    private static IReadOnlyDictionary<string, string> Hashes(PackLockEntry entry, string file) =>
        entry.MergeEntryHashes.TryGetValue(file, out var map)
            ? map
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Removes directories emptied by the deletions above, bottom-up, stopping at
    /// <c>.agents/</c> itself. A directory that still holds anything — a runtime-written memory
    /// topic file, <c>evaluator/memory/scores.json</c>, an owner-edited SKILL — is left standing.
    /// </summary>
    private static void PruneEmptyDirectories(string agentsDir, IEnumerable<string> deletedFullPaths)
    {
        var root = Path.GetFullPath(agentsDir);
        foreach (var file in deletedFullPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            while (!string.IsNullOrEmpty(dir)
                   && dir.StartsWith(root, StringComparison.Ordinal)
                   && !string.Equals(dir, root, StringComparison.Ordinal))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir);
                }
                catch { break; }
                dir = Path.GetDirectoryName(dir);
            }
        }
    }
}

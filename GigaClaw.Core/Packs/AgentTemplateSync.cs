using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Packs;

public enum AgentTemplateSyncChangeKind
{
    Add,
    Update,
    Remove,
    Conflict,
    DeletedByOwner,
    SkippedMemory,
    ManualReviewRequired,
}

public sealed record AgentTemplateSyncChange(
    string RelativePath,
    AgentTemplateSyncChangeKind Kind,
    string Detail);

public sealed record AgentTemplateSyncPlan(
    string TemplateVersion,
    string PlanToken,
    bool CanApply,
    IReadOnlyList<AgentTemplateSyncChange> Changes)
{
    public int Additions => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.Add);
    public int Updates => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.Update);
    public int Removals => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.Remove);
    public int Conflicts => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.Conflict);
    public int DeletedByOwner => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.DeletedByOwner);
    public int SkippedMemory => Changes.Count(change => change.Kind == AgentTemplateSyncChangeKind.SkippedMemory);
    public bool HasApplicableChanges => Additions + Updates + Removals > 0;
}

public sealed record AgentTemplateSyncResult(
    AgentTemplateSyncPlan Plan,
    IReadOnlyList<string> AppliedPaths);

public sealed class AgentTemplateSyncPlanChangedException : Exception
{
    public AgentTemplateSyncPlanChangedException()
        : base("The agent-template sync preview is stale. Preview again before applying.") { }
}

/// <summary>
/// Synchronizes the embedded core pack against a workspace using the previous lock hashes as the
/// ownership baseline. Unlike Initialize, this operation never overwrites an owner edit or
/// recreates an owner-deleted managed file.
/// </summary>
public sealed class AgentTemplateSyncService
{
    private readonly IPackSource _coreSource;

    public AgentTemplateSyncService(IPackSource? coreSource = null) =>
        _coreSource = coreSource ?? CorePack.Source();

    public Task<AgentTemplateSyncPlan> PreviewAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Build(workspacePath).PublicPlan);
    }

    public async Task<AgentTemplateSyncResult> ApplyAsync(
        string workspacePath,
        string expectedPlanToken,
        CancellationToken ct = default)
    {
        var built = Build(workspacePath);
        var actualToken = Encoding.UTF8.GetBytes(built.PublicPlan.PlanToken);
        var suppliedToken = Encoding.UTF8.GetBytes(expectedPlanToken ?? string.Empty);
        if (actualToken.Length != suppliedToken.Length ||
            !CryptographicOperations.FixedTimeEquals(actualToken, suppliedToken))
        {
            throw new AgentTemplateSyncPlanChangedException();
        }

        if (!built.PublicPlan.CanApply ||
            (built.Writes.Count == 0 && built.Deletes.Count == 0 && built.LockBytes is null))
        {
            return new AgentTemplateSyncResult(built.PublicPlan, []);
        }

        var workspace = Path.GetFullPath(workspacePath);
        EnsureSafeDestination(workspace, ".agents/" + PackLockFile.FileName);
        foreach (var path in built.Writes.Keys) EnsureSafeDestination(workspace, path);
        foreach (var path in built.Deletes) EnsureSafeDestination(workspace, path);

        var staging = Path.Combine(workspace, PackInstaller.StagingPrefix + "sync-" + Guid.NewGuid().ToString("d"));
        try
        {
            foreach (var (relative, content) in built.Writes)
            {
                var staged = Path.Combine(staging, ToNative(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, content, ct);
            }

            var transaction = new WorkspaceMergeTransaction(workspace);
            try
            {
                foreach (var relative in built.Deletes) transaction.Delete(relative);
                foreach (var relative in built.Writes.Keys.Order(StringComparer.Ordinal))
                {
                    var staged = Path.Combine(staging, ToNative(relative));
                    await transaction.WriteAsync(relative, await File.ReadAllBytesAsync(staged, ct), ct);
                }

                if (built.LockBytes is not null)
                {
                    await transaction.WriteAsync(
                        ".agents/" + PackLockFile.FileName,
                        built.LockBytes,
                        ct);
                }
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            var applied = built.Deletes.Concat(built.Writes.Keys)
                .Concat(built.LockBytes is null ? [] : [".agents/" + PackLockFile.FileName])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return new AgentTemplateSyncResult(built.PublicPlan, applied);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }

    private BuiltPlan Build(string workspacePath)
    {
        var workspace = Path.GetFullPath(workspacePath);
        var composition = PackComposer.Compose([_coreSource]);
        var core = composition.Find(CorePack.Id)
            ?? throw new PackValidationException("The selected source does not contain the core pack.");
        var changes = new List<AgentTemplateSyncChange>();
        var fingerprints = new SortedDictionary<string, string>(StringComparer.Ordinal);

        PackLockFile? previous;
        try
        {
            var lockPath = Path.Combine(workspace, ".agents", PackLockFile.FileName);
            fingerprints["lock"] = Fingerprint(lockPath);
            previous = PackInstaller.ReadLock(lockPath);
        }
        catch (PackValidationException ex)
        {
            return ManualReview(core.Manifest.Version, changes, fingerprints, ex.Message);
        }

        var previousCore = previous?.Find(CorePack.Id);
        if (previous is null || previousCore is null)
        {
            return ManualReview(
                core.Manifest.Version,
                changes,
                fingerprints,
                "No trustworthy core lock baseline exists; use explicit initialization or adopt the workspace manually.");
        }

        var writes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var deletes = new SortedSet<string>(StringComparer.Ordinal);
        var nextFileHashes = new Dictionary<string, string>(previousCore.FileHashes, StringComparer.Ordinal);
        PlanOpaque(workspace, core, previousCore, writes, deletes, changes, fingerprints, nextFileHashes);

        var nextMergeHashes = previousCore.MergeEntryHashes.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

        PlanContracts(workspace, core, previousCore, writes, changes, fingerprints, nextMergeHashes);
        PlanModels(workspace, core, previousCore, writes, changes, fingerprints, nextMergeHashes);
        PlanTeams(workspace, core, previous!, previousCore, writes, changes, fingerprints, nextMergeHashes);
        PlanAutomations(workspace, core, previous!, previousCore, writes, changes, fingerprints, nextMergeHashes);

        var nextCore = new PackLockEntry(
            core.Id,
            core.Manifest.Version,
            core.Manifest.Kind,
            core.Manifest.Removable,
            core.Manifest.RequiresRuntime,
            core.Manifest.DependsOn,
            core.Manifest.Provides.Agents.Order(StringComparer.Ordinal).ToArray(),
            core.Automations.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            core.Teams.Keys.Order(StringComparer.Ordinal).ToArray(),
            core.ContractAgents.Keys.Order(StringComparer.Ordinal).ToArray(),
            core.Models.Keys.Order(StringComparer.Ordinal).ToArray(),
            core.Manifest.AutomationPatches,
            nextFileHashes,
            nextMergeHashes.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.Ordinal));

        var entries = previous!.Packs.Where(pack => pack.Id != CorePack.Id).Append(nextCore).ToArray();
        var nextLock = new PackLockFile(
            PackRuntime.LockSchemaVersion,
            Guid.NewGuid().ToString("d"),
            DateTimeOffset.UtcNow,
            PackRuntime.Version,
            entries);

        var lockChanged = !Equivalent(previousCore, nextCore) || writes.Count > 0 || deletes.Count > 0;
        var lockBytes = lockChanged
            ? Encoding.UTF8.GetBytes(PackLockSerializer.ToJson(nextLock))
            : null;
        var token = ComputeToken(core.Manifest.Version, changes, fingerprints, writes, deletes);
        var plan = new AgentTemplateSyncPlan(core.Manifest.Version, token, true, Sort(changes));
        return new BuiltPlan(plan, writes, deletes, lockBytes);
    }

    private static void PlanOpaque(
        string workspace,
        ComposedPack core,
        PackLockEntry previous,
        IDictionary<string, byte[]> writes,
        ISet<string> deletes,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        IDictionary<string, string> nextHashes)
    {
        var agentFiles = core.Files
            .Where(file => IsAgentPath(file.DestinationPath))
            .ToArray();
        var intendedPaths = agentFiles.Select(file => file.DestinationPath).ToHashSet(StringComparer.Ordinal);
        foreach (var file in agentFiles.OrderBy(file => file.DestinationPath, StringComparer.Ordinal))
        {
            var relative = file.DestinationPath;
            if (IsMemoryPath(relative))
            {
                changes.Add(new(relative, AgentTemplateSyncChangeKind.SkippedMemory, "Runtime memory is never synchronized."));
                continue;
            }

            var full = Path.Combine(workspace, ToNative(relative));
            fingerprints[relative] = Fingerprint(full);
            if (IsSymbolicLink(full))
            {
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
                continue;
            }

            var intendedHash = PackFileHash.OfBytes(file.Content);
            var hadPrevious = previous.FileHashes.TryGetValue(relative, out var previousHash);
            if (!File.Exists(full))
            {
                if (hadPrevious)
                {
                    changes.Add(new(relative, AgentTemplateSyncChangeKind.DeletedByOwner, "Managed file was deleted locally and will not be recreated."));
                    continue;
                }

                writes[relative] = file.Content;
                nextHashes[relative] = intendedHash;
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Add, "New core-managed file."));
                continue;
            }

            var currentHash = PackFileHash.OfBytes(File.ReadAllBytes(full));
            if (currentHash == intendedHash)
            {
                nextHashes[relative] = intendedHash;
                continue;
            }
            if (hadPrevious && currentHash == previousHash)
            {
                writes[relative] = file.Content;
                nextHashes[relative] = intendedHash;
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Update, "Unchanged since the previous core install."));
                continue;
            }

            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Owner-modified file preserved."));
        }

        foreach (var (relative, previousHash) in previous.FileHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsAgentPath(relative)) continue;
            if (intendedPaths.Contains(relative)) continue;
            if (IsMemoryPath(relative))
            {
                changes.Add(new(relative, AgentTemplateSyncChangeKind.SkippedMemory, "Runtime memory is never synchronized."));
                continue;
            }

            var full = Path.Combine(workspace, ToNative(relative));
            fingerprints[relative] = Fingerprint(full);
            if (IsSymbolicLink(full))
            {
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
                continue;
            }

            nextHashes.Remove(relative);
            if (!File.Exists(full)) continue;
            if (PackFileHash.OfBytes(File.ReadAllBytes(full)) == previousHash)
            {
                deletes.Add(relative);
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Remove, "No longer shipped by core and still unmodified."));
            }
            else
            {
                changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Owner-modified retired file preserved."));
            }
        }
    }

    private static void PlanContracts(
        string workspace,
        ComposedPack core,
        PackLockEntry previous,
        IDictionary<string, byte[]> writes,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        IDictionary<string, Dictionary<string, string>> nextHashes)
    {
        const string file = PackComposer.ContractsFile;
        var relative = ".agents/" + file;
        var intended = core.ContractAgents.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DeepClone(),
            StringComparer.Ordinal);
        var path = Path.Combine(workspace, ToNative(relative));
        fingerprints[relative] = Fingerprint(path);
        if (IsSymbolicLink(path))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
            return;
        }
        var prior = PreviousHashes(previous, file);
        if (!TryLoadObjectForMerge(path, relative, prior, changes, out var root)) return;
        if (root["agents"] is not JsonObject agents)
        {
            agents = new JsonObject();
            root["agents"] = agents;
        }
        var changed = false;
        string? defaultsHash = null;
        if (core.ContractDefaults is not null)
        {
            changed |= MergeMetadataNode(
                relative,
                "defaults",
                root,
                "defaults",
                core.ContractDefaults,
                prior,
                changes,
                out defaultsHash);
        }

        var agentPrior = prior.Where(pair => !IsMetadataKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changed |= MergeNodes(relative, agents, intended, agentPrior, changes, out var hashes);
        if (defaultsHash is not null) hashes[MetadataKey("defaults")] = defaultsHash;
        nextHashes[file] = hashes;
        if (changed) writes[relative] = Serialize(root);
    }

    private static void PlanModels(
        string workspace,
        ComposedPack core,
        PackLockEntry previous,
        IDictionary<string, byte[]> writes,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        IDictionary<string, Dictionary<string, string>> nextHashes)
    {
        const string file = PackComposer.ModelsFile;
        var relative = ".agents/" + file;
        var path = Path.Combine(workspace, ToNative(relative));
        fingerprints[relative] = Fingerprint(path);
        if (IsSymbolicLink(path))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
            return;
        }
        var prior = PreviousHashes(previous, file);
        if (!TryLoadObjectForMerge(path, relative, prior, changes, out var root)) return;
        var changed = false;
        var metadataHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in core.ModelsPreamble.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            changed |= MergeMetadataNode(
                relative,
                key,
                root,
                key,
                value,
                prior,
                changes,
                out var metadataHash);
            if (metadataHash is not null) metadataHashes[MetadataKey(key)] = metadataHash;
        }
        var intended = core.Models.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DeepClone(),
            StringComparer.Ordinal);
        var modelPrior = prior.Where(pair => !IsMetadataKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changed |= MergeNodes(relative, root, intended, modelPrior, changes, out var hashes);
        foreach (var pair in metadataHashes) hashes[pair.Key] = pair.Value;
        nextHashes[file] = hashes;
        if (changed) writes[relative] = Serialize(root);
    }

    private static void PlanTeams(
        string workspace,
        ComposedPack core,
        PackLockFile lockFile,
        PackLockEntry previous,
        IDictionary<string, byte[]> writes,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        IDictionary<string, Dictionary<string, string>> nextHashes)
    {
        const string file = PackComposer.TeamsFile;
        var relative = ".agents/" + file;
        var path = Path.Combine(workspace, ToNative(relative));
        fingerprints[relative] = Fingerprint(path);
        if (IsSymbolicLink(path))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
            return;
        }
        var prior = PreviousHashes(previous, file);
        if (!TryLoadNodeForMerge(path, relative, prior, changes, out var document)) return;
        var existing = PackComposer.TeamsArrayOf(document) ?? new JsonArray();
        var teamItems = existing.OfType<JsonObject>().ToList();
        if (teamItems.Count != existing.Count || teamItems.Any(team => team["slug"] is null) ||
            teamItems.GroupBy(team => team["slug"]!.GetValue<string>(), StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Duplicate or invalid team entries preserved for manual review."));
            return;
        }
        var workspaceTeams = teamItems.ToDictionary(
            team => team["slug"]!.GetValue<string>(), team => team, StringComparer.Ordinal);
        var specialistAgents = lockFile.Packs.Where(pack => pack.Kind != PackKind.Core)
            .SelectMany(pack => pack.Agents)
            .ToHashSet(StringComparer.Ordinal);
        var intended = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var slug in core.TeamOrder)
        {
            var team = (JsonObject)core.Teams[slug].DeepClone();
            if (workspaceTeams.TryGetValue(slug, out var current) && current["agentSlugs"] is JsonArray currentMembers)
            {
                var target = team["agentSlugs"] as JsonArray ?? new JsonArray();
                team["agentSlugs"] = target;
                var present = target.Select(node => node?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                foreach (var member in currentMembers.Select(node => node?.GetValue<string>()).Where(member => member is not null))
                {
                    if (specialistAgents.Contains(member!) && present.Add(member)) target.Add(member);
                }
            }
            intended[slug] = team;
        }

        var holder = new JsonObject();
        foreach (var team in workspaceTeams) holder[team.Key] = team.Value.DeepClone();
        var changed = MergeNodes(relative, holder, intended, prior, changes, out var hashes);
        nextHashes[file] = hashes;
        if (!changed) return;

        var ordered = new JsonArray();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in existing.OfType<JsonObject>())
        {
            var slug = item["slug"]?.GetValue<string>();
            if (slug is null || holder[slug] is not JsonObject replacement) continue;
            ordered.Add(replacement.DeepClone());
            emitted.Add(slug);
        }
        foreach (var slug in core.TeamOrder.Where(slug => !emitted.Contains(slug)))
        {
            if (holder[slug] is JsonObject team) ordered.Add(team.DeepClone());
        }
        writes[relative] = Serialize(WriteTeams(document, ordered));
    }

    private static void PlanAutomations(
        string workspace,
        ComposedPack core,
        PackLockFile lockFile,
        PackLockEntry previous,
        IDictionary<string, byte[]> writes,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        IDictionary<string, Dictionary<string, string>> nextHashes)
    {
        const string file = PackComposer.AutomationsFile;
        var relative = ".agents/" + file;
        var path = Path.Combine(workspace, ToNative(relative));
        fingerprints[relative] = Fingerprint(path);
        if (IsSymbolicLink(path))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Symbolic-link destination preserved for manual review."));
            return;
        }
        var prior = PreviousHashes(previous, file);
        if (!File.Exists(path))
        {
            ReportMissingMergeFile(relative, prior, changes);
            return;
        }

        AutomationConfig config;
        try { config = PackInstaller.LoadAutomations(path); }
        catch (JsonException)
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Invalid JSON preserved for manual review."));
            return;
        }

        if (config.Automations.Any(item => string.IsNullOrWhiteSpace(item.Id)) ||
            config.Automations.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Duplicate or invalid automation entries preserved for manual review."));
            return;
        }
        var current = config.Automations.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var intended = core.Automations.ToDictionary(
            item => item.Id,
            item => CloneAutomation(item),
            StringComparer.Ordinal);
        foreach (var patch in lockFile.Packs.Where(pack => pack.Kind != PackKind.Core).SelectMany(pack => pack.AutomationPatches))
        {
            if (intended.TryGetValue(patch.Automation, out var target)) ApplyRecordedPatch(target, patch);
        }

        var hashes = new Dictionary<string, string>(prior, StringComparer.Ordinal);
        var changed = false;
        foreach (var id in intended.Keys.Order(StringComparer.Ordinal))
        {
            var target = intended[id];
            var intendedHash = PackFileHash.OfAutomation(target);
            var hadPrevious = prior.TryGetValue(id, out var previousHash);
            if (!current.TryGetValue(id, out var value))
            {
                if (hadPrevious)
                {
                    changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.DeletedByOwner, "Managed automation was deleted locally and will not be recreated."));
                    continue;
                }
                config.Automations.Add(target);
                current[id] = target;
                hashes[id] = intendedHash;
                changed = true;
                changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.Add, "New core automation."));
                continue;
            }

            var currentHash = PackFileHash.OfAutomation(value);
            if (currentHash == intendedHash)
            {
                hashes[id] = intendedHash;
                continue;
            }
            if (hadPrevious && currentHash == previousHash)
            {
                config.Automations[config.Automations.IndexOf(value)] = target;
                current[id] = target;
                hashes[id] = intendedHash;
                changed = true;
                changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.Update, "Unchanged since the previous core install."));
                continue;
            }
            changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.Conflict, "Owner-modified automation preserved."));
        }

        foreach (var (id, previousHash) in prior.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (intended.ContainsKey(id)) continue;
            hashes.Remove(id);
            if (!current.TryGetValue(id, out var value)) continue;
            if (PackFileHash.OfAutomation(value) == previousHash)
            {
                config.Automations.Remove(value);
                changed = true;
                changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.Remove, "Retired core automation remained unmodified."));
            }
            else
            {
                changes.Add(new(relative + "#" + id, AgentTemplateSyncChangeKind.Conflict, "Owner-modified retired automation preserved."));
            }
        }

        nextHashes[file] = hashes;
        if (changed) writes[relative] = JsonSerializer.SerializeToUtf8Bytes(config, AutomationStore.JsonOptions);
    }

    private static bool MergeNodes(
        string displayPath,
        JsonObject current,
        IReadOnlyDictionary<string, JsonNode> intended,
        IReadOnlyDictionary<string, string> prior,
        ICollection<AgentTemplateSyncChange> changes,
        out Dictionary<string, string> nextHashes)
    {
        var changed = false;
        nextHashes = new Dictionary<string, string>(prior, StringComparer.Ordinal);
        foreach (var key in intended.Keys.Order(StringComparer.Ordinal))
        {
            var target = intended[key];
            var intendedHash = PackFileHash.OfNode(target);
            var hadPrevious = prior.TryGetValue(key, out var previousHash);
            if (current[key] is not JsonNode value)
            {
                if (hadPrevious)
                {
                    changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.DeletedByOwner, "Managed entry was deleted locally and will not be recreated."));
                    continue;
                }
                current[key] = target.DeepClone();
                nextHashes[key] = intendedHash;
                changed = true;
                changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.Add, "New core-managed entry."));
                continue;
            }

            var currentHash = PackFileHash.OfNode(value);
            if (currentHash == intendedHash)
            {
                nextHashes[key] = intendedHash;
                continue;
            }
            if (hadPrevious && currentHash == previousHash)
            {
                current[key] = target.DeepClone();
                nextHashes[key] = intendedHash;
                changed = true;
                changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.Update, "Unchanged since the previous core install."));
                continue;
            }
            changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.Conflict, "Owner-modified entry preserved."));
        }

        foreach (var (key, previousHash) in prior.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (intended.ContainsKey(key)) continue;
            nextHashes.Remove(key);
            if (current[key] is not JsonNode value) continue;
            if (PackFileHash.OfNode(value) == previousHash)
            {
                current.Remove(key);
                changed = true;
                changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.Remove, "Retired core entry remained unmodified."));
            }
            else
            {
                changes.Add(new(displayPath + "#" + key, AgentTemplateSyncChangeKind.Conflict, "Owner-modified retired entry preserved."));
            }
        }
        return changed;
    }

    private static bool MergeMetadataNode(
        string displayPath,
        string label,
        JsonObject container,
        string jsonKey,
        JsonNode intended,
        IReadOnlyDictionary<string, string> prior,
        ICollection<AgentTemplateSyncChange> changes,
        out string? nextHash)
    {
        var ownershipKey = MetadataKey(label);
        var hadPrevious = prior.TryGetValue(ownershipKey, out var previousHash);
        var targetHash = PackFileHash.OfNode(intended);
        if (container[jsonKey] is not JsonNode current)
        {
            nextHash = hadPrevious ? previousHash : null;
            changes.Add(new(
                displayPath + "#" + label,
                hadPrevious ? AgentTemplateSyncChangeKind.DeletedByOwner : AgentTemplateSyncChangeKind.Conflict,
                hadPrevious
                    ? "Managed metadata was deleted locally and will not be recreated."
                    : "Metadata has no previous ownership baseline and was preserved for manual review."));
            return false;
        }

        var currentHash = PackFileHash.OfNode(current);
        if (currentHash == targetHash)
        {
            nextHash = targetHash;
            return false;
        }
        if (hadPrevious && currentHash == previousHash)
        {
            container[jsonKey] = intended.DeepClone();
            nextHash = targetHash;
            changes.Add(new(displayPath + "#" + label, AgentTemplateSyncChangeKind.Update, "Unchanged since the previous core install."));
            return true;
        }

        nextHash = hadPrevious ? previousHash : null;
        changes.Add(new(displayPath + "#" + label, AgentTemplateSyncChangeKind.Conflict, "Owner-modified metadata preserved."));
        return false;
    }

    private static bool TryLoadObjectForMerge(
        string path,
        string relative,
        IReadOnlyDictionary<string, string> prior,
        ICollection<AgentTemplateSyncChange> changes,
        out JsonObject root)
    {
        root = new JsonObject();
        if (!File.Exists(path))
        {
            if (prior.Count > 0)
            {
                ReportMissingMergeFile(relative, prior, changes);
                return false;
            }
            return true;
        }
        try
        {
            root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
                ?? throw new JsonException("Expected a JSON object.");
            return true;
        }
        catch (JsonException)
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Invalid JSON preserved for manual review."));
            return false;
        }
    }

    private static bool TryLoadNodeForMerge(
        string path,
        string relative,
        IReadOnlyDictionary<string, string> prior,
        ICollection<AgentTemplateSyncChange> changes,
        out JsonNode document)
    {
        document = new JsonObject { ["schemaVersion"] = 1, ["teams"] = new JsonArray() };
        if (!File.Exists(path))
        {
            if (prior.Count > 0)
            {
                ReportMissingMergeFile(relative, prior, changes);
                return false;
            }
            return true;
        }
        try
        {
            document = JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new JsonException("Expected JSON.");
            return true;
        }
        catch (JsonException)
        {
            changes.Add(new(relative, AgentTemplateSyncChangeKind.Conflict, "Invalid JSON preserved for manual review."));
            return false;
        }
    }

    private static void ReportMissingMergeFile(
        string relative,
        IReadOnlyDictionary<string, string> prior,
        ICollection<AgentTemplateSyncChange> changes)
    {
        changes.Add(new(
            relative,
            prior.Count > 0 ? AgentTemplateSyncChangeKind.DeletedByOwner : AgentTemplateSyncChangeKind.Add,
            prior.Count > 0
                ? "Managed merge file was deleted locally and will not be recreated."
                : "New core-managed merge file."));
    }

    private static IReadOnlyDictionary<string, string> PreviousHashes(PackLockEntry previous, string file) =>
        previous.MergeEntryHashes.TryGetValue(file, out var hashes)
            ? hashes
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static AutomationRule CloneAutomation(AutomationRule automation) =>
        JsonSerializer.Deserialize<AutomationRule>(
            JsonSerializer.SerializeToUtf8Bytes(automation, AutomationStore.JsonOptions),
            AutomationStore.JsonOptions)!;

    private static void ApplyRecordedPatch(AutomationRule target, PackAutomationPatch patch)
    {
        if (patch.Op == PackAutomationPatch.OpAddAssignees)
        {
            var condition = target.Conditions.OfType<AssignedToConditionSpec>().SingleOrDefault();
            if (condition is null) return;
            foreach (var slug in patch.Slugs)
            {
                if (!condition.Slugs.Contains(slug, StringComparer.Ordinal)) condition.Slugs.Add(slug);
            }
            return;
        }
        if (patch.Op != PackAutomationPatch.OpAddLabels) return;
        var labels = target.Conditions.OfType<LabelsConditionSpec>().SingleOrDefault();
        if (labels is null) return;
        foreach (var label in patch.Labels)
        {
            if (!labels.Labels.Contains(label, StringComparer.Ordinal)) labels.Labels.Add(label);
        }
    }

    private static JsonNode WriteTeams(JsonNode document, JsonArray teams)
    {
        if (document is not JsonObject root) return teams;
        var clone = (JsonObject)root.DeepClone();
        clone["teams"] = teams;
        return clone;
    }

    private static bool Equivalent(PackLockEntry left, PackLockEntry right)
    {
        var leftJson = PackLockSerializer.ToJson(new PackLockFile(
            PackRuntime.LockSchemaVersion, "", DateTimeOffset.UnixEpoch, PackRuntime.Version, [left]));
        var rightJson = PackLockSerializer.ToJson(new PackLockFile(
            PackRuntime.LockSchemaVersion, "", DateTimeOffset.UnixEpoch, PackRuntime.Version, [right]));
        return leftJson == rightJson;
    }

    private static BuiltPlan ManualReview(
        string version,
        ICollection<AgentTemplateSyncChange> changes,
        IDictionary<string, string> fingerprints,
        string detail)
    {
        changes.Add(new(".agents/" + PackLockFile.FileName, AgentTemplateSyncChangeKind.ManualReviewRequired, detail));
        var token = ComputeToken(version, changes, fingerprints,
            new Dictionary<string, byte[]>(), new HashSet<string>());
        return new BuiltPlan(new AgentTemplateSyncPlan(version, token, false, Sort(changes)),
            new Dictionary<string, byte[]>(), new HashSet<string>(), null);
    }

    private static string ComputeToken(
        string version,
        IEnumerable<AgentTemplateSyncChange> changes,
        IEnumerable<KeyValuePair<string, string>> fingerprints,
        IEnumerable<KeyValuePair<string, byte[]>> writes,
        IEnumerable<string> deletes)
    {
        var builder = new StringBuilder(version).Append('\n');
        foreach (var pair in fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append("state|").Append(pair.Key).Append('|').Append(pair.Value).Append('\n');
        foreach (var change in Sort(changes))
            builder.Append("change|").Append(change.Kind).Append('|').Append(change.RelativePath).Append('|').Append(change.Detail).Append('\n');
        foreach (var pair in writes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append("write|").Append(pair.Key).Append('|').Append(PackFileHash.OfBytes(pair.Value)).Append('\n');
        foreach (var path in deletes.Order(StringComparer.Ordinal)) builder.Append("delete|").Append(path).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IReadOnlyList<AgentTemplateSyncChange> Sort(IEnumerable<AgentTemplateSyncChange> changes) =>
        changes.OrderBy(change => change.RelativePath, StringComparer.Ordinal)
            .ThenBy(change => change.Kind)
            .ToArray();

    private static string Fingerprint(string path) =>
        IsSymbolicLink(path)
            ? "symlink:" + new FileInfo(path).LinkTarget
            : File.Exists(path) ? PackFileHash.OfBytes(File.ReadAllBytes(path)) : "missing";

    private static bool IsSymbolicLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static byte[] Serialize(JsonNode node) =>
        Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static string ToNative(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string MetadataKey(string label) => "@metadata:" + label;

    private static bool IsMetadataKey(string key) => key.StartsWith("@metadata:", StringComparison.Ordinal);

    private static bool IsMemoryPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == ".agents" && parts[2] == "memory";
    }

    private static bool IsAgentPath(string path) =>
        path.StartsWith(".agents/", StringComparison.Ordinal);

    private static void EnsureSafeDestination(string workspace, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(workspace, ToNative(relative)));
        var prefix = workspace.EndsWith(Path.DirectorySeparatorChar)
            ? workspace
            : workspace + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new PackValidationException($"Sync destination escapes the workspace: {relative}");

        var cursor = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(cursor) && cursor.StartsWith(prefix, StringComparison.Ordinal))
        {
            var directory = new DirectoryInfo(cursor);
            if (directory.LinkTarget is not null ||
                (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new PackValidationException($"Sync destination traverses a symbolic link: {relative}");
            cursor = Path.GetDirectoryName(cursor);
        }
        var destination = new FileInfo(full);
        if (destination.LinkTarget is not null ||
            (destination.Exists && (destination.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new PackValidationException($"Sync destination is a symbolic link: {relative}");
    }

    private sealed record BuiltPlan(
        AgentTemplateSyncPlan PublicPlan,
        IReadOnlyDictionary<string, byte[]> Writes,
        IReadOnlySet<string> Deletes,
        byte[]? LockBytes);
}

using System.Collections.Concurrent;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Runtime enforcement of doc/pack-infrastructure.md §5 quarantine.
///
/// <para>A pack whose <c>requiresRuntime.max</c> is below this build's
/// <see cref="PackRuntime.Version"/> is <em>quarantined, not auto-upgraded and not auto-removed</em>:
/// its files stay on disk, its automations are force-disabled at config load, and its agents are
/// refused at dispatch. The install already records everything needed to decide this — each
/// <c>packs.lock.json</c> entry carries <c>requiresRuntime</c> alongside the pack's agent and
/// automation ids — so enforcement is a read of the lockfile, never a re-composition.</para>
///
/// <para>Why the lockfile and not deserialization: <c>AutomationStore.JsonOptions</c> sets no
/// <c>UnmappedMemberHandling</c>, so System.Text.Json's default (Skip) applies. An automation
/// written against a newer action vocabulary deserializes cleanly with the unknown field silently
/// dropped and then runs with semantics its author did not intend. Nothing throws. The declared
/// <c>requiresRuntime</c> bound is the only place that mismatch is visible.</para>
/// </summary>
public sealed class PackQuarantine
{
    /// <summary>Nothing quarantined — a workspace with no lockfile, or one where every pack fits.</summary>
    public static readonly PackQuarantine None = new(
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> _agents;
    private readonly IReadOnlyDictionary<string, string> _automations;

    private PackQuarantine(
        IReadOnlyList<string> packIds,
        IReadOnlyDictionary<string, string> agents,
        IReadOnlyDictionary<string, string> automations)
    {
        PackIds = packIds;
        _agents = agents;
        _automations = automations;
    }

    /// <summary>Quarantined pack ids, ordinal ascending. Empty is the overwhelmingly common case.</summary>
    public IReadOnlyList<string> PackIds { get; }

    public bool IsEmpty => PackIds.Count == 0;

    /// <summary>The quarantined pack owning this agent slug, or null when dispatch may proceed.</summary>
    public string? PackOfAgent(string slug) =>
        _agents.TryGetValue(slug, out var pack) ? pack : null;

    /// <summary>The quarantined pack owning this automation id, or null when it may run.</summary>
    public string? PackOfAutomation(string automationId) =>
        _automations.TryGetValue(automationId, out var pack) ? pack : null;

    // The lockfile changes only on install/uninstall, but is read on every config load and every
    // dispatch, so it is cached against its own last-write stamp rather than re-parsed each time.
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, PackQuarantine Value)> Cache = new();

    public static PackQuarantine ForWorkspace(string workspacePath, int runtimeVersion = PackRuntime.Version)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return None;
        var path = Path.Combine(workspacePath, ".agents", PackLockFile.FileName);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                Cache.TryRemove(path, out _);
                return None;
            }
        }
        catch { return None; }

        if (Cache.TryGetValue(path, out var cached)
            && cached.Stamp == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Value;
        }

        var built = Read(path, runtimeVersion);
        Cache[path] = (info.LastWriteTimeUtc, info.Length, built);
        return built;
    }

    private static PackQuarantine Read(string path, int runtimeVersion)
    {
        PackLockFile? file;
        try { file = PackInstaller.ReadLock(path); }
        catch
        {
            // An unreadable lockfile must not take the automation engine down. Quarantine is a
            // restriction; failing to read it degrades to "nothing quarantined", which is the same
            // behaviour as a workspace that predates packs entirely.
            return None;
        }
        if (file is null) return None;

        var packIds = new List<string>();
        var agents = new Dictionary<string, string>(StringComparer.Ordinal);
        var automations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in file.Packs)
        {
            if (entry.RequiresRuntime.Evaluate(runtimeVersion) == PackCompatibility.Compatible) continue;
            packIds.Add(entry.Id);
            foreach (var slug in entry.Agents) agents[slug] = entry.Id;
            foreach (var id in entry.Automations) automations[id] = entry.Id;
        }

        if (packIds.Count == 0) return None;
        packIds.Sort(StringComparer.Ordinal);
        return new PackQuarantine(packIds, agents, automations);
    }

    /// <summary>Drops the cached decision for a workspace. Call after an install or uninstall
    /// rewrites the lockfile within the same process.</summary>
    public static void Invalidate(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return;
        Cache.TryRemove(Path.Combine(workspacePath, ".agents", PackLockFile.FileName), out _);
    }
}

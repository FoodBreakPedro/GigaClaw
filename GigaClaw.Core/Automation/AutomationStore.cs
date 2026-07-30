using System.Collections.Concurrent;
using System.Text.Json;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

public sealed class AutomationStore : IDisposable
{
    private readonly ProjectService _projectService;
    private readonly ConcurrentDictionary<string, ProjectEntry> _cache = new();

    public event Action<string>? OnConfigChangedOnDisk;

    public AutomationStore(ProjectService projectService)
    {
        _projectService = projectService;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonSerializerOptions JsonOptions => Json;

    public async Task<(AutomationConfig Config, string WorkspacePath, string ConfigPath)> LoadAsync(string slug)
    {
        var project = await _projectService.GetProjectAsync(slug)
            ?? throw new InvalidOperationException($"Projet '{slug}' introuvable.");
        var workspace = _projectService.ResolveWorkspacePath(project);
        var agentsDir = Path.Combine(workspace, ".agents");
        var configPath = Path.Combine(agentsDir, "automations.json");

        var entry = _cache.GetOrAdd(slug, s => new ProjectEntry(s));
        lock (entry.Lock)
        {
            if (entry.ConfigPath != configPath)
            {
                entry.DisposeWatcher();
                entry.ConfigPath = configPath;
                entry.WorkspacePath = workspace;
                if (Directory.Exists(agentsDir))
                    entry.AttachWatcher(agentsDir, configPath, () => OnConfigChangedOnDisk?.Invoke(slug));
            }
        }

        AutomationConfig config;
        if (File.Exists(configPath))
        {
            await using var fs = File.OpenRead(configPath);
            config = await JsonSerializer.DeserializeAsync<AutomationConfig>(fs, Json)
                ?? new AutomationConfig();
        }
        else
        {
            config = new AutomationConfig();
        }

        ApplyQuarantine(config, workspace);

        entry.LastLoaded = config;
        return (config, workspace, configPath);
    }

    /// <summary>
    /// §5: a quarantined pack's automations are force-disabled at config load. Done here rather
    /// than at evaluation so every reader — the engine, the editor, the API — sees one answer, and
    /// the owner can see in the UI that the automation is off and why.
    ///
    /// <para>The disable is not written back by this method; it reaches disk only if something
    /// saves the config afterwards, and that is harmless because updating the pack reinstalls its
    /// automations from pack data, which restores <c>enabled</c> wholesale.</para>
    /// </summary>
    internal static void ApplyQuarantine(
        AutomationConfig config, string workspace, int runtimeVersion = Packs.PackRuntime.Version)
    {
        var quarantine = PackQuarantine.ForWorkspace(workspace, runtimeVersion);
        if (quarantine.IsEmpty) return;

        foreach (var automation in config.Automations)
        {
            if (!automation.Enabled) continue;
            if (quarantine.PackOfAutomation(automation.Id) is null) continue;
            automation.Enabled = false;
        }
    }

    public AutomationConfig? GetCached(string slug) =>
        _cache.TryGetValue(slug, out var e) ? e.LastLoaded : null;

    public async Task SaveAsync(string slug, AutomationConfig config)
    {
        var (_, _, configPath) = await LoadAsync(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var entry = _cache[slug];
        entry.SuppressWatcher = true;
        try
        {
            await using var fs = File.Create(configPath);
            await JsonSerializer.SerializeAsync(fs, config, Json);
        }
        finally
        {
            entry.SuppressWatcher = false;
            entry.LastLoaded = config;
        }
    }

    public void Dispose()
    {
        foreach (var e in _cache.Values) e.DisposeWatcher();
        _cache.Clear();
    }

    private sealed class ProjectEntry
    {
        public ProjectEntry(string slug) { }
        public string ConfigPath { get; set; } = "";
        public string WorkspacePath { get; set; } = "";
        public AutomationConfig? LastLoaded { get; set; }
        public bool SuppressWatcher { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public readonly object Lock = new();

        public void AttachWatcher(string dir, string path, Action onChange)
        {
            var w = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            void fire(object _, FileSystemEventArgs __) { if (!SuppressWatcher) onChange(); }
            w.Changed += fire;
            w.Created += fire;
            Watcher = w;
        }

        public void DisposeWatcher() { Watcher?.Dispose(); Watcher = null; }
    }
}

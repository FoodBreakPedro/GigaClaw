using System.Text.Json;

namespace GigaClaw.Core.Services;

public class AppSettingsService
{
    private readonly string _settingsPath;
    private AppSettingsData _data = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string dataDir)
    {
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Load();
    }

    public string Language
    {
        get => _data.Language;
        set
        {
            if (_data.Language == value) return;
            _data.Language = value;
            Save();
            OnLanguageChanged?.Invoke();
        }
    }

    public event Action? OnLanguageChanged;

    public bool OnboardingSeen
    {
        get => _data.OnboardingSeen;
        set
        {
            if (_data.OnboardingSeen == value) return;
            _data.OnboardingSeen = value;
            Save();
        }
    }

    public string? UpdateDismissedVersion
    {
        get => _data.UpdateDismissedVersion;
        set
        {
            if (_data.UpdateDismissedVersion == value) return;
            _data.UpdateDismissedVersion = value;
            Save();
        }
    }

    public DateTime? UpdateCheckLastRun
    {
        get => _data.UpdateCheckLastRun;
        set
        {
            if (_data.UpdateCheckLastRun == value) return;
            _data.UpdateCheckLastRun = value;
            Save();
        }
    }

    /// <summary>Random, non-reversible instance identifier; generated once, never tied to any user data.</summary>
    public string TelemetryInstanceId
    {
        get
        {
            if (string.IsNullOrEmpty(_data.TelemetryInstanceId))
            {
                _data.TelemetryInstanceId = Guid.NewGuid().ToString();
                Save();
            }
            return _data.TelemetryInstanceId;
        }
    }

    public DateTime? TelemetryLastSent
    {
        get => _data.TelemetryLastSent;
        set
        {
            if (_data.TelemetryLastSent == value) return;
            _data.TelemetryLastSent = value;
            Save();
        }
    }

    /// <summary>
    /// Whether the server-wide Hermes Agent chat target is enabled. Supplying
    /// GIGACLAW_HERMES_API_KEY also enables it for headless deployments.
    /// </summary>
    public bool HermesEnabled =>
        _data.HermesEnabled ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIGACLAW_HERMES_API_KEY"));

    public string HermesApiBaseUrl =>
        Environment.GetEnvironmentVariable("GIGACLAW_HERMES_API_BASE_URL")
        ?? _data.HermesApiBaseUrl
        ?? "http://127.0.0.1:8642";

    public bool HermesApiKeyConfigured => !string.IsNullOrWhiteSpace(GetHermesApiKey());

    /// <summary>Returns the Hermes bearer key for server-side callers only.</summary>
    public string? GetHermesApiKey() =>
        Environment.GetEnvironmentVariable("GIGACLAW_HERMES_API_KEY")
        ?? _data.HermesApiKey;

    /// <summary>
    /// Atomically updates the local Hermes connection. A null/blank apiKey keeps the
    /// previously stored key so the settings UI never has to round-trip a secret.
    /// </summary>
    public void ConfigureHermes(bool enabled, string? baseUrl, string? apiKey)
    {
        _data.HermesEnabled = enabled;
        _data.HermesApiBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://127.0.0.1:8642"
            : baseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            _data.HermesApiKey = apiKey.Trim();
        Save();
    }

    /// <summary>
    /// R3/U17 trust anchor for host-side outbound automation actions. The list lives in the
    /// owner's settings.json, which sits outside every workspace and therefore outside every
    /// agent's write globs — a ticket label is orchestration metadata, never authorization.
    /// Re-reads the file on every call so an owner edit takes effect on the next httpRequest
    /// execution without an engine restart.
    /// </summary>
    public IReadOnlyCollection<string> GetApprovedOutboundHosts()
    {
        Load();
        return _data.ApprovedOutboundHosts ?? [];
    }

    /// <summary>
    /// R6 (doc/roadmap/lane-codex-runtime.md) trust anchor for the merge queue: the same
    /// "owner's settings.json, outside every workspace and every agent's write globs" pattern R3
    /// uses for <see cref="GetApprovedOutboundHosts"/>. A ticket label is orchestration metadata an
    /// agent can set itself — it is not authorization to land that agent's own branch. Listing a
    /// project slug here is the owner's explicit statement that the merge queue may actually merge
    /// for that project; an unlisted project's candidates stay held. Re-reads the file on every call
    /// so an owner edit takes effect on the queue processor's next poll without an engine restart.
    /// </summary>
    public IReadOnlyCollection<string> GetApprovedMergeProjects()
    {
        Load();
        return _data.ApprovedMergeProjects ?? [];
    }

    /// <summary>
    /// C7/U5 per-project GitHub configuration. Returns null for every project that has not opted
    /// in — local-first is the default, and an absent entry is how a project says so.
    /// Re-reads the file on every call for the same reason
    /// <see cref="GetApprovedOutboundHosts"/> does: an owner edit must take effect on the next
    /// poll without an engine restart.
    /// </summary>
    public Github.GitHubProjectConfig? GetGitHubConfig(string projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug)) return null;
        Load();
        if (_data.GitHub is null || !_data.GitHub.TryGetValue(projectSlug, out var stored) || stored is null)
            return null;
        return new Github.GitHubProjectConfig
        {
            Enabled = stored.Enabled,
            Owner = stored.Owner ?? "",
            Repo = stored.Repo ?? "",
            ImportLabel = stored.ImportLabel ?? "gigaclaw",
            ImportStatus = string.IsNullOrWhiteSpace(stored.ImportStatus) ? "Backlog" : stored.ImportStatus,
            CommentOnIssueWhenTicketDone = stored.CommentOnIssueWhenTicketDone,
            CloseIssueWhenTicketDone = stored.CloseIssueWhenTicketDone,
            DoneStatuses = stored.DoneStatuses is { Count: > 0 } ? stored.DoneStatuses : ["Done"],
            OwnerLogins = stored.OwnerLogins ?? [],
            ApiBaseUrl = string.IsNullOrWhiteSpace(stored.ApiBaseUrl) ? "https://api.github.com" : stored.ApiBaseUrl,
        };
    }

    /// <summary>
    /// The project's GitHub personal access token, for server-side callers only. Never returned by
    /// the API, never rendered, never written to a ticket, a comment, a run prompt or a log line —
    /// <see cref="GitHubTokenConfigured"/> is what every caller that only needs to know "is it set"
    /// must use instead. The environment variable exists for headless deployments and mirrors
    /// <see cref="GetHermesApiKey"/>.
    /// </summary>
    public string? GetGitHubToken(string projectSlug)
    {
        var perProject = Environment.GetEnvironmentVariable(
            $"GIGACLAW_GITHUB_TOKEN_{projectSlug?.ToUpperInvariant().Replace('-', '_')}");
        if (!string.IsNullOrWhiteSpace(perProject)) return perProject;

        var global = Environment.GetEnvironmentVariable("GIGACLAW_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(global)) return global;

        if (string.IsNullOrWhiteSpace(projectSlug)) return null;
        Load();
        if (_data.GitHub is null || !_data.GitHub.TryGetValue(projectSlug, out var stored)) return null;
        return string.IsNullOrWhiteSpace(stored?.Token) ? null : stored.Token;
    }

    public bool GitHubTokenConfigured(string projectSlug) =>
        !string.IsNullOrWhiteSpace(GetGitHubToken(projectSlug));

    /// <summary>
    /// Writes the project's GitHub configuration. A null/blank <paramref name="token"/> keeps the
    /// previously stored one, so a settings round-trip never has to carry a secret back and forth
    /// — the same rule <see cref="ConfigureHermes"/> follows.
    /// </summary>
    public void ConfigureGitHub(string projectSlug, Github.GitHubProjectConfig config, string? token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        ArgumentNullException.ThrowIfNull(config);
        Load();
        _data.GitHub ??= new();
        if (!_data.GitHub.TryGetValue(projectSlug, out var stored) || stored is null)
            stored = _data.GitHub[projectSlug] = new GitHubProjectData();

        stored.Enabled = config.Enabled;
        stored.Owner = config.Owner?.Trim() ?? "";
        stored.Repo = config.Repo?.Trim() ?? "";
        stored.ImportLabel = config.ImportLabel?.Trim() ?? "";
        stored.ImportStatus = config.ImportStatus?.Trim() ?? "Backlog";
        stored.CommentOnIssueWhenTicketDone = config.CommentOnIssueWhenTicketDone;
        stored.CloseIssueWhenTicketDone = config.CloseIssueWhenTicketDone;
        stored.DoneStatuses = config.DoneStatuses?.ToList() ?? ["Done"];
        stored.OwnerLogins = config.OwnerLogins?.ToList() ?? [];
        stored.ApiBaseUrl = config.ApiBaseUrl?.Trim() ?? "https://api.github.com";
        if (!string.IsNullOrWhiteSpace(token))
            stored.Token = token.Trim();
        Save();
    }

    /// <summary>Forgets a project's GitHub configuration and its stored token.</summary>
    public void RemoveGitHubConfig(string projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug)) return;
        Load();
        if (_data.GitHub is null || !_data.GitHub.Remove(projectSlug)) return;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var json = File.ReadAllText(_settingsPath);
            _data = JsonSerializer.Deserialize<AppSettingsData>(json, JsonOpts) ?? new();
        }
        catch { /* use defaults if settings file is corrupted */ _data = new(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOpts);
        File.WriteAllText(_settingsPath, json);
    }

    private class AppSettingsData
    {
        public string Language { get; set; } = "en";
        public bool OnboardingSeen { get; set; } = false;
        public string? UpdateDismissedVersion { get; set; }
        public DateTime? UpdateCheckLastRun { get; set; }
        public string? TelemetryInstanceId { get; set; }
        public DateTime? TelemetryLastSent { get; set; }
        public bool HermesEnabled { get; set; }
        public string? HermesApiBaseUrl { get; set; }
        public string? HermesApiKey { get; set; }
        public List<string>? ApprovedOutboundHosts { get; set; }
        public List<string>? ApprovedMergeProjects { get; set; }
        /// <summary>C7/U5: per-project GitHub surface config, keyed by project slug.</summary>
        public Dictionary<string, GitHubProjectData>? GitHub { get; set; }
    }

    /// <summary>
    /// On-disk shape of a project's GitHub settings. <see cref="Token"/> never leaves this file
    /// except through <see cref="GetGitHubToken"/>, which only server-side request builders call.
    /// </summary>
    private class GitHubProjectData
    {
        public bool Enabled { get; set; }
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? ImportLabel { get; set; }
        public string? ImportStatus { get; set; }
        public bool CommentOnIssueWhenTicketDone { get; set; }
        public bool CloseIssueWhenTicketDone { get; set; }
        public List<string>? DoneStatuses { get; set; }
        public List<string>? OwnerLogins { get; set; }
        public string? ApiBaseUrl { get; set; }
        public string? Token { get; set; }
    }
}

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
    }
}

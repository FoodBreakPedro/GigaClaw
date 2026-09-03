using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using GigaClaw.Core.Services;

namespace GigaClaw.Web.Services;

public class UpdateCheckService : BackgroundService
{
    private readonly AppSettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpdateCheckService> _logger;
    private string? _latestVersion;

    public UpdateCheckService(AppSettingsService settings, IHttpClientFactory httpFactory, ILogger<UpdateCheckService> logger)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _logger = logger;
        CurrentVersion = ResolveCurrentVersion();
    }

    public string CurrentVersion { get; }

    public string? LatestVersion => _latestVersion;

    public bool HasUpdate =>
        !string.IsNullOrEmpty(_latestVersion)
        && VersionCompare.IsNewer(CurrentVersion, _latestVersion)
        && !string.Equals(NormalizeTag(_latestVersion), NormalizeTag(_settings.UpdateDismissedVersion), StringComparison.OrdinalIgnoreCase);

    public event Action? OnChange;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                await CheckIfDueAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckIfDueAsync(CancellationToken ct)
    {
        var last = _settings.UpdateCheckLastRun;
        if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24)) return;

        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GigaClaw-UpdateCheck");
            // An update check is never worth holding a connection open for a minute; the default
            // 100s timeout let a stalled api.github.com call block this task for ~60s per attempt.
            client.Timeout = TimeSpan.FromSeconds(15);
            var resp = await client.GetFromJsonAsync<GitHubRelease>(
                "https://api.github.com/repos/FoodBreakPedro/GigaClaw/releases/latest", ct);
            if (resp is not null && !string.IsNullOrWhiteSpace(resp.TagName))
            {
                _latestVersion = resp.TagName;
                OnChange?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
        }
        finally
        {
            // Stamp the attempt whether or not it succeeded. A repo with no published release
            // answers 404 forever, and only stamping on success meant retrying every hour for
            // good. Worst case a transient outage delays discovery of a release by 24h, which is
            // the cadence this check is already built around.
            if (!ct.IsCancellationRequested) _settings.UpdateCheckLastRun = DateTime.UtcNow;
        }
    }

    public void Dismiss()
    {
        if (string.IsNullOrEmpty(_latestVersion)) return;
        _settings.UpdateDismissedVersion = _latestVersion;
        OnChange?.Invoke();
    }

    public void SimulateUpdate(string fakeVersion)
    {
        _latestVersion = fakeVersion;
        _settings.UpdateDismissedVersion = null;
        OnChange?.Invoke();
    }

    public void ResetSimulation()
    {
        _latestVersion = null;
        _settings.UpdateDismissedVersion = null;
        _settings.UpdateCheckLastRun = null;
        OnChange?.Invoke();
    }

    private static string ResolveCurrentVersion()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return VersionFormatter.Format(info);
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return tag;
        return tag.StartsWith("v") || tag.StartsWith("V") ? tag[1..] : tag;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}

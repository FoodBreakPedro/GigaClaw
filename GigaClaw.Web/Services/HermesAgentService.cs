using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;

namespace GigaClaw.Web.Services;

public sealed record HermesConversationMessage(string Role, string Content);

public sealed record HermesChatRunContext(
    string ProjectSlug,
    string WorkspacePath,
    string ChatTarget,
    string Message,
    string Instructions,
    string SessionId,
    int? TicketId = null,
    IReadOnlyList<HermesConversationMessage>? ConversationHistory = null,
    IReadOnlyList<string>? ImagePaths = null,
    Action<StreamEvent>? OnEventHook = null);

public sealed record HermesProbeResult(bool Success, string Message);

/// <summary>
/// Bridges Hermes Agent's authenticated Runs API into GigaClaw's in-process run model.
/// The service owns the upstream SSE connection even when the browser drawer disconnects,
/// so local replay/reattach semantics remain identical to Claude CLI chat runs.
/// </summary>
public sealed class HermesAgentService
{
    public const string TargetSlug = "_hermes";
    public const string BackendName = "hermes";

    private readonly AppSettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<HermesAgentService> _logger;

    public HermesAgentService(
        AppSettingsService settings,
        IHttpClientFactory httpFactory,
        AgentRunRegistry runs,
        ILogger<HermesAgentService> logger)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _runs = runs;
        _logger = logger;
    }

    public bool IsConfigured =>
        _settings.HermesEnabled &&
        _settings.HermesApiKeyConfigured &&
        TryNormalizeBaseUri(_settings.HermesApiBaseUrl, out _);

    public async Task<HermesProbeResult> ProbeAsync(
        string? baseUrl = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? _settings.HermesApiBaseUrl : baseUrl;
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? _settings.GetHermesApiKey() : apiKey;
        if (!TryNormalizeBaseUri(baseUrl, out var root))
            return new(false, "Hermes URL must be an absolute http:// or https:// URL.");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new(false, "Hermes API key is required.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Get, new Uri(root, "v1/capabilities"), apiKey);
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Hermes returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var platform = doc.RootElement.TryGetProperty("platform", out var p) ? p.GetString() : null;
            return string.Equals(platform, "hermes-agent", StringComparison.OrdinalIgnoreCase)
                ? new(true, "Connected to Hermes Agent.")
                : new(false, "The endpoint responded, but did not identify itself as Hermes Agent.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "Hermes connection timed out.");
        }
        catch (Exception ex)
        {
            return new(false, $"Hermes connection failed: {ex.Message}");
        }
    }

    /// <summary>Registers immediately and runs in the background, matching ClaudeRunner chat semantics.</summary>
    public AgentRun StartChat(HermesChatRunContext ctx)
    {
        var run = RegisterRun(ctx);
        _ = ExecuteRunAsync(run, ctx, CancellationToken.None);
        return run;
    }

    /// <summary>Runs to completion; exposed for deterministic service tests.</summary>
    public Task<AgentRun> RunAsync(HermesChatRunContext ctx, CancellationToken ct = default)
    {
        var run = RegisterRun(ctx);
        return ExecuteRunAsync(run, ctx, ct);
    }

    private AgentRun RegisterRun(HermesChatRunContext ctx)
    {
        var run = new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            ProjectSlug = ctx.ProjectSlug,
            TicketId = ctx.TicketId,
            AgentName = "Hermes",
            SkillFile = "hermes-api-server",
            ConcurrencyGroup = $"chat:{ctx.ProjectSlug}:{ctx.ChatTarget}",
            StartedAt = DateTime.UtcNow,
            SessionId = ctx.SessionId,
            Model = "hermes-agent",
            ChatTarget = ctx.ChatTarget,
            Backend = BackendName,
        };
        if (ctx.OnEventHook is not null) run.OnEvent += ctx.OnEventHook;
        _runs.Register(run);
        return run;
    }

    private async Task<AgentRun> ExecuteRunAsync(
        AgentRun run,
        HermesChatRunContext ctx,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cancellation.Token);
        try
        {
            if (!IsConfigured)
                throw new InvalidOperationException(
                    "Hermes is not configured. Add its localhost API URL and API key in Project Settings.");

            if (!TryNormalizeBaseUri(_settings.HermesApiBaseUrl, out var root))
                throw new InvalidOperationException("The configured Hermes API URL is invalid.");
            var apiKey = _settings.GetHermesApiKey()
                ?? throw new InvalidOperationException("Hermes API key is not configured.");

            var input = BuildInput(ctx.Message, ctx.ImagePaths);
            var history = (ctx.ConversationHistory ?? [])
                .Where(m => m.Role is "user" or "assistant")
                .Select(m => new { role = m.Role, content = m.Content })
                .ToArray();
            var payload = new
            {
                input,
                instructions = ctx.Instructions,
                session_id = ctx.SessionId,
                conversation_history = history,
            };

            using var client = CreateClient();
            using var startRequest = CreateRequest(HttpMethod.Post, new Uri(root, "v1/runs"), apiKey);
            startRequest.Headers.TryAddWithoutValidation(
                "X-Hermes-Session-Key",
                BuildSessionKey(ctx.ProjectSlug, ctx.ChatTarget));
            startRequest.Content = JsonContent.Create(payload);

            using var startResponse = await client.SendAsync(startRequest, linked.Token);
            if (!startResponse.IsSuccessStatusCode)
            {
                var body = await startResponse.Content.ReadAsStringAsync(linked.Token);
                throw new HttpRequestException(
                    $"Hermes run start returned HTTP {(int)startResponse.StatusCode}: {TrimError(body)}");
            }

            using (var startDoc = JsonDocument.Parse(
                       await startResponse.Content.ReadAsStreamAsync(linked.Token)))
            {
                run.ExternalRunId = startDoc.RootElement.TryGetProperty("run_id", out var rid)
                    ? rid.GetString()
                    : null;
            }
            if (string.IsNullOrWhiteSpace(run.ExternalRunId))
                throw new InvalidOperationException("Hermes did not return a run_id.");

            run.Push(new StreamEvent(
                DateTime.UtcNow,
                "launch",
                $"Connected to Hermes run {run.ExternalRunId}"));

            await ConsumeEventsAsync(client, root, apiKey, run, linked.Token);
            if (run.Status == AgentRunStatus.Running)
                await ReconcileTerminalStatusAsync(client, root, apiKey, run, linked.Token);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested || ct.IsCancellationRequested)
        {
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermes chat run failed for project {ProjectSlug}", ctx.ProjectSlug);
            run.Push(new StreamEvent(DateTime.UtcNow, "error", ex.Message));
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
        }
        finally
        {
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            CleanupImageTempFiles(ctx.ImagePaths);
        }

        return run;
    }

    public async Task StopAsync(AgentRun run, CancellationToken ct = default)
    {
        if (!string.Equals(run.Backend, BackendName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run is not owned by Hermes.");

        var externalRunId = run.ExternalRunId;
        if (!string.IsNullOrWhiteSpace(externalRunId) &&
            TryNormalizeBaseUri(_settings.HermesApiBaseUrl, out var root) &&
            _settings.GetHermesApiKey() is string apiKey)
        {
            try
            {
                using var client = CreateClient();
                using var request = CreateRequest(
                    HttpMethod.Post,
                    new Uri(root, $"v1/runs/{Uri.EscapeDataString(externalRunId)}/stop"),
                    apiKey);
                request.Content = JsonContent.Create(new { });
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    _logger.LogWarning(
                        "Hermes stop for {RunId} returned HTTP {Status}",
                        externalRunId,
                        (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to stop Hermes run {RunId}", externalRunId);
            }
        }

        run.Cancellation.Cancel();
        _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
    }

    public async Task ApproveAsync(
        AgentRun run,
        string choice,
        bool resolveAll = false,
        CancellationToken ct = default)
    {
        var normalized = choice.Trim().ToLowerInvariant();
        if (normalized is not ("once" or "session" or "always" or "deny"))
            throw new ArgumentException("Approval choice must be once, session, always, or deny.", nameof(choice));
        if (!string.Equals(run.Backend, BackendName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(run.ExternalRunId))
            throw new InvalidOperationException("Run is not an active Hermes run.");
        if (!TryNormalizeBaseUri(_settings.HermesApiBaseUrl, out var root))
            throw new InvalidOperationException("The configured Hermes API URL is invalid.");
        var apiKey = _settings.GetHermesApiKey()
            ?? throw new InvalidOperationException("Hermes API key is not configured.");

        using var client = CreateClient();
        using var request = CreateRequest(
            HttpMethod.Post,
            new Uri(root, $"v1/runs/{Uri.EscapeDataString(run.ExternalRunId)}/approval"),
            apiKey);
        request.Content = JsonContent.Create(new { choice = normalized, resolve_all = resolveAll });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Hermes approval returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }
    }

    private async Task ConsumeEventsAsync(
        HttpClient client,
        Uri root,
        string apiKey,
        AgentRun run,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            new Uri(root, $"v1/runs/{Uri.EscapeDataString(run.ExternalRunId!)}/events"),
            apiKey);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Hermes event stream returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].TrimStart();
            if (string.IsNullOrWhiteSpace(json)) continue;
            using var doc = JsonDocument.Parse(json);
            HandleRemoteEvent(run, doc.RootElement);
        }
    }

    private void HandleRemoteEvent(AgentRun run, JsonElement root)
    {
        var eventName = GetString(root, "event") ?? "event";
        var now = DateTime.UtcNow;
        switch (eventName)
        {
            case "message.delta":
                var delta = GetString(root, "delta");
                if (!string.IsNullOrEmpty(delta))
                    run.Push(new StreamEvent(now, "content_block_delta", delta));
                break;

            case "tool.started":
                run.Push(new StreamEvent(
                    now,
                    "tool_use",
                    GetString(root, "tool") ?? "tool",
                    root.GetRawText()));
                break;

            case "tool.completed":
                run.Push(new StreamEvent(
                    now,
                    "tool_result",
                    $"{GetString(root, "tool") ?? "tool"} completed",
                    root.GetRawText()));
                break;

            case "reasoning.available":
                run.Push(new StreamEvent(now, "reasoning", GetString(root, "text") ?? ""));
                break;

            case "approval.request":
                run.IsAwaitingUserAnswer = true;
                run.Push(new StreamEvent(
                    now,
                    "approval_request",
                    GetString(root, "description") ?? GetString(root, "command") ?? "Approval required",
                    root.GetRawText()));
                break;

            case "approval.responded":
                run.IsAwaitingUserAnswer = false;
                run.Push(new StreamEvent(
                    now,
                    "approval_response",
                    GetString(root, "choice") ?? "resolved",
                    root.GetRawText()));
                break;

            case "run.completed":
                AddUsage(run, root);
                var output = GetString(root, "output");
                if (!string.IsNullOrWhiteSpace(output))
                    run.Push(new StreamEvent(now, "assistant", $"[assistant] {output}"));
                _runs.Complete(run.RunId, AgentRunStatus.Completed, 0);
                break;

            case "run.failed":
                run.Push(new StreamEvent(now, "error", GetString(root, "error") ?? "Hermes run failed."));
                _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
                break;

            case "run.cancelled":
                _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
                break;

            default:
                run.Push(new StreamEvent(now, eventName.Replace('.', '_'), EventSummary(root), root.GetRawText()));
                break;
        }
    }

    private async Task ReconcileTerminalStatusAsync(
        HttpClient client,
        Uri root,
        string apiKey,
        AgentRun run,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            new Uri(root, $"v1/runs/{Uri.EscapeDataString(run.ExternalRunId!)}"),
            apiKey);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Hermes event stream ended before a terminal event.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var status = GetString(doc.RootElement, "status");
        switch (status)
        {
            case "completed":
                AddUsage(run, doc.RootElement);
                var output = GetString(doc.RootElement, "output");
                if (!string.IsNullOrWhiteSpace(output))
                    run.Push(new StreamEvent(DateTime.UtcNow, "assistant", $"[assistant] {output}"));
                _runs.Complete(run.RunId, AgentRunStatus.Completed, 0);
                break;
            case "cancelled":
                _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
                break;
            default:
                throw new InvalidOperationException(
                    $"Hermes event stream ended while the remote run status was '{status ?? "unknown"}'.");
        }
    }

    private static void AddUsage(AgentRun run, JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;
        var input = GetInt(usage, "input_tokens");
        var output = GetInt(usage, "output_tokens");
        if (input > 0 || output > 0)
            run.AddUsage(input, output, 0, 0, null);
    }

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(nameof(HermesAgentService));
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static bool TryNormalizeBaseUri(string? value, out Uri root)
    {
        root = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
            return false;
        var text = parsed.ToString().TrimEnd('/');
        if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            text = text[..^3];
        root = new Uri(text.TrimEnd('/') + "/");
        return true;
    }

    private static string BuildSessionKey(string projectSlug, string target)
    {
        var raw = $"agent:main:gigaclaw:project:{projectSlug}:target:{target}";
        return new string(raw.Where(c => !char.IsControl(c)).Take(256).ToArray());
    }

    private static string BuildInput(string message, IReadOnlyList<string>? imagePaths)
    {
        if (imagePaths is null || imagePaths.Count == 0) return message;
        var lines = imagePaths.Select(p => $"- {p}");
        return $"{message}\n\nAttached images are available on this host at:\n{string.Join("\n", lines)}\n" +
               "Use your file or vision tools to inspect them when relevant.";
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static string EventSummary(JsonElement root) =>
        GetString(root, "text")
        ?? GetString(root, "summary")
        ?? GetString(root, "tool")
        ?? GetString(root, "event")
        ?? "Hermes event";

    private static string TrimError(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }

    private static void CleanupImageTempFiles(IReadOnlyList<string>? imagePaths)
    {
        if (imagePaths is null) return;
        foreach (var path in imagePaths)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}

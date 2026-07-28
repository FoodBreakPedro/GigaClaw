using System.Net;
using System.Text;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using GigaClaw.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Services;

public sealed class HermesAgentServiceTests
{
    [Fact]
    public void Hermes_settings_round_trip_without_requiring_secret_to_be_reentered()
    {
        using var dir = new TempDir();
        var settings = new AppSettingsService(dir.Path);

        settings.ConfigureHermes(true, "http://127.0.0.1:8642/", "local-secret");
        settings.ConfigureHermes(true, "http://localhost:8642", apiKey: null);

        var reloaded = new AppSettingsService(dir.Path);
        Assert.True(reloaded.HermesEnabled);
        Assert.Equal("http://localhost:8642", reloaded.HermesApiBaseUrl);
        Assert.Equal("local-secret", reloaded.GetHermesApiKey());
    }

    [Fact]
    public async Task RunAsync_maps_Hermes_run_events_into_GigaClaw_run_events()
    {
        using var dir = new TempDir();
        var settings = new AppSettingsService(dir.Path);
        settings.ConfigureHermes(true, "http://127.0.0.1:8642", "local-secret");

        var handler = new HermesStubHandler();
        var service = new HermesAgentService(
            settings,
            new StubHttpClientFactory(handler),
            new AgentRunRegistry(),
            NullLogger<HermesAgentService>.Instance);

        var run = await service.RunAsync(new HermesChatRunContext(
            ProjectSlug: "gigaclaw",
            WorkspacePath: dir.Path,
            ChatTarget: HermesAgentService.TargetSlug,
            Message: "What is the frontend built on?",
            Instructions: "Inspect the repository before answering.",
            SessionId: "session-123",
            ConversationHistory:
            [
                new HermesConversationMessage("user", "Earlier question"),
                new HermesConversationMessage("assistant", "Earlier answer"),
            ]));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(HermesAgentService.BackendName, run.Backend);
        Assert.Equal("run_remote123", run.ExternalRunId);
        Assert.Equal(12, run.InputTokens);
        Assert.Equal(7, run.OutputTokens);

        var events = run.SnapshotBuffer();
        Assert.Contains(events, e => e.Kind == "content_block_delta" && e.Text == "GigaClaw ");
        Assert.Contains(events, e => e.Kind == "tool_use" && e.Text == "search_files");
        Assert.Contains(events, e => e.Kind == "approval_request");
        Assert.Contains(events, e => e.Kind == "assistant" && e.Text.Contains("Blazor Server"));

        Assert.NotNull(handler.StartBody);
        Assert.Contains("\"session_id\":\"session-123\"", handler.StartBody);
        Assert.Contains("\"conversation_history\"", handler.StartBody);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("local-secret", handler.LastAuthorizationParameter);
        Assert.StartsWith("agent:main:gigaclaw:", handler.SessionKey);
    }

    [Fact]
    public async Task ProbeAsync_rejects_non_Hermes_capability_endpoint()
    {
        using var dir = new TempDir();
        var settings = new AppSettingsService(dir.Path);
        settings.ConfigureHermes(true, "http://127.0.0.1:8642", "local-secret");
        var handler = new StaticHandler(
            HttpStatusCode.OK,
            """{"platform":"something-else"}""");
        var service = new HermesAgentService(
            settings,
            new StubHttpClientFactory(handler),
            new AgentRunRegistry(),
            NullLogger<HermesAgentService>.Instance);

        var result = await service.ProbeAsync();

        Assert.False(result.Success);
        Assert.Contains("did not identify itself", result.Message);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class HermesStubHandler : HttpMessageHandler
    {
        public string? StartBody { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? SessionKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (request.Headers.TryGetValues("X-Hermes-Session-Key", out var values))
                SessionKey = values.Single();

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/runs")
            {
                StartBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.Accepted, """{"run_id":"run_remote123","status":"started"}""");
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == "/v1/runs/run_remote123/events")
            {
                const string sse = """
                    data: {"event":"message.delta","run_id":"run_remote123","delta":"GigaClaw "}

                    data: {"event":"tool.started","run_id":"run_remote123","tool":"search_files","preview":"GigaClaw.Web"}

                    data: {"event":"tool.completed","run_id":"run_remote123","tool":"search_files","duration":0.1,"error":false}

                    data: {"event":"approval.request","run_id":"run_remote123","description":"Run tests","command":"dotnet test","choices":["once","deny"]}

                    data: {"event":"approval.responded","run_id":"run_remote123","choice":"once","resolved":1}

                    data: {"event":"run.completed","run_id":"run_remote123","output":"GigaClaw uses Blazor Server.","usage":{"input_tokens":12,"output_tokens":7,"total_tokens":19}}

                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }

            return Json(HttpStatusCode.NotFound, """{"error":"unexpected request"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
            new(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
    }
}

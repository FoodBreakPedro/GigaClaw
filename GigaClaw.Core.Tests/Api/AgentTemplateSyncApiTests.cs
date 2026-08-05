using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using GigaClaw.Web.Api;

namespace GigaClaw.Core.Tests.Api;

[Collection("ApiHost")]
public sealed class AgentTemplateSyncApiTests : IClassFixture<AgentTemplateSyncApiTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiFactory _factory;

    public AgentTemplateSyncApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Preview_returns_not_found_for_an_unknown_project()
    {
        var response = await _client.GetAsync("/api/projects/missing/agent-templates/sync");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Apply_maps_a_stale_preview_to_conflict()
    {
        var slug = await CreateProjectAsync("Stale Sync");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/agent-templates/sync",
            new ApplyAgentTemplateSyncRequest("stale-plan-token"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Missing_baseline_returns_manual_review_plan_without_workspace_writes()
    {
        var slug = await CreateProjectAsync("No Baseline");
        var previewResponse = await _client.GetAsync($"/api/projects/{slug}/agent-templates/sync");
        previewResponse.EnsureSuccessStatusCode();
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());

        Assert.False(preview.RootElement.GetProperty("canApply").GetBoolean());
        var token = preview.RootElement.GetProperty("planToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var applyResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/agent-templates/sync",
            new ApplyAgentTemplateSyncRequest(token!));
        applyResponse.EnsureSuccessStatusCode();
        using var applied = JsonDocument.Parse(await applyResponse.Content.ReadAsStringAsync());

        Assert.Empty(applied.RootElement.GetProperty("appliedPaths").EnumerateArray());
        Assert.False(applied.RootElement.GetProperty("automationsReloaded").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_factory.DataDirectory, "projects", slug, ".agents")));
        Assert.NotEmpty(applied.RootElement.GetProperty("membersCreated").EnumerateArray());
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("slug").GetString()!;
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public string DataDirectory { get; }

        public ApiFactory()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "gigaclaw-sync-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("GIGACLAW_DATA_DIR", DataDirectory);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("GIGACLAW_DATA_DIR", null);
            try { Directory.Delete(DataDirectory, recursive: true); } catch { }
        }
    }
}

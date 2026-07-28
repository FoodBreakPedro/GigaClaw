using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using GigaClaw.Web.Api;

namespace GigaClaw.Core.Tests.Api;

/// <summary>
/// AD-9 model seeding, exercised through the real HTTP endpoint (as opposed to
/// <c>AgentsTemplateServiceTests</c>, which covers the shared
/// <see cref="GigaClaw.Core.Services.AgentsTemplateService.EnsureAgentMembersAsync"/> helper
/// directly). <c>POST /api/projects/{slug}/initialize</c> and Home.razor's project-creation flow
/// both call that same helper, so this test's assertions hold for both call sites by construction.
/// </summary>
public sealed class ProjectInitializeModelSeedTests : IClassFixture<ProjectInitializeModelSeedTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public ProjectInitializeModelSeedTests(ApiFactory factory) => _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Initialize_SeedsAD9DefaultModelsOnCreatedMembers()
    {
        var createResp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Task10ModelSeed"));
        createResp.EnsureSuccessStatusCode();
        var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var slug = created.RootElement.GetProperty("slug").GetString()!;

        var initResp = await _client.PostAsync($"/api/projects/{slug}/initialize", content: null);
        Assert.True(initResp.IsSuccessStatusCode, $"initialize returned {(int)initResp.StatusCode}");
        var initBody = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var membersCreated = initBody.RootElement.GetProperty("membersCreated")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("content-writer", membersCreated);
        Assert.Contains("blog-reviewer", membersCreated);

        var membersResp = await _client.GetAsync($"/api/projects/{slug}/members");
        membersResp.EnsureSuccessStatusCode();
        using var members = JsonDocument.Parse(await membersResp.Content.ReadAsStringAsync());

        string? ModelOf(string slugName) => members.RootElement.EnumerateArray()
            .FirstOrDefault(m => m.GetProperty("slug").GetString() == slugName)
            .GetProperty("defaultModel").GetString();

        Assert.Equal("claude-sonnet-4-6", ModelOf("content-writer"));
        Assert.Equal("claude-opus-4-8", ModelOf("blog-reviewer"));
        Assert.Equal("claude-haiku-4-5", ModelOf("committer"));

        // An agent absent from models.json (e.g. producer) is seeded with no explicit default —
        // it relies on the project's FallbackModel, never a hardcoded stand-in.
        var producer = members.RootElement.EnumerateArray()
            .First(m => m.GetProperty("slug").GetString() == "producer");
        Assert.Equal(JsonValueKind.Null, producer.GetProperty("defaultModel").ValueKind);
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "gigaclaw-modelseed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("GIGACLAW_DATA_DIR", _dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("GIGACLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}

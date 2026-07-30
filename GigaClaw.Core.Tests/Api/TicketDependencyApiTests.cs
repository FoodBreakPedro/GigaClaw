using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GigaClaw.Web.Api;

namespace GigaClaw.Core.Tests.Api;

public sealed class TicketDependencyApiTests :
    IClassFixture<EndpointsRefactorTests.ApiFactory>,
    IDisposable
{
    private readonly HttpClient _client;

    public TicketDependencyApiTests(EndpointsRefactorTests.ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task DependencyEndpoints_ExposeCrudProjectionAndStableValidationErrors()
    {
        var slug = await CreateProjectAsync("dependency-api-" + Guid.NewGuid().ToString("N"));
        var blockedId = await CreateTicketAsync(slug, "Blocked");
        var blockerId = await CreateTicketAsync(slug, "Blocker");

        var created = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets/{blockedId}/dependencies",
            new AddTicketDependencyRequest(blockerId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var dependencies = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{slug}/tickets/{blockedId}/dependencies");
        var blocker = Assert.Single(dependencies.GetProperty("blockedBy").EnumerateArray());
        Assert.Equal(blockerId, blocker.GetProperty("id").GetInt32());
        Assert.Empty(dependencies.GetProperty("blocks").EnumerateArray());

        var duplicate = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets/{blockedId}/dependencies",
            new AddTicketDependencyRequest(blockerId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(
            "dependency_duplicate",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var cycle = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets/{blockerId}/dependencies",
            new AddTicketDependencyRequest(blockedId));
        Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode);
        Assert.Equal(
            "dependency_cycle",
            (await cycle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var deleted = await _client.DeleteAsync(
            $"/api/projects/{slug}/tickets/{blockedId}/dependencies/{blockerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var afterDelete = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{slug}/tickets/{blockedId}/dependencies");
        Assert.Empty(afterDelete.GetProperty("blockedBy").EnumerateArray());
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slug")
            .GetString()!;
    }

    private async Task<int> CreateTicketAsync(string slug, string title)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets",
            new CreateTicketRequest(title, "owner", "Backlog"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
    }
}

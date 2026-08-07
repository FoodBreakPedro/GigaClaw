using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GigaClaw.Web.Api;

namespace GigaClaw.Core.Tests.Api;

/// <summary>
/// Contract tests for POST /api/projects/{slug}/tickets on partial payloads. A request that omits
/// <c>status</c> used to pass the DTO's null straight through to the store and surface the
/// resulting <c>NOT NULL constraint failed: Tickets.Status</c> as a 500 with an EF stack trace.
/// </summary>
[Collection("ApiHost")] // see ApiHostCollection: GIGACLAW_DATA_DIR is process-global
public sealed class TicketCreationApiTests :
    IClassFixture<EndpointsRefactorTests.ApiFactory>,
    IDisposable
{
    private readonly HttpClient _client;

    public TicketCreationApiTests(EndpointsRefactorTests.ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CreateTicket_WithoutStatus_DefaultsToFirstColumn()
    {
        var slug = await CreateProjectAsync();

        var response = await PostJsonAsync(slug, """{"title":"No status supplied","createdBy":"owner"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(await FirstColumnNameAsync(slug), ticket.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateTicket_WithNullStatus_DefaultsToFirstColumn()
    {
        var slug = await CreateProjectAsync();

        var response = await PostJsonAsync(slug, """{"title":"Null status","createdBy":"owner","status":null}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(await FirstColumnNameAsync(slug), ticket.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateTicket_WithExplicitStatus_KeepsIt()
    {
        var slug = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Explicit status", "owner", "Todo"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Todo", ticket.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateTicket_WithoutCreatedBy_ReturnsEnglishBadRequest()
    {
        var slug = await CreateProjectAsync();

        var response = await PostJsonAsync(slug, """{"title":"No author"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Equal("The 'createdBy' field is required.", error);
    }

    [Fact]
    public async Task CreateTicket_WithoutTitle_ReturnsBadRequestWithoutLeakingInternals()
    {
        var slug = await CreateProjectAsync();

        // Title is NOT NULL in the store; the insert throws DbUpdateException.
        var response = await PostJsonAsync(slug, """{"createdBy":"owner"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqliteException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at GigaClaw.", body, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> PostJsonAsync(string slug, string json) =>
        _client.PostAsync(
            $"/api/projects/{slug}/tickets",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<string> CreateProjectAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectRequest("ticket-create-api-" + Guid.NewGuid().ToString("N")));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slug")
            .GetString()!;
    }

    private async Task<string> FirstColumnNameAsync(string slug)
    {
        var columns = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/columns");
        return columns.EnumerateArray().First().GetProperty("name").GetString()!;
    }
}

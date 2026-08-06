using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GigaClaw.Web.Api;

namespace GigaClaw.Core.Tests.Api;

[Collection("ApiHost")]
public sealed class TicketDeliverableTypeApiTests :
    IClassFixture<EndpointsRefactorTests.ApiFactory>,
    IDisposable
{
    private readonly HttpClient _client;

    public TicketDeliverableTypeApiTests(EndpointsRefactorTests.ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task TicketEndpoints_RoundTripDeliverableTypeAcrossCreateUpdateListAndDetail()
    {
        var slug = await CreateProjectAsync("deliverable-api-" + Guid.NewGuid().ToString("N"));

        var create = await _client.PostAsJsonAsync(
            $"/api/projects/{slug}/tickets",
            new CreateTicketRequest(
                "Ship the newsletter",
                "owner",
                "Backlog",
                DeliverableType: "email-newsletter"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var ticketId = created.GetProperty("id").GetInt32();
        Assert.Equal("email-newsletter", created.GetProperty("deliverableType").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets");
        var summary = Assert.Single(list.EnumerateArray());
        Assert.Equal("email-newsletter", summary.GetProperty("deliverableType").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{ticketId}");
        Assert.Equal("email-newsletter", detail.GetProperty("deliverableType").GetString());

        var update = await _client.PatchAsJsonAsync(
            $"/api/projects/{slug}/tickets/{ticketId}",
            new UpdateTicketRequest("owner", DeliverableType: ""));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(updated.GetProperty("deliverableType").ValueKind is JsonValueKind.Null);

        var updatedDetail = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{ticketId}");
        Assert.True(updatedDetail.GetProperty("deliverableType").ValueKind is JsonValueKind.Null);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slug")
            .GetString()!;
    }
}

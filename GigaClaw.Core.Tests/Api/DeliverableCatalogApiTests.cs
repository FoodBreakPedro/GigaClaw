using System.Net.Http.Json;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Tests.Api;

[Collection("ApiHost")]
public sealed class DeliverableCatalogApiTests :
    IClassFixture<EndpointsRefactorTests.ApiFactory>,
    IDisposable
{
    private readonly HttpClient _client;

    public DeliverableCatalogApiTests(EndpointsRefactorTests.ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task GetDeliverables_ReturnsTheCanonicalCatalogInDisplayOrder()
    {
        var actual = await _client.GetFromJsonAsync<List<DeliverableDefinition>>("/api/deliverables");

        Assert.NotNull(actual);
        Assert.Equal(DeliverableCatalog.GetAll(), actual);
    }
}

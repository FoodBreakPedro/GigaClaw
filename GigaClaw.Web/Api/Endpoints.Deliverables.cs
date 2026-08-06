using GigaClaw.Core.Models;

namespace GigaClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapDeliverables(RouteGroupBuilder api)
    {
        api.MapGet("/deliverables", () => Results.Ok(DeliverableCatalog.GetAll()))
            .WithTags("Deliverables")
            .Produces<IReadOnlyList<DeliverableDefinition>>();
    }
}

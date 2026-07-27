using System.Text.Json;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;

namespace GigaClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapMedia(RouteGroupBuilder api)
    {
        var media = api.MapGroup("/projects/{slug}/media").WithTags("Local Media");

        media.MapGet("/jobs", async (
            string slug,
            LocalMediaJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListAsync(slug, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        }).Produces<IReadOnlyList<LocalMediaJob>>();

        media.MapGet("/jobs/{id}", async (
            string slug,
            string id,
            LocalMediaJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var job = await service.GetAsync(slug, id, cancellationToken);
                return job is null
                    ? Results.NotFound(new { error = "Local media job not found." })
                    : Results.Ok(job);
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        }).Produces<LocalMediaJob>().ProducesProblem(404);

        media.MapPost("/jobs", async (
            string slug,
            CreateLocalMediaJobRequest request,
            LocalMediaJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await service.CreateAsync(slug, request, cancellationToken);
                return result.Created
                    ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
                    : Results.Ok(result);
            }
            catch (FileNotFoundException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (JsonException exception)
            {
                return Results.BadRequest(new { error = $"Invalid execution spec JSON: {exception.Message}" });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Produces<CreateLocalMediaJobResult>(202).Produces<CreateLocalMediaJobResult>()
          .ProducesProblem(400);

        media.MapPost("/jobs/{id}/cancel", async (
            string slug,
            string id,
            LocalMediaJobActionRequest request,
            LocalMediaJobService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Author))
                return Results.BadRequest(new { error = "The 'author' field is required." });
            try
            {
                return Results.Ok(await service.CancelAsync(slug, id, request.Author, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).Produces<LocalMediaJob>().ProducesProblem(400).ProducesProblem(409);

        media.MapPost("/jobs/{id}/review", async (
            string slug,
            string id,
            ReviewLocalMediaJobRequest request,
            LocalMediaJobService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Author))
                return Results.BadRequest(new { error = "The 'author' field is required." });
            try
            {
                return Results.Ok(await service.ReviewAsync(
                    slug,
                    id,
                    request.Decision,
                    request.Author,
                    cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).Produces<LocalMediaJob>().ProducesProblem(400).ProducesProblem(409);
    }
}

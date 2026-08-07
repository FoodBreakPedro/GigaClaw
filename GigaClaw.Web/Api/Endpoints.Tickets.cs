using Microsoft.EntityFrameworkCore;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Web.Services;

namespace GigaClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapTickets(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets", async (string slug, string? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, int? parentId, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search, parentId)))
            .WithTags("Tickets")
            .Produces<List<TicketSummary>>();

        api.MapPost("/projects/{slug}/tickets", async (string slug, CreateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.CreateTicketAsync(slug, req.Title, req.Description, req.CreatedBy, req.Status, req.LabelIds, req.Priority, req.AssignedTo, req.ParentId, req.DeliverableType);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DbUpdateException)
            {
                // A store constraint violation on insert means the payload was incomplete
                // (e.g. a null title). Report it as bad input — the EF/SQLite exception would
                // otherwise reach the client as a 500 with a stack trace exposing internals.
                return Results.BadRequest(new { error = "The ticket could not be created: one or more required fields are missing or invalid." });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}", async (string slug, int id, UpdateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.UpdateTicketAsync(slug, id, req.Title, req.Description, req.Author, req.Priority, req.AssignedTo, req.DeliverableType);
                if (ticket is not null && req.LabelIds is not null)
                    await ts.SetTicketLabelsAsync(slug, id, req.LabelIds);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/projects/{slug}/tickets/{id:int}/dependencies", async (string slug, int id, TicketService ts) =>
        {
            var dependencies = await ts.GetTicketDependenciesAsync(slug, id);
            return dependencies is null ? Results.NotFound() : Results.Ok(dependencies);
        }).WithTags("Tickets")
        .Produces<TicketDependencies>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/projects/{slug}/tickets/{id:int}/dependencies", async (
            string slug,
            int id,
            AddTicketDependencyRequest req,
            TicketService ts,
            BoardUpdateNotifier notifier) =>
        {
            try
            {
                var blocker = await ts.AddTicketDependencyAsync(slug, id, req.BlockingTicketId);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created(
                    $"/api/projects/{slug}/tickets/{id}/dependencies",
                    blocker);
            }
            catch (TicketDependencyException ex)
            {
                return DependencyError(ex);
            }
        }).WithTags("Tickets")
        .Produces<TicketDependencyInfo>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/projects/{slug}/tickets/{id:int}/dependencies/{blockingTicketId:int}", async (
            string slug,
            int id,
            int blockingTicketId,
            TicketService ts,
            BoardUpdateNotifier notifier) =>
        {
            var deleted = await ts.RemoveTicketDependencyAsync(slug, id, blockingTicketId);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/status", async (string slug, int id, MoveTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.MoveTicketAsync(slug, id, req.Status, req.Author);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/transition", async (string slug, int id, TransitionTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.TransitionTicketAsync(
                    slug,
                    id,
                    req.Status,
                    req.AssignedTo,
                    req.Author,
                    req.ExpectedStatus);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (TicketTransitionConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/schedule", async (string slug, int id, ScheduleTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.ScheduleTicketAsync(slug, id, req.FireAt, req.TargetStatus ?? "Todo", req.Author);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var deleted = await ts.DeleteTicketAsync(slug, id);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // Sub-tickets
        api.MapPut("/projects/{slug}/tickets/{id:int}/parent", async (string slug, int id, SetParentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.SetParentAsync(slug, id, req.ParentId);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = "Cannot link this sub-ticket." });
        }).WithTags("Tickets");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/parent", async (string slug, int id, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.UnparentAsync(slug, id);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets");

        // Comments
        api.MapPost("/projects/{slug}/tickets/{id:int}/comments", async (string slug, int id, AddCommentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var comment = await ts.AddCommentAsync(slug, id, req.Content, req.Author);
                if (comment is not null) notifier.NotifyProjectUpdated(slug);
                return comment is null ? Results.NotFound() : Results.Created($"/api/projects/{slug}/tickets/{id}", comment);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Comments");

        api.MapPatch("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", async (string slug, int id, int commentId, UpdateCommentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ok = await ts.UpdateCommentAsync(slug, id, commentId, req.Content, req.Author);
                if (ok) notifier.NotifyProjectUpdated(slug);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Comments");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", async (string slug, int id, int commentId, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.DeleteCommentAsync(slug, id, commentId);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Comments");

        // Activity
        api.MapGet("/projects/{slug}/tickets/{id:int}/activity", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            if (ticket is null) return Results.NotFound();
            var timeline = ticket.Comments
                .Select(c => new { at = c.CreatedAt, author = c.Author, type = "comment", text = c.Content, id = (int?)c.Id })
                .Cast<object>()
                .Concat(ticket.Activities
                    .Select(a => new { at = a.CreatedAt, author = a.Author, type = "event", text = a.Text, id = (int?)null })
                    .Cast<object>())
                .OrderBy(x => ((dynamic)x).at);
            return Results.Ok(timeline);
        }).WithTags("Activity");
    }

    private static IResult DependencyError(TicketDependencyException exception)
    {
        var statusCode = exception.Code switch
        {
            "ticket_not_found" or "blocking_ticket_not_found" => StatusCodes.Status404NotFound,
            "dependency_duplicate" or "dependency_cycle" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(
            new { code = exception.Code, error = exception.Message },
            statusCode: statusCode);
    }

    private static void MapTicketReorder(RouteGroupBuilder api)
    {
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", async (string slug, int id, ReorderTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            await ts.ReorderTicketAsync(slug, id, req.Status, req.Index);
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }).WithTags("Tickets");
    }
}

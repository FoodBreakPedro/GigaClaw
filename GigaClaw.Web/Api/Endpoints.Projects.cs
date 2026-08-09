using GigaClaw.Core.Services;
using GigaClaw.Core.Packs;

namespace GigaClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapProjects(RouteGroupBuilder api)
    {
        api.MapGet("/projects", async (ProjectService ps) =>
            Results.Ok(await ps.ListProjectsAsync()))
            .WithTags("Projects");

        api.MapPost("/projects", async (CreateProjectRequest req, ProjectService ps) =>
        {
            var project = await ps.CreateProjectAsync(req.Name);
            return Results.Created($"/api/projects/{project.Slug}", project);
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapDelete("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var deleted = await ps.DeleteProjectAsync(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}", async (string slug, UpdateProjectRequest req, ProjectService ps) =>
        {
            var project = await ps.UpdateProjectAsync(slug, req.WorkspacePath, req.FallbackModel, req.UpdateFallbackModel);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapPost("/projects/{slug}/initialize", async (string slug, InitializeProjectRequest? req, ProjectService ps, AgentsTemplateService template, MemberService members, GigaClaw.Core.Automation.AutomationEngine engine) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var workspace = ps.ResolveWorkspacePath(project);
            var result = await template.InitializeAsync(workspace, req?.OverwriteConflicts ?? false);

            // Shared with Home.razor's EnsureAgentMembersAsync so both paths seed AD-9
            // DefaultModel identically — see AgentsTemplateService.EnsureAgentMembersAsync.
            var membersCreated = await template.EnsureAgentMembersAsync(slug, members);

            await engine.ReloadProjectAsync(slug);
            return Results.Ok(new
            {
                written = result.Written,
                skipped = result.Skipped,
                gitInit = result.GitInit.ToString(),
                membersCreated,
            });
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}/agent-templates/sync", async (string slug, ProjectService ps, AgentTemplateSyncService sync) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var workspace = ps.ResolveWorkspacePath(project);
            return Results.Ok(await sync.PreviewAsync(workspace));
        }).WithTags("Projects");

        api.MapPost("/projects/{slug}/agent-templates/sync", async (
            string slug,
            ApplyAgentTemplateSyncRequest req,
            ProjectService ps,
            AgentTemplateSyncService sync,
            AgentsTemplateService template,
            MemberService members,
            GigaClaw.Core.Automation.AutomationEngine engine) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var workspace = ps.ResolveWorkspacePath(project);
            AgentTemplateSyncResult result;
            try
            {
                result = await sync.ApplyAsync(workspace, req.PlanToken);
            }
            catch (AgentTemplateSyncPlanChangedException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var membersCreated = result.Plan.CanApply
                ? await template.EnsureAgentMembersAsync(slug, members)
                : [];
            var automationsReloaded = result.AppliedPaths.Contains(".agents/automations.json", StringComparer.Ordinal);
            var workflowReloaded = result.AppliedPaths.Contains(".agents/workflow.json", StringComparer.Ordinal);
            if (automationsReloaded || workflowReloaded)
            {
                await engine.ReloadProjectAsync(slug);
            }

            return Results.Ok(new AgentTemplateSyncApplyResponse(
                result.Plan,
                result.AppliedPaths,
                membersCreated,
                automationsReloaded,
                workflowReloaded));
        }).WithTags("Projects");

        api.MapPost("/projects/{slug}/pause", async (string slug, ProjectService ps) =>
        {
            var project = await ps.TogglePauseAsync(slug);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}/local-model", async (string slug, SaveLocalModelConfigRequest req, ProjectService ps) =>
        {
            var project = await ps.SaveLocalModelConfigAsync(slug, req.LocalModelBaseUrl, req.LocalModelName);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");
    }
}

using GigaClaw.Core.Github;
using GigaClaw.Core.Services;

namespace GigaClaw.Web.Api;

public static partial class Endpoints
{
    /// <summary>
    /// C7/U5 GitHub surface. Configuration and a manual sync trigger; the automatic path is the
    /// automation vocabulary (see <c>doc/github-surface.md</c>).
    /// <para>
    /// No route on this group ever returns the personal access token. <see cref="GitHubConfigDto"/>
    /// carries <c>tokenConfigured</c> instead, so a UI can show "connected" without the secret
    /// crossing the wire, and a blank token on PUT keeps the stored one.
    /// </para>
    /// </summary>
    private static void MapGitHub(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/github", (string slug, AppSettingsService settings) =>
        {
            var config = settings.GetGitHubConfig(slug);
            return Results.Ok(ToDto(slug, config, settings));
        }).WithTags("GitHub");

        api.MapPut("/projects/{slug}/github", async (
            string slug, SaveGitHubConfigRequest req, ProjectService projects, AppSettingsService settings) =>
        {
            if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();

            var config = new GitHubProjectConfig
            {
                Enabled = req.Enabled,
                Owner = req.Owner ?? "",
                Repo = req.Repo ?? "",
                ImportLabel = string.IsNullOrWhiteSpace(req.ImportLabel) ? "gigaclaw" : req.ImportLabel,
                ImportStatus = string.IsNullOrWhiteSpace(req.ImportStatus) ? "Backlog" : req.ImportStatus,
                CommentOnIssueWhenTicketDone = req.CommentOnIssueWhenTicketDone,
                CloseIssueWhenTicketDone = req.CloseIssueWhenTicketDone,
                DoneStatuses = req.DoneStatuses is { Count: > 0 } ? req.DoneStatuses : ["Done"],
                OwnerLogins = req.OwnerLogins ?? [],
                ApiBaseUrl = string.IsNullOrWhiteSpace(req.ApiBaseUrl) ? "https://api.github.com" : req.ApiBaseUrl,
            };
            settings.ConfigureGitHub(slug, config, req.Token);
            return Results.Ok(ToDto(slug, settings.GetGitHubConfig(slug), settings));
        }).WithTags("GitHub");

        api.MapDelete("/projects/{slug}/github", (string slug, AppSettingsService settings) =>
        {
            settings.RemoveGitHubConfig(slug);
            return Results.NoContent();
        }).WithTags("GitHub");

        api.MapPost("/projects/{slug}/github/sync", async (
            string slug, ProjectService projects, GitHubIssueSyncService sync, CancellationToken ct) =>
        {
            if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();
            var result = await sync.SyncAsync(slug, ct);
            return Results.Ok(result);
        }).WithTags("GitHub");

        api.MapGet("/projects/{slug}/github/links", async (
            string slug, AppSettingsService settings, GitHubIssueLinkStore links) =>
        {
            var repository = settings.GetGitHubConfig(slug)?.RepositoryKey;
            return Results.Ok(await links.ListAsync(slug, repository));
        }).WithTags("GitHub");
    }

    private static GitHubConfigDto ToDto(string slug, GitHubProjectConfig? config, AppSettingsService settings) =>
        new(
            Configured: config is not null,
            Enabled: config?.Enabled ?? false,
            Owner: config?.Owner ?? "",
            Repo: config?.Repo ?? "",
            ImportLabel: config?.ImportLabel ?? "gigaclaw",
            ImportStatus: config?.ImportStatus ?? "Backlog",
            CommentOnIssueWhenTicketDone: config?.CommentOnIssueWhenTicketDone ?? false,
            CloseIssueWhenTicketDone: config?.CloseIssueWhenTicketDone ?? false,
            DoneStatuses: config?.DoneStatuses.ToList() ?? ["Done"],
            OwnerLogins: config?.OwnerLogins.ToList() ?? [],
            ApiBaseUrl: config?.ApiBaseUrl ?? "https://api.github.com",
            TokenConfigured: settings.GitHubTokenConfigured(slug));
}

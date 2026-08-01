using System.Text.Json;
using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Github;

/// <summary>What one sync pass did. Every number is derivable from the mapping table afterwards.</summary>
public sealed record GitHubSyncResult(
    bool Ran,
    string? Reason = null,
    int Imported = 0,
    int Updated = 0,
    int Unchanged = 0,
    int ClosedIssues = 0,
    int CommentedIssues = 0,
    bool DryRun = false)
{
    public static GitHubSyncResult Skipped(string reason) => new(false, reason);
    public static GitHubSyncResult Refused(string reason) => new(false, reason, DryRun: true);
}

/// <summary>
/// C7 part 1: labeled GitHub issues become tickets, and finished tickets optionally answer back.
/// <para>
/// <b>Poll, not webhook.</b> GigaClaw is a local app behind whatever NAT the owner happens to be
/// on; a webhook listener would need an inbound route that most installs cannot provide and that
/// none of them should have to open. The pass is therefore a pure function of "what GitHub says
/// now" plus <see cref="GitHubIssueLinkStore"/>, which is also what makes it safe to run twice.
/// </para>
/// <para>
/// <b>Idempotence.</b> An issue is imported only when it has no row in the link table. On every
/// later pass the row is found and the ticket is updated in place, so a re-sync, an engine
/// restart, or two owners hitting the endpoint at once converge on exactly one ticket per issue.
/// </para>
/// </summary>
public sealed class GitHubIssueSyncService
{
    private readonly AppSettingsService _settings;
    private readonly GitHubApiClient _client;
    private readonly GitHubIssueLinkStore _links;
    private readonly TicketService _tickets;
    private readonly ILogger<GitHubIssueSyncService> _logger;

    public GitHubIssueSyncService(
        AppSettingsService settings,
        GitHubApiClient client,
        GitHubIssueLinkStore links,
        TicketService tickets,
        ILogger<GitHubIssueSyncService> logger)
    {
        _settings = settings;
        _client = client;
        _links = links;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>
    /// Runs one import pass followed by the closure round trip. Returns rather than throws for
    /// every "not configured" case: a project that never opted in must cost nothing and say so.
    /// </summary>
    public async Task<GitHubSyncResult> SyncAsync(string slug, CancellationToken ct = default)
    {
        var config = _settings.GetGitHubConfig(slug);
        if (config is null || !config.Enabled)
            return GitHubSyncResult.Skipped("GitHub integration is not enabled for this project.");
        if (!config.HasRepository)
            return GitHubSyncResult.Skipped("No GitHub repository configured (owner/repo).");
        if (string.IsNullOrWhiteSpace(config.ImportLabel))
            return GitHubSyncResult.Skipped("No import label configured — nothing would be selected.");

        var token = _settings.GetGitHubToken(slug);
        if (string.IsNullOrWhiteSpace(token))
            return GitHubSyncResult.Skipped("No GitHub token configured for this project.");

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/issues"
                + $"?state=all&per_page=100&labels={Uri.EscapeDataString(config.ImportLabel)}";

        var response = await _client.SendAsync(
            new GitHubRequest(slug, HttpMethod.Get, url, token, Actor: "github-issue-sync"), ct);

        if (response.DryRun)
            return GitHubSyncResult.Refused(response.Error ?? "outbound request refused by policy.");
        if (!response.Success)
            return GitHubSyncResult.Skipped(response.Error ?? "GitHub request failed.");

        List<GitHubIssue> issues;
        try { issues = ParseIssues(response.Body); }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "[{Slug}] GitHub issue list was not readable JSON", slug);
            return GitHubSyncResult.Skipped("GitHub returned a response that could not be parsed.");
        }

        int imported = 0, updated = 0, unchanged = 0;
        var now = DateTime.UtcNow;

        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();

            var link = await _links.GetAsync(slug, config.RepositoryKey, issue.Number);
            if (link is null)
            {
                var ticket = await _tickets.CreateTicketAsync(
                    slug,
                    title: issue.Title,
                    description: RenderDescription(issue),
                    createdBy: "github",
                    status: config.ImportStatus);
                await _links.UpsertAsync(slug, new GitHubIssueLink(
                    config.RepositoryKey, issue.Number, ticket.Id, issue.State, issue.UpdatedAtUtc, now, RoundTripDone: false));
                imported++;
                continue;
            }

            // The ticket the link points at may have been deleted by the owner. Re-importing would
            // resurrect it on every poll forever, so the link is refreshed and the issue left alone
            // — a deliberate delete stays deleted.
            var existing = await _tickets.GetTicketAsync(slug, link.TicketId);
            if (existing is null)
            {
                await _links.UpsertAsync(slug, link with { LastSyncedAtUtc = now });
                unchanged++;
                continue;
            }

            var isNewer = issue.UpdatedAtUtc is not null
                && (link.IssueUpdatedAtUtc is null || issue.UpdatedAtUtc > link.IssueUpdatedAtUtc);
            if (!isNewer)
            {
                await _links.UpsertAsync(slug, link with { IssueState = issue.State, LastSyncedAtUtc = now });
                unchanged++;
                continue;
            }

            await _tickets.UpdateTicketAsync(
                slug, link.TicketId, title: issue.Title, description: RenderDescription(issue), author: "github");
            await _links.UpsertAsync(slug, link with
            {
                IssueState = issue.State,
                IssueUpdatedAtUtc = issue.UpdatedAtUtc,
                LastSyncedAtUtc = now,
            });
            updated++;
        }

        var (closed, commented) = await RunClosureRoundTripAsync(slug, config, token!, ct);
        return new GitHubSyncResult(true, null, imported, updated, unchanged, closed, commented);
    }

    /// <summary>
    /// The other half of the round trip: a ticket that reached a done status optionally comments on
    /// and/or closes its issue, exactly once. "Exactly once" is
    /// <see cref="GitHubIssueLink.RoundTripDone"/> — without it every poll after the ticket is done
    /// would post another comment.
    /// </summary>
    private async Task<(int Closed, int Commented)> RunClosureRoundTripAsync(
        string slug, GitHubProjectConfig config, string token, CancellationToken ct)
    {
        if (!config.CommentOnIssueWhenTicketDone && !config.CloseIssueWhenTicketDone)
            return (0, 0);

        var doneStatuses = new HashSet<string>(config.DoneStatuses, StringComparer.OrdinalIgnoreCase);
        int closed = 0, commented = 0;

        foreach (var link in await _links.ListAsync(slug, config.RepositoryKey))
        {
            ct.ThrowIfCancellationRequested();
            if (link.RoundTripDone) continue;

            var ticket = await _tickets.GetTicketAsync(slug, link.TicketId);
            if (ticket is null || !doneStatuses.Contains(ticket.Status)) continue;

            var issueUrl = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/issues/{link.IssueNumber}";
            var anySent = false;

            if (config.CommentOnIssueWhenTicketDone)
            {
                var body = JsonSerializer.Serialize(new
                {
                    body = $"Closed by GigaClaw: ticket #{ticket.Id} ({ticket.Title}) reached **{ticket.Status}**.",
                });
                var reply = await _client.SendAsync(new GitHubRequest(
                    slug, HttpMethod.Post, $"{issueUrl}/comments", token, body, link.TicketId, "github-issue-sync"), ct);
                if (reply.DryRun) continue;   // policy refused; retry on a later pass once approved
                if (reply.Success) { commented++; anySent = true; }
            }

            if (config.CloseIssueWhenTicketDone)
            {
                var body = JsonSerializer.Serialize(new { state = "closed" });
                var reply = await _client.SendAsync(new GitHubRequest(
                    slug, HttpMethod.Patch, issueUrl, token, body, link.TicketId, "github-issue-sync"), ct);
                if (reply.DryRun) continue;
                if (reply.Success) { closed++; anySent = true; }
            }

            if (anySent)
                await _links.MarkRoundTripDoneAsync(slug, config.RepositoryKey, link.IssueNumber);
        }

        return (closed, commented);
    }

    /// <summary>
    /// The imported ticket body: the issue's own text plus a provenance footer. The footer carries
    /// the issue's public URL and nothing else — no token, and no header the request needed.
    /// </summary>
    internal static string RenderDescription(GitHubIssue issue)
    {
        var body = string.IsNullOrWhiteSpace(issue.Body) ? "_(the issue has no description)_" : issue.Body!.Trim();
        return $"{body}\n\n---\nImported from GitHub issue [#{issue.Number}]({issue.HtmlUrl}).";
    }

    /// <summary>
    /// Reads the issues array. <c>/issues</c> returns pull requests too — they carry a
    /// <c>pull_request</c> member — and importing one as a ticket would make a PR look like a work
    /// item, so they are dropped here rather than filtered downstream.
    /// </summary>
    internal static List<GitHubIssue> ParseIssues(string json)
    {
        using var document = JsonDocument.Parse(json);
        var issues = new List<GitHubIssue>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return issues;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (element.TryGetProperty("pull_request", out _)) continue;
            if (!element.TryGetProperty("number", out var number) || !number.TryGetInt32(out var issueNumber)) continue;

            issues.Add(new GitHubIssue(
                Number: issueNumber,
                Title: Str(element, "title") ?? $"GitHub issue #{issueNumber}",
                Body: Str(element, "body"),
                State: Str(element, "state") ?? "open",
                HtmlUrl: Str(element, "html_url") ?? "",
                UpdatedAtUtc: Time(element, "updated_at")));
        }
        return issues;

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        static DateTime? Time(JsonElement element, string name) =>
            Str(element, name) is string raw
            && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
    }
}

/// <summary>The subset of a GitHub issue the import actually uses.</summary>
public sealed record GitHubIssue(
    int Number,
    string Title,
    string? Body,
    string State,
    string HtmlUrl,
    DateTime? UpdatedAtUtc);

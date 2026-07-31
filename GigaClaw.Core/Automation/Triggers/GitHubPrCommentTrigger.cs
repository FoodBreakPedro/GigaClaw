using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Github;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>
/// C7 part 2. Polls pull-request review comments and fires once per ticket whose PR just received
/// a comment from a configured owner login, after recording that comment on the ticket as
/// <c>github-owner-feedback/v1</c> — which is what <c>ActionExecutor.ComposeDispatchContextAsync</c>
/// injects into the re-dispatch. Composing with the ordinary condition vocabulary (assignedTo,
/// ticketInColumn, labels…) is the point of making it a trigger rather than a bespoke hook.
/// <para>
/// <b>Fails closed.</b> No configured owner login means no comment qualifies. The owner list lives
/// in the app-level settings.json, so an agent that can write to the repository still cannot make
/// itself an owner and steer its own next dispatch.
/// </para>
/// <para>
/// <b>Dedupe.</b> A per-automation cursor of the highest comment id seen, in the workspace's
/// dispatch-state.json — the same mechanism <see cref="TicketCommentAddedTrigger"/> uses, and for
/// the same reason: two automations watching the same repository must not swallow each other's
/// comments. The cursor is advanced eagerly, but the steering itself is already durable on the
/// ticket by then, so a chain that fails afterwards loses the dispatch, never the feedback.
/// </para>
/// </summary>
public sealed class GitHubPrCommentTrigger : ITrigger
{
    private readonly GitHubPrCommentTriggerSpec _spec;
    private readonly GitHubTriggerServices _services;
    private DateTime _lastPolled = DateTime.MinValue;

    public GitHubPrCommentTrigger(GitHubPrCommentTriggerSpec spec, GitHubTriggerServices services)
    {
        _spec = spec;
        _services = services;
    }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return [];
        _lastPolled = ctx.Now;

        var config = _services.Settings.GetGitHubConfig(ctx.ProjectSlug);
        if (config is null || !config.Enabled || !config.HasRepository) return [];

        var token = _services.Settings.GetGitHubToken(ctx.ProjectSlug);
        if (string.IsNullOrWhiteSpace(token)) return [];

        // The spec may narrow the project's owner list, never widen it: an automation is
        // agent-editable config, and widening there would be a way to grant owner authority.
        var owners = ResolveOwnerLogins(config, _spec.OwnerLogins);
        if (owners.Count == 0) return [];

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/pulls/comments"
                + "?sort=created&direction=desc&per_page=100";

        var response = await _services.Client.SendAsync(
            new GitHubRequest(ctx.ProjectSlug, HttpMethod.Get, url, token, Actor: "github-pr-feedback"), ct);
        if (!response.Success) return [];   // dry-run or failure: nothing fires, the receipt is the record

        List<PrComment> comments;
        try { comments = ParseComments(response.Body); }
        catch (JsonException) { return []; }

        var cursor = LoadCursor(ctx);
        var newestSeen = cursor;
        var firings = new List<TriggerFiring>();
        var firedTickets = new HashSet<int>();

        foreach (var comment in comments.OrderBy(c => c.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (comment.Id <= cursor) continue;
            newestSeen = Math.Max(newestSeen, comment.Id);
            if (!owners.Contains(comment.Author)) continue;

            var ticketId = await ResolveTicketAsync(ctx, config, token!, comment, ct);
            if (ticketId is null) continue;

            var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, ticketId.Value);
            if (ticket is null) continue;

            // Durable first, dispatch second: the feedback is on the ticket before anything can
            // fail, so it is still injected on a later dispatch even if this chain never runs.
            await ctx.Tickets.AddCommentAsync(
                ctx.ProjectSlug,
                ticket.Id,
                OwnerFeedback.RenderComment(new OwnerFeedbackItem(
                    comment.Id, comment.PullRequestNumber, comment.Author, comment.Body, comment.HtmlUrl, comment.CreatedAtUtc)),
                "github");

            // One firing per ticket per poll: three comments on the same PR are one re-dispatch
            // that must address all three, not three competing runs on the same files.
            if (firedTickets.Add(ticket.Id))
                firings.Add(new TriggerFiring(ticket.Id, ticket.Title, ticket.Status));
        }

        if (newestSeen > cursor) SaveCursor(ctx, newestSeen);
        return firings;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);

    /// <summary>
    /// The effective owner set: the project's configured logins, optionally narrowed by the
    /// automation's own list. Intersection, never union — see the class remarks.
    /// </summary>
    internal static HashSet<string> ResolveOwnerLogins(
        GitHubProjectConfig config, IReadOnlyList<string> specLogins)
    {
        var configured = new HashSet<string>(
            config.OwnerLogins.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (specLogins.Count == 0) return configured;
        configured.IntersectWith(specLogins.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()));
        return configured;
    }

    /// <summary>
    /// Which ticket a PR comment steers. Three sources, cheapest first:
    /// an explicit <c>ticket-&lt;id&gt;</c> in the comment itself, then the pull request's own
    /// branch name / title / body, then the issue the PR closes, resolved through the C7 part-1
    /// link table. Nothing found means nothing fires — guessing a ticket would re-dispatch an
    /// agent onto work the comment was never about.
    /// </summary>
    private async Task<int?> ResolveTicketAsync(
        TriggerContext ctx, GitHubProjectConfig config, string token, PrComment comment, CancellationToken ct)
    {
        if (TicketReference.Find(comment.Body) is int fromComment) return fromComment;

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/pulls/{comment.PullRequestNumber}";
        var response = await _services.Client.SendAsync(
            new GitHubRequest(ctx.ProjectSlug, HttpMethod.Get, url, token, Actor: "github-pr-feedback"), ct);
        if (!response.Success) return null;

        string? branch, title, body;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            branch = root.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object
                ? Str(head, "ref") : null;
            title = Str(root, "title");
            body = Str(root, "body");
        }
        catch (JsonException) { return null; }

        foreach (var candidate in new[] { branch, title, body })
            if (TicketReference.Find(candidate) is int fromPr) return fromPr;

        // Last resort: the issue the PR closes, if that issue was imported as a ticket.
        foreach (var issueNumber in TicketReference.IssueReferences(body).Concat(TicketReference.IssueReferences(title)))
        {
            var link = await _services.Links.GetAsync(ctx.ProjectSlug, config.RepositoryKey, issueNumber);
            if (link is not null) return link.TicketId;
        }

        return null;

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
    }

    // ── cursor ──────────────────────────────────────────────────────────────

    private static string CursorKey(TriggerContext ctx) => "_githubPrComments:" + ctx.Automation.Id;

    private static long LoadCursor(TriggerContext ctx)
    {
        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        return state[CursorKey(ctx)]?.GetValue<long>() ?? 0;
    }

    private static void SaveCursor(TriggerContext ctx, long value) =>
        ctx.Sessions.Update(ctx.WorkspacePath, state => state[CursorKey(ctx)] = JsonValue.Create(value));

    // ── parsing ─────────────────────────────────────────────────────────────

    internal sealed record PrComment(
        long Id, int PullRequestNumber, string Author, string Body, string HtmlUrl, DateTime CreatedAtUtc);

    internal static List<PrComment> ParseComments(string json)
    {
        using var document = JsonDocument.Parse(json);
        var comments = new List<PrComment>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return comments;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;

            var author = element.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? Str(user, "login") ?? "" : "";
            var pullRequestNumber = PullNumber(Str(element, "pull_request_url"))
                ?? PullNumber(Str(element, "html_url"))
                ?? 0;
            if (pullRequestNumber == 0) continue;

            comments.Add(new PrComment(
                Id: id,
                PullRequestNumber: pullRequestNumber,
                Author: author,
                Body: Str(element, "body") ?? "",
                HtmlUrl: Str(element, "html_url") ?? "",
                CreatedAtUtc: Str(element, "created_at") is string raw
                    && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var created)
                        ? created : DateTime.MinValue));
        }
        return comments;

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        static int? PullNumber(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/pulls?/(?<n>[0-9]+)");
            return match.Success && int.TryParse(match.Groups["n"].Value, out var n) ? n : null;
        }
    }
}

/// <summary>Recognises the ticket and issue references a PR or a comment can carry.</summary>
internal static class TicketReference
{
    private static readonly System.Text.RegularExpressions.Regex TicketRegex = new(
        @"\bticket[-\s#]?(?<id>[0-9]+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex IssueRegex = new(
        @"(?<!\w)#(?<n>[0-9]+)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The first <c>ticket-&lt;id&gt;</c> reference, or null.</summary>
    public static int? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = TicketRegex.Match(text);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    /// <summary>Every bare <c>#n</c> reference, in order — candidate GitHub issue numbers.</summary>
    public static IEnumerable<int> IssueReferences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (System.Text.RegularExpressions.Match match in IssueRegex.Matches(text))
            if (int.TryParse(match.Groups["n"].Value, out var n)) yield return n;
    }
}

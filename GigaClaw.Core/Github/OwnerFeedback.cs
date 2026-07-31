using System.Text;
using System.Text.Json;
using GigaClaw.Core.Automation.Handoffs;

namespace GigaClaw.Core.Github;

/// <summary>One pull-request comment written by a configured owner login.</summary>
public sealed record OwnerFeedbackItem(
    long CommentId,
    int PullRequestNumber,
    string Author,
    string Body,
    string HtmlUrl,
    DateTime CreatedAtUtc);

/// <summary>
/// C7 part 2: a PR review comment from the owner becomes steering input for the ticket's assignee.
/// <para>
/// <b>Why this is a ticket comment and not a private queue.</b> C3's repair loop already solved the
/// "re-dispatch an agent with something it must read first" problem, and it solved it by putting the
/// evidence on the ticket and re-deriving the state from the comment trail at dispatch time — see
/// <see cref="Automation.Verdicts.RepairLoop"/>. That makes the steering auditable (an owner can
/// read what the agent was told), restart-proof (the engine holds nothing to lose), and immune to a
/// replayed run inventing a second copy. Owner feedback follows the same mechanism rather than
/// adding a second, weaker one: the trigger posts a <c>github-owner-feedback/v1</c> comment, and
/// <c>ActionExecutor.ComposeDispatchContextAsync</c> renders the outstanding ones into the prompt
/// beside the repair brief and the handoff.
/// </para>
/// <para>
/// <b>What "outstanding" means.</b> Every feedback comment since the agent last reported back. A
/// handoff closes the episode exactly the way a SHIP closes a repair episode: the agent has
/// responded to what it was given, so the next dispatch starts from the ticket rather than
/// re-litigating answered comments forever.
/// </para>
/// </summary>
public static class OwnerFeedback
{
    /// <summary>Marker line that carries owner feedback in a ticket comment.</summary>
    public const string MarkerPrefix = "GIGACLAW-GH-FEEDBACK v1";

    public const string Schema = "github-owner-feedback/v1";

    private static readonly System.Text.RegularExpressions.Regex MarkerRegex = new(
        @"^GIGACLAW-GH-FEEDBACK\s+v1\s+pr-(?<pr>[0-9]+)\s+comment-(?<comment>[0-9]+)\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FenceRegex = new(
        "```json\\s*\\n(?<body>.*?)\\n```",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool ContainsMarker(string? commentBody)
        => commentBody is not null && MarkerRegex.IsMatch(commentBody);

    /// <summary>
    /// The ticket comment the trigger posts. Marker line plus a JSON block, the same shape the
    /// handoff and verdict contracts use — one parsing idiom across every structured receipt.
    /// </summary>
    public static string RenderComment(OwnerFeedbackItem item)
    {
        var json = JsonSerializer.Serialize(new
        {
            schema = Schema,
            commentId = item.CommentId,
            pullRequest = item.PullRequestNumber,
            author = item.Author,
            url = item.HtmlUrl,
            createdAtUtc = item.CreatedAtUtc.ToString("O"),
            body = item.Body,
        }, new JsonSerializerOptions { WriteIndented = true });

        return $"{MarkerPrefix} pr-{item.PullRequestNumber} comment-{item.CommentId}\n\n```json\n{json}\n```";
    }

    /// <summary>Reads one feedback comment back. An unreadable one is no feedback at all.</summary>
    public static bool TryRead(string? commentBody, out OwnerFeedbackItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(commentBody)) return false;
        if (!MarkerRegex.IsMatch(commentBody)) return false;

        var fence = FenceRegex.Match(commentBody);
        if (!fence.Success) return false;

        try
        {
            using var document = JsonDocument.Parse(fence.Groups["body"].Value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (Str(root, "schema") != Schema) return false;

            item = new OwnerFeedbackItem(
                CommentId: root.TryGetProperty("commentId", out var id) && id.TryGetInt64(out var commentId) ? commentId : 0,
                PullRequestNumber: root.TryGetProperty("pullRequest", out var pr) && pr.TryGetInt32(out var prNumber) ? prNumber : 0,
                Author: Str(root, "author") ?? "",
                Body: Str(root, "body") ?? "",
                HtmlUrl: Str(root, "url") ?? "",
                CreatedAtUtc: Str(root, "createdAtUtc") is string raw
                    && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                        ? created : DateTime.MinValue);
            return true;
        }
        catch (JsonException) { return false; }

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
    }

    /// <summary>
    /// The feedback the next dispatch must answer: everything posted since the agent last handed
    /// off. A handoff comment resets the list — the same "the episode is over" rule
    /// <see cref="Automation.Verdicts.RepairLoop"/> applies to a SHIP.
    /// </summary>
    public static IReadOnlyList<OwnerFeedbackItem> Outstanding(IReadOnlyList<string> commentsOldestFirst)
    {
        var outstanding = new List<OwnerFeedbackItem>();
        foreach (var comment in commentsOldestFirst)
        {
            if (HandoffReader.ContainsMarker(comment))
            {
                outstanding.Clear();
                continue;
            }
            if (TryRead(comment, out var item) && item is not null)
                outstanding.Add(item);
        }
        return outstanding;
    }

    /// <summary>
    /// The brief prepended to the re-dispatch, in the same "here is what you must address first"
    /// shape as <see cref="Automation.Verdicts.RepairLoop.RenderBrief"/>.
    /// </summary>
    public static string RenderBrief(IReadOnlyList<OwnerFeedbackItem> items, int ticketId)
    {
        if (items.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine(items.Count == 1
            ? $"[Owner feedback on ticket #{ticketId} — 1 unanswered pull-request comment]"
            : $"[Owner feedback on ticket #{ticketId} — {items.Count} unanswered pull-request comments]");
        sb.AppendLine("Address every point below before handing the work back.");

        foreach (var item in items)
        {
            sb.AppendLine();
            sb.AppendLine($"### {item.Author} on PR #{item.PullRequestNumber} ({item.CreatedAtUtc:yyyy-MM-dd HH:mm}Z)");
            if (!string.IsNullOrWhiteSpace(item.HtmlUrl))
                sb.AppendLine($"Source: {item.HtmlUrl}");
            sb.AppendLine(item.Body.Trim());
        }

        return sb.ToString().TrimEnd();
    }
}

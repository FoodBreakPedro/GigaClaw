using System.Text.Json.Nodes;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>Signal emitted by TicketService when a comment is added to a ticket.</summary>
public sealed record CommentAddedSignal(int TicketId, string Author, string Content);

/// <summary>
/// Fires when a new comment is added to any ticket, optionally filtered by author.
/// Persists the last-seen comment ID per ticket in dispatch-state.json under
/// "_lastCommentIds".
/// </summary>
public sealed class TicketCommentAddedTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private readonly TicketCommentAddedTriggerSpec _spec;

    public TicketCommentAddedTrigger(TicketCommentAddedTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        var lastSeen = LoadLastCommentIds(ctx);
        var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug);
        var firings = new List<TriggerFiring>();

        // Only inspect tickets that have comments (CommentCount > 0)
        foreach (var summary in tickets)
        {
            if (summary.CommentCount == 0) continue;

            var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, summary.Id);
            if (ticket is null) continue;

            lastSeen.TryGetValue(summary.Id, out var prevMaxId);

            foreach (var comment in ticket.Comments)
            {
                if (comment.Id <= prevMaxId) continue;
                if (_spec.Authors.Count > 0 && !_spec.Authors.Contains(comment.Author, StringComparer.OrdinalIgnoreCase))
                    continue;
                firings.Add(new TriggerFiring(ticket.Id, ticket.Title, ticket.Status));
                break; // one firing per ticket per poll
            }

            var maxId = ticket.Comments.Count > 0 ? ticket.Comments.Max(c => c.Id) : 0;
            if (maxId > prevMaxId)
                lastSeen[summary.Id] = maxId;
        }

        SaveLastCommentIds(ctx, lastSeen);
        return firings;
    }

    // One cursor per automation: two ticketCommentAdded automations polling the same
    // workspace must not advance each other's last-seen ids, or whichever polls first
    // swallows the comment for both. Falls back to the legacy shared key on first read
    // so existing installs don't re-fire on historical comments.
    private static string CursorKey(TriggerContext ctx) => "_lastCommentIds:" + ctx.Automation.Id;

    private static Dictionary<int, int> LoadLastCommentIds(TriggerContext ctx)
    {
        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var node = state[CursorKey(ctx)] as JsonObject ?? state["_lastCommentIds"] as JsonObject;
        var dict = new Dictionary<int, int>();
        if (node is null) return dict;
        foreach (var kv in node)
            if (int.TryParse(kv.Key, out var ticketId) && kv.Value is not null)
                dict[ticketId] = kv.Value.GetValue<int>();
        return dict;
    }

    public bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        if (signal is not CommentAddedSignal s)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        var matches = _spec.Authors.Count == 0
                   || _spec.Authors.Contains(s.Author, StringComparer.OrdinalIgnoreCase);

        if (!matches)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        firings = [new TriggerFiring(s.TicketId, null, null)];
        return true;
    }

    /// <summary>
    /// Signal firings bypass <see cref="EvaluateAsync"/>, so dispatching one does not advance
    /// the poll cursor — without this, the next poll would see the same comment as new and run
    /// the action chain a second time. Advances the cursor over the ticket's current comments,
    /// mirroring the poll's eager consumption. Comments added after this point (e.g. during a
    /// long agent run) still fire normally via their own signal or the next poll.
    /// </summary>
    public async Task ConsumeSignalFiringAsync(TriggerContext ctx, TriggerFiring firing)
    {
        if (firing.TicketId is not int ticketId) return;
        var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, ticketId);
        if (ticket is null || ticket.Comments.Count == 0) return;

        var maxId = ticket.Comments.Max(c => c.Id);
        var lastSeen = LoadLastCommentIds(ctx);
        lastSeen.TryGetValue(ticketId, out var prev);
        if (maxId <= prev) return;
        lastSeen[ticketId] = maxId;
        SaveLastCommentIds(ctx, lastSeen);
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);

    private static void SaveLastCommentIds(TriggerContext ctx, Dictionary<int, int> ids)
    {
        ctx.Sessions.Update(ctx.WorkspacePath, state =>
        {
            var obj = new JsonObject();
            foreach (var kv in ids) obj[kv.Key.ToString()] = kv.Value;
            state[CursorKey(ctx)] = obj;
        });
    }
}

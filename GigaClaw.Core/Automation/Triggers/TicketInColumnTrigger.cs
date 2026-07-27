using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>
/// Fires once per matching ticket (column + optional assignee filter).
/// The poll interval gates evaluation; between polls, no evaluation happens.
/// Dedup against active runs is handled by the engine (via AgentRunRegistry).
/// Optional per-ticket debounce prevents re-firing within a configurable window.
/// Consecutive outcomes and their retry cooldown are persisted per ticket. By default,
/// an unchanged ticket is suspended after three completed action chains; editing or
/// transitioning the ticket resets the attempt series.
///
/// The debounce marker is only written to disk after the full action chain succeeds
/// via <see cref="CommitFiringAsync"/> — firings skipped by transient gates are retried.
/// </summary>
public sealed class TicketInColumnTrigger : ITrigger
{
    private DateTime _lastEvaluated = DateTime.MinValue;
    private readonly TicketInColumnTriggerSpec _spec;

    public TicketInColumnTrigger(TicketInColumnTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastEvaluated).TotalSeconds < _spec.Seconds)
            return Array.Empty<TriggerFiring>();
        _lastEvaluated = ctx.Now;

        if (_spec.Columns.Count == 0) return Array.Empty<TriggerFiring>();

        var debounce = _spec.DebounceSeconds;
        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var lastFiredNode = GetLastFiredBucket(state, ctx.Automation.Id);
        var attemptNode = GetAttemptBucket(state, ctx.Automation.Id);

        var firings = new List<TriggerFiring>();
        foreach (var col in _spec.Columns)
        {
            var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug, statusFilter: col);
            foreach (var t in tickets)
            {
                if (!string.IsNullOrEmpty(_spec.AssigneeSlug))
                {
                    if (t.AssignedTo is null || t.AssignedTo != _spec.AssigneeSlug) continue;
                }

                if (debounce > 0 && t.Id is { } tid)
                {
                    var lastIso = lastFiredNode?[tid.ToString()]?.GetValue<string>();
                    if (lastIso is not null &&
                        DateTime.TryParse(lastIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastFiredAt) &&
                        (ctx.Now - lastFiredAt).TotalSeconds < debounce)
                    {
                        continue;
                    }
                }

                if (t.Id is { } attemptTid
                    && attemptNode[attemptTid.ToString()] is JsonObject attempt)
                {
                    var recordedFingerprint = attempt["ticketFingerprint"]?.GetValue<string>();
                    var ticketChanged = recordedFingerprint is not null
                        && !string.Equals(recordedFingerprint, Fingerprint(
                            t.Status, t.AssignedTo, t.Title, t.Description), StringComparison.Ordinal);
                    if (!ticketChanged)
                    {
                        var attempts = attempt["count"]?.GetValue<int>() ?? 0;
                        if (_spec.MaxConsecutiveFirings > 0
                            && attempts >= _spec.MaxConsecutiveFirings)
                        {
                            continue;
                        }

                        var nextEligibleAt = ParseDate(attempt["nextEligibleAt"]);
                        if (nextEligibleAt is not null && ctx.Now < nextEligibleAt.Value)
                            continue;
                    }
                }

                firings.Add(new TriggerFiring(t.Id, t.Title, t.Status));
            }
        }

        return firings;
    }

    public Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
        => CompleteFiringAsync(ctx, firing, succeeded: true, completedAt);

    public async Task CompleteFiringAsync(
        TriggerContext ctx,
        TriggerFiring firing,
        bool succeeded,
        DateTime? completedAt = null)
    {
        if (firing.TicketId is int tid)
        {
            var at = completedAt ?? ctx.Now;
            var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, tid);
            var fingerprint = ticket is null
                ? null
                : Fingerprint(ticket.Status, ticket.AssignedTo, ticket.Title, ticket.Description);
            var shouldHandleExhaustion = false;
            ctx.Sessions.Update(ctx.WorkspacePath, state =>
            {
                if (succeeded && _spec.DebounceSeconds > 0)
                {
                    var bucket = GetLastFiredBucket(state, ctx.Automation.Id);
                    bucket[tid.ToString()] = at.ToString("o");
                }

                var attempts = GetAttemptBucket(state, ctx.Automation.Id);
                var key = tid.ToString();
                var previous = attempts[key] as JsonObject;
                var previousFingerprint = previous?["ticketFingerprint"]?.GetValue<string>();
                var ticketChanged = fingerprint is not null
                    && previousFingerprint is not null
                    && !string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal);
                var count = ticketChanged ? 1 : (previous?["count"]?.GetValue<int>() ?? 0) + 1;
                var backoff = Math.Max(0, _spec.RetryBackoffSeconds);
                var previouslyHandled = !ticketChanged
                    && (previous?["exhaustionHandled"]?.GetValue<bool>() ?? false);
                shouldHandleExhaustion = _spec.MaxConsecutiveFirings > 0
                    && count >= _spec.MaxConsecutiveFirings
                    && !previouslyHandled
                    && (!string.IsNullOrWhiteSpace(_spec.ExhaustedStatus)
                        || !string.IsNullOrWhiteSpace(_spec.ExhaustedComment));
                attempts[key] = new JsonObject
                {
                    ["count"] = count,
                    ["lastCompletedAt"] = at.ToString("o"),
                    ["nextEligibleAt"] = at.AddSeconds(backoff).ToString("o"),
                    ["ticketFingerprint"] = fingerprint,
                    ["lastOutcome"] = succeeded ? "completed" : "failed",
                    // Claim before side effects so duplicate completion callbacks cannot
                    // emit duplicate escalation comments.
                    ["exhaustionHandled"] = previouslyHandled || shouldHandleExhaustion,
                };
            });

            if (shouldHandleExhaustion)
            {
                if (!string.IsNullOrWhiteSpace(_spec.ExhaustedStatus)
                    && ticket is not null
                    && !string.Equals(ticket.Status, _spec.ExhaustedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await ctx.Tickets.MoveTicketAsync(
                        ctx.ProjectSlug, tid, _spec.ExhaustedStatus, "automation");
                }

                if (!string.IsNullOrWhiteSpace(_spec.ExhaustedComment))
                {
                    var current = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, tid);
                    if (current is not null
                        && !current.Comments.Any(c =>
                            c.Author == "automation"
                            && c.Content == _spec.ExhaustedComment))
                    {
                        await ctx.Tickets.AddCommentAsync(
                            ctx.ProjectSlug, tid, _spec.ExhaustedComment, "automation");
                    }
                }

                // Store the post-escalation workflow fingerprint. Comments/activities do
                // not affect it; a later explicit move/edit does and starts a fresh series.
                var after = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, tid);
                if (after is not null)
                {
                    var finalFingerprint = Fingerprint(
                        after.Status, after.AssignedTo, after.Title, after.Description);
                    ctx.Sessions.Update(ctx.WorkspacePath, state =>
                    {
                        var entry = GetAttemptBucket(state, ctx.Automation.Id)[tid.ToString()] as JsonObject;
                        if (entry is not null) entry["ticketFingerprint"] = finalFingerprint;
                    });
                }
            }
        }
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastEvaluated == DateTime.MinValue ? now : _lastEvaluated.AddSeconds(_spec.Seconds);

    private static JsonObject GetLastFiredBucket(JsonObject state, string autoId)
    {
        var autoNode = state[autoId] as JsonObject;
        if (autoNode is null)
        {
            autoNode = new JsonObject();
            state[autoId] = autoNode;
        }
        var bucket = autoNode["lastFired"] as JsonObject;
        if (bucket is null)
        {
            bucket = new JsonObject();
            autoNode["lastFired"] = bucket;
        }
        return bucket;
    }

    private static JsonObject GetAttemptBucket(JsonObject state, string autoId)
    {
        var autoNode = state[autoId] as JsonObject;
        if (autoNode is null)
        {
            autoNode = new JsonObject();
            state[autoId] = autoNode;
        }
        var bucket = autoNode["ticketInColumnAttempts"] as JsonObject;
        if (bucket is null)
        {
            bucket = new JsonObject();
            autoNode["ticketInColumnAttempts"] = bucket;
        }
        return bucket;
    }

    private static DateTime? ParseDate(JsonNode? node)
    {
        var raw = node?.GetValue<string>();
        return raw is not null
            && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }

    private static string Fingerprint(
        string status,
        string? assignee,
        string title,
        string description)
    {
        var input = string.Join('\u001f', status, assignee ?? "", title, description);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}

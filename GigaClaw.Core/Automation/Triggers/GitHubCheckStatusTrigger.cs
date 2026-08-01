using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Github;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>
/// C7 part 3: GitHub check-run conclusions as a trigger, declared and registered exactly like
/// <see cref="GitCommitTrigger"/> — same family, same shape.
/// <para>
/// <b>Why the gitCommit family.</b> This trigger is about a commit, not about a ticket or a clock.
/// It resolves the commit the same way <see cref="GitCommitTrigger"/> does (the workspace's own
/// <c>git rev-parse</c>, unless the spec pins a ref), it polls on the same debounce, and it keeps
/// its seen-state in the same place. Anything else would give the board two different notions of
/// "a commit happened".
/// </para>
/// <para>
/// <b>Dedupe.</b> One key per <c>(sha, check-run id, conclusion)</c>, persisted per automation in
/// the workspace's <c>dispatch-state.json</c> — the same durable store
/// <see cref="GitCommitTrigger"/> uses for its last-processed commit and
/// <see cref="TicketCommentAddedTrigger"/> uses for its comment cursor. The conclusion is part of
/// the key because a re-run that flips <c>failure</c> to <c>success</c> is a new event, not the
/// same one seen twice.
/// </para>
/// </summary>
public sealed class GitHubCheckStatusTrigger : ITrigger
{
    private readonly GitHubCheckStatusTriggerSpec _spec;
    private readonly GitHubTriggerServices _services;
    private DateTime _lastPolled = DateTime.MinValue;

    /// <summary>Bounds the seen-key set so a long-lived workspace's state file cannot grow forever.</summary>
    private const int MaxRememberedKeys = 500;

    public GitHubCheckStatusTrigger(GitHubCheckStatusTriggerSpec spec, GitHubTriggerServices services)
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

        var reference = string.IsNullOrWhiteSpace(_spec.Ref)
            ? await ResolveHeadAsync(ctx.WorkspacePath, ct)
            : _spec.Ref.Trim();
        if (string.IsNullOrWhiteSpace(reference)) return [];

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}"
                + $"/commits/{Uri.EscapeDataString(reference)}/check-runs?per_page=100";

        var response = await _services.Client.SendAsync(
            new GitHubRequest(ctx.ProjectSlug, HttpMethod.Get, url, token!, Actor: "github-check-status"), ct);
        if (!response.Success) return [];   // dry-run or failure: the receipt is the record

        List<CheckRun> runs;
        try { runs = ParseCheckRuns(response.Body); }
        catch (JsonException) { return []; }

        var wanted = new HashSet<string>(
            _spec.Conclusions.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var seen = LoadSeen(ctx);
        var firings = new List<TriggerFiring>();
        var added = false;

        // Resolved once per poll, not per check run: every run here belongs to the same commit.
        int? ticketId = null;
        var ticketResolved = false;

        foreach (var run in runs.OrderBy(r => r.Id))
        {
            ct.ThrowIfCancellationRequested();
            // Still running: a check with no conclusion has not concluded anything.
            if (string.IsNullOrWhiteSpace(run.Conclusion)) continue;
            if (wanted.Count > 0 && !wanted.Contains(run.Conclusion)) continue;

            var key = $"{reference}:{run.Id}:{run.Conclusion}";
            if (!seen.Add(key)) continue;
            added = true;

            if (!ticketResolved)
            {
                ticketId = await ResolveTicketAsync(ctx, reference, ct);
                ticketResolved = true;
            }

            if (ticketId is int id)
            {
                var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, id);
                if (ticket is not null)
                {
                    firings.Add(new TriggerFiring(ticket.Id, ticket.Title, ticket.Status));
                    continue;
                }
            }

            // Ticketless, exactly like a gitCommit firing: the title carries the evidence.
            firings.Add(new TriggerFiring(null, $"check {run.Name} {run.Conclusion} on {Short(reference)}", null));
        }

        if (added) SaveSeen(ctx, seen);
        return firings;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);

    private static string Short(string reference) => reference.Length > 7 ? reference[..7] : reference;

    /// <summary>The workspace's own HEAD — the same source <see cref="GitCommitTrigger"/> reads.</summary>
    private static async Task<string?> ResolveHeadAsync(string workspacePath, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(workspacePath, ".git"))) return null;
        try
        {
            var result = await Services.ProcessRunner.RunAsync(
                "git", "rev-parse HEAD", workspacePath, TimeSpan.FromSeconds(15), ct: ct);
            return result.Success ? result.Stdout?.Trim() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Which ticket a CI result belongs to, read from the commit's own message — free, local, and
    /// no second API call. Nothing found means the firing is ticketless, which is the gitCommit
    /// family's normal shape rather than a failure.
    /// </summary>
    private static async Task<int?> ResolveTicketAsync(TriggerContext ctx, string reference, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(ctx.WorkspacePath, ".git"))) return null;
        try
        {
            var result = await Services.ProcessRunner.RunAsync(
                "git", $"log -1 --format=%B {reference}", ctx.WorkspacePath, TimeSpan.FromSeconds(15), ct: ct);
            return result.Success ? TicketReference.Find(result.Stdout) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── seen-state ──────────────────────────────────────────────────────────

    private static string SeenKey(TriggerContext ctx) => "_githubChecks:" + ctx.Automation.Id;

    private static HashSet<string> LoadSeen(TriggerContext ctx)
    {
        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (state[SeenKey(ctx)] is not JsonArray array) return seen;
        foreach (var node in array)
            if (node?.GetValue<string>() is string value) seen.Add(value);
        return seen;
    }

    private static void SaveSeen(TriggerContext ctx, HashSet<string> seen)
    {
        // Newest wins when trimming: an old commit's checks will not be re-reported, because the
        // commit itself is long past and its keys are the first to be dropped.
        var kept = seen.Count <= MaxRememberedKeys ? seen : seen.Skip(seen.Count - MaxRememberedKeys);
        ctx.Sessions.Update(ctx.WorkspacePath, state =>
        {
            var array = new JsonArray();
            foreach (var key in kept) array.Add(key);
            state[SeenKey(ctx)] = array;
        });
    }

    // ── parsing ─────────────────────────────────────────────────────────────

    internal sealed record CheckRun(long Id, string Name, string Status, string? Conclusion);

    internal static List<CheckRun> ParseCheckRuns(string json)
    {
        using var document = JsonDocument.Parse(json);
        var runs = new List<CheckRun>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return runs;
        if (!root.TryGetProperty("check_runs", out var array) || array.ValueKind != JsonValueKind.Array) return runs;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
            runs.Add(new CheckRun(
                Id: id,
                Name: Str(element, "name") ?? $"check {id}",
                Status: Str(element, "status") ?? "",
                Conclusion: Str(element, "conclusion")));
        }
        return runs;

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
    }
}

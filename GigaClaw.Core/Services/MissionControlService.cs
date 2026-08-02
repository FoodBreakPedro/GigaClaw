using GigaClaw.Core.Automation;
using GigaClaw.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Services;

/// <summary>
/// Plan 4.2 — the cross-project aggregation behind the Mission Control page (<c>/mission</c>).
///
/// <para>One <see cref="BuildAsync"/> call assembles every section from live sources: the registry
/// and per-project SQLite for tickets, <see cref="TicketStatSnapshotService"/> for yesterday's
/// baseline and the velocity series, <see cref="CostLogReader"/> for agent workload and spend,
/// <see cref="AgentRunRegistry"/> for what is running right now,
/// <see cref="LocalMediaJobService"/> for media throughput, and
/// <see cref="EvaluatorScores"/> for the quality tile. The page component renders — it aggregates
/// nothing, which is what keeps the aggregation testable and the component readable.</para>
///
/// <para>Paused projects are excluded from every section: the board hides them, and a KPI that
/// counts tickets nobody is working on reads as work in flight.</para>
/// </summary>
public sealed class MissionControlService
{
    /// <summary>KPI columns, in the mockup's order. "Resolved today" is appended separately — it is a
    /// flow, not a column count.</summary>
    public static readonly string[] KpiColumns = ["Backlog", "Todo", "InProgress", "Review", "Blocked"];

    public const string ResolvedKpi = "ResolvedToday";
    public const string PendingApprovalLabel = "pending-approval";
    private const string DoneStatus = "Done";
    private const int VelocityDays = 7;
    /// <summary>Window the workload bars count dispatches over — "dispatches this week".</summary>
    private const int WorkloadDays = 7;
    /// <summary>Window the cost journal is read over. Wider than <see cref="WorkloadDays"/> on
    /// purpose: staleness is answered from the last run found here, and an agent whose last run was
    /// nine days ago is exactly the one the "very stale" bucket exists to surface. Reading it in the
    /// dispatch window instead made that bucket unreachable and hid cold agents entirely.</summary>
    private const int CostLookbackDays = 30;
    private const int ActivityWindowDays = 3;
    /// <summary>Cost-cap receipts park a ticket indefinitely, so the attention queue looks much
    /// further back than the activity feed does.</summary>
    private const int CostCapLookbackDays = 90;
    private const int MaxMarkerComments = 200;
    private const int MaxActivityEvents = 20;
    private const int MaxRecentTickets = 8;
    private const double BudgetAlertFraction = 0.8;

    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly TicketStatSnapshotService _snapshots;
    private readonly LocalMediaJobService _media;
    private readonly AgentTeamService _teams;
    private readonly MemberService _members;
    private readonly AutomationStore _automations;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<MissionControlService>? _logger;

    public MissionControlService(
        ProjectService projects,
        TicketService tickets,
        TicketStatSnapshotService snapshots,
        LocalMediaJobService media,
        AgentTeamService teams,
        MemberService members,
        AutomationStore automations,
        AgentRunRegistry runs,
        ILogger<MissionControlService>? logger = null)
    {
        _projects = projects;
        _tickets = tickets;
        _snapshots = snapshots;
        _media = media;
        _teams = teams;
        _members = members;
        _automations = automations;
        _runs = runs;
        _logger = logger;
    }

    public async Task<MissionControlSnapshot> BuildAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(nowUtc.Date);
        var projects = (await _projects.ListProjectsAsync()).Where(p => !p.IsPaused).ToList();

        var lanes = new List<Lane>();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) break;
            try { lanes.Add(await LoadLaneAsync(project, nowUtc, today, ct)); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Mission Control skipped project {Project}", project.Slug); }
        }

        var kpis = BuildKpis(lanes, today);
        var velocity = BuildVelocity(lanes, today);
        var snapshotsSince = lanes
            .Select(l => l.FirstSnapshotDate)
            .Where(d => d is not null)
            .DefaultIfEmpty(null)
            .Min();
        var workload = BuildWorkload(lanes, nowUtc);
        var attention = BuildAttention(lanes, nowUtc, ct);
        var throughput = BuildThroughput(lanes, nowUtc);
        var mix = BuildStatusMix(lanes);
        var cost = BuildCost(lanes, nowUtc);
        var recent = lanes
            .SelectMany(l => l.Tickets.Select(t => new MissionRecentTicket(
                l.Project.Slug, l.Project.Name, t.Id, t.Title, t.Status, t.Priority, t.UpdatedAt)))
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(MaxRecentTickets)
            .ToList();
        var activity = BuildActivity(lanes, nowUtc);
        var scores = lanes
            .Select(l => new MissionProjectScores(l.Project.Slug, l.Project.Name, l.Scores))
            .Where(s => s.Scores.Count > 0)
            .ToList();

        var refs = lanes.Select(l => new MissionProjectRef(l.Project.Slug, l.Project.Name)).ToList();

        return new MissionControlSnapshot(
            nowUtc, refs, kpis, velocity, snapshotsSince, workload, attention,
            throughput, mix, cost, recent, activity, scores);
    }

    // ---------------------------------------------------------------- per-project load

    private sealed record Lane(
        Project Project,
        string Workspace,
        List<TicketSummary> Tickets,
        ColumnCountSnapshot? Yesterday,
        IReadOnlyList<DailyTicketStats> Series,
        DateOnly? FirstSnapshotDate,
        IReadOnlyList<CostLogEntry> CostEntries,
        IReadOnlyList<LocalMediaJob> MediaJobs,
        IReadOnlyList<CommentRow> Markers,
        decimal? DailyBudgetUsd,
        IReadOnlyList<EvaluatorAgentScore> Scores,
        IReadOnlyList<string> Roster);

    private sealed record CommentRow(int TicketId, string Author, string Content, DateTime CreatedAt);

    private async Task<Lane> LoadLaneAsync(Project project, DateTime nowUtc, DateOnly today, CancellationToken ct)
    {
        var workspace = _projects.ResolveWorkspacePath(project);
        var tickets = await _tickets.ListTicketsAsync(project.Slug);
        var yesterday = await _snapshots.GetColumnCountsAsync(project.Slug, today.AddDays(-1), ct);
        var series = await _snapshots.GetSeriesAsync(project.Slug, today.AddDays(-(VelocityDays - 1)), today, ct);
        var firstSnapshot = await _snapshots.GetFirstSnapshotDateAsync(project.Slug, ct);
        var costEntries = Directory.Exists(workspace)
            ? CostLogReader.Read(workspace, nowUtc.AddDays(-CostLookbackDays))
            : [];

        IReadOnlyList<LocalMediaJob> mediaJobs;
        try { mediaJobs = await _media.ListAsync(project.Slug, ct); }
        catch { mediaJobs = []; }

        decimal? budget = null;
        try { budget = (await _automations.LoadAsync(project.Slug)).Config.DailyBudgetUsd; }
        catch { /* no workspace / unreadable automations.json — the project simply has no budget alert */ }

        var markers = await ReadMarkerCommentsAsync(project.Slug, nowUtc, ct);
        var scores = Directory.Exists(workspace) ? EvaluatorScores.ReadForWorkspace(workspace) : [];

        // The project's own members are the roster the workload chart is supposed to describe. An
        // agent that has never been dispatched is a fact about the project, not an absence of data,
        // and it is precisely the one an operator wants to see.
        IReadOnlyList<string> roster;
        try { roster = (await _members.ListMembersAsync(project.Slug)).Select(m => m.Slug).ToList(); }
        catch { roster = []; }

        return new Lane(project, workspace, tickets, yesterday, series, firstSnapshot,
            costEntries, mediaJobs, markers, budget, scores, roster);
    }

    /// <summary>
    /// Comments carrying a <c>GIGACLAW-*</c> receipt marker. These are the machine-written events the
    /// activity feed and the cost-cap attention row are derived from — the repo has no event log, and
    /// the receipt trail is deliberately the durable record (see <c>doc/verdict-contract.md</c>).
    /// The window is widened for cost-cap receipts, which park a ticket indefinitely.
    /// </summary>
    private async Task<IReadOnlyList<CommentRow>> ReadMarkerCommentsAsync(
        string slug, DateTime nowUtc, CancellationToken ct)
    {
        var activitySince = nowUtc.AddDays(-ActivityWindowDays);
        var costCapSince = nowUtc.AddDays(-CostCapLookbackDays);
        try
        {
            await using var db = _projects.GetProjectDb(slug);

            // Two date-led queries rather than one OR: with `CreatedAt >=` leading, the comments
            // table is range-scanned and the substring match only ever runs over the rows inside the
            // window. The combined form put the widest LIKE first, so every comment ever written on
            // the project was scanned on every render.
            var recent = await db.Comments.AsNoTracking()
                .Where(c => c.CreatedAt >= activitySince && c.Content.Contains("GIGACLAW-"))
                .OrderByDescending(c => c.CreatedAt)
                .Take(MaxMarkerComments)
                .Select(c => new CommentRow(c.TicketId, c.Author, c.Content, c.CreatedAt))
                .ToListAsync(ct);

            var parked = await db.Comments.AsNoTracking()
                .Where(c => c.CreatedAt >= costCapSince && c.CreatedAt < activitySince
                    && c.Content.Contains(TicketCostCap.MarkerPrefix))
                .OrderByDescending(c => c.CreatedAt)
                .Take(MaxMarkerComments)
                .Select(c => new CommentRow(c.TicketId, c.Author, c.Content, c.CreatedAt))
                .ToListAsync(ct);

            return [.. recent, .. parked];
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Mission Control could not read receipts for {Project}", slug);
            return [];
        }
    }

    // ---------------------------------------------------------------- sections

    private static List<MissionKpi> BuildKpis(List<Lane> lanes, DateOnly today)
    {
        var kpis = new List<MissionKpi>();
        var yesterdayDate = today.AddDays(-1);
        // Deltas are computed over the subset of projects that actually have yesterday's snapshot,
        // and against that same subset's live counts — never a whole-board count against a partial
        // baseline, which would report a delta the size of the projects missing from it.
        //
        // The CapturedAt re-check is the belt to the writer's suspenders: a row written long after
        // the day it is stamped with holds today's counts under yesterday's date, and differencing
        // against that yields a delta of about zero that looks like a calm day rather than a gap.
        var withBaseline = lanes
            .Where(l => l.Yesterday is not null
                && TicketStatSnapshotService.IsWithinCaptureGrace(yesterdayDate, l.Yesterday.CapturedAtUtc))
            .ToList();

        foreach (var column in KpiColumns)
        {
            var count = lanes.Sum(l => l.Tickets.Count(t => Same(t.Status, column)));
            int? delta = null;
            if (withBaseline.Count > 0)
            {
                var live = withBaseline.Sum(l => l.Tickets.Count(t => Same(t.Status, column)));
                var baseline = withBaseline.Sum(l => l.Yesterday!.Counts.TryGetValue(column, out var v) ? v : 0);
                delta = live - baseline;
            }
            kpis.Add(new MissionKpi(column, count, delta));
        }

        var resolvedToday = lanes.Sum(l => l.Tickets.Count(t =>
            Same(t.Status, DoneStatus) && DateOnly.FromDateTime(t.UpdatedAt.Date) == today));
        int? resolvedDelta = null;
        var yesterdayResolved = lanes
            .Select(l => l.Series.FirstOrDefault(s => s.Date == today.AddDays(-1)))
            .Where(s => s is not null)
            .ToList();
        if (withBaseline.Count > 0 && yesterdayResolved.Count > 0)
            resolvedDelta = resolvedToday - yesterdayResolved.Sum(s => s!.Resolved);
        kpis.Add(new MissionKpi(ResolvedKpi, resolvedToday, resolvedDelta));
        return kpis;
    }

    private static List<DailyTicketStats> BuildVelocity(List<Lane> lanes, DateOnly today)
    {
        var series = new List<DailyTicketStats>();
        for (var day = today.AddDays(-(VelocityDays - 1)); day <= today; day = day.AddDays(1))
        {
            var rows = lanes
                .Select(l => l.Series.FirstOrDefault(s => s.Date == day))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();
            var blocked = rows.Where(r => r.Blocked is not null).ToList();
            series.Add(new DailyTicketStats(
                day,
                rows.Sum(r => r.Created),
                rows.Sum(r => r.Resolved),
                blocked.Count == 0 ? null : blocked.Sum(r => r.Blocked!.Value)));
        }
        return series;
    }

    /// <summary>
    /// Dispatch counts and staleness per agent. The two answer different questions and therefore
    /// read different windows: the bar length is "dispatches this week"
    /// (<see cref="WorkloadDays"/>), while the last-run stamp comes from the whole
    /// <see cref="CostLookbackDays"/> journal — otherwise nothing could ever be more than seven days
    /// stale and an agent idle for eight days would simply vanish from the chart.
    /// </summary>
    private List<MissionAgentWorkload> BuildWorkload(List<Lane> lanes, DateTime nowUtc)
    {
        var dispatchSince = nowUtc.AddDays(-WorkloadDays);
        var byAgent = new Dictionary<string, (int Count, DateTime? Last)>(StringComparer.OrdinalIgnoreCase);

        // The roster comes first so a member that has never been dispatched lands on the chart with
        // zero dispatches and no last-run rather than being absent — "this agent has never run" is
        // the single most actionable thing the tile can say.
        foreach (var agent in lanes.SelectMany(l => l.Roster).Where(a => !string.IsNullOrWhiteSpace(a)))
            byAgent.TryAdd(agent, (0, null));

        foreach (var entry in lanes.SelectMany(l => l.CostEntries))
        {
            var agent = string.IsNullOrWhiteSpace(entry.Agent) ? "unknown" : entry.Agent;
            var at = entry.At.ToUniversalTime();
            var dispatch = at >= dispatchSince ? 1 : 0;
            if (byAgent.TryGetValue(agent, out var current))
                byAgent[agent] = (
                    current.Count + dispatch,
                    current.Last is DateTime last && last > at ? last : at);
            else
                byAgent[agent] = (dispatch, at);
        }

        var running = _runs.AllActive()
            .Select(r => r.AgentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // An agent running right now but with no journal line yet (its cost is written when it ends)
        // still belongs on the chart — otherwise the busiest agent in the house is the invisible one.
        foreach (var agent in running)
            byAgent[agent] = byAgent.TryGetValue(agent, out var known) ? (known.Count, nowUtc) : (0, nowUtc);

        return byAgent
            .Select(kv => new MissionAgentWorkload(kv.Key, kv.Value.Count, kv.Value.Last, running.Contains(kv.Key)))
            .OrderByDescending(w => w.Dispatches)
            .ThenByDescending(w => w.LastRunAtUtc ?? DateTime.MinValue)
            .ThenBy(w => w.Agent, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MissionAttentionItem> BuildAttention(
        List<Lane> lanes, DateTime nowUtc, CancellationToken ct)
    {
        var items = new List<MissionAttentionItem>();
        var todayStart = nowUtc.Date;

        foreach (var lane in lanes)
        {
            if (ct.IsCancellationRequested) break;

            foreach (var ticket in lane.Tickets.Where(t => Same(t.Status, "Blocked")))
            {
                items.Add(new MissionAttentionItem(
                    MissionSeverity.Critical, "blocked", lane.Project.Slug, lane.Project.Name,
                    ticket.Id, MissionText.Literal(ticket.Title),
                    // The reason is agent-authored prose already in the operator's ticket; only the
                    // "there wasn't one" fallback is ours to translate.
                    ticket.BlockedReason is string reason
                        ? MissionText.Literal(reason)
                        : MissionText.Of("MissionAttentionBlockedNoReceipt"),
                    ticket.UpdatedAt, null));
            }

            foreach (var ticket in lane.Tickets.Where(t =>
                t.Labels.Any(l => string.Equals(l.Name, PendingApprovalLabel, StringComparison.OrdinalIgnoreCase))))
            {
                items.Add(new MissionAttentionItem(
                    MissionSeverity.Warning, "approval", lane.Project.Slug, lane.Project.Name,
                    ticket.Id, MissionText.Literal(ticket.Title),
                    ticket.AssignedTo is string assignee
                        ? MissionText.Of("MissionAttentionApproval", PendingApprovalLabel, assignee)
                        : MissionText.Of("MissionAttentionApprovalUnassigned", PendingApprovalLabel),
                    ticket.UpdatedAt, null));
            }

            // Plan 2.1 receipts: a ticket parked by the per-ticket cost cap is invisible on the board
            // (it just stops moving), so the queue is the only place it surfaces.
            var capped = lane.Markers
                .Where(m => m.Content.Contains(TicketCostCap.MarkerPrefix, StringComparison.Ordinal))
                .GroupBy(m => m.TicketId)
                .Select(g => g.OrderByDescending(m => m.CreatedAt).First());
            foreach (var receipt in capped)
            {
                var ticket = lane.Tickets.FirstOrDefault(t => t.Id == receipt.TicketId);
                if (ticket is null || Same(ticket.Status, DoneStatus)) continue;
                items.Add(new MissionAttentionItem(
                    MissionSeverity.Warning, "costcap", lane.Project.Slug, lane.Project.Name,
                    ticket.Id, MissionText.Literal(ticket.Title),
                    MissionText.Of("MissionAttentionCostCap", ticket.AgentCostUsd.ToString("0.##")),
                    receipt.CreatedAt, null));
            }

            if (lane.DailyBudgetUsd is decimal budget && budget > 0m)
            {
                var spentToday = lane.CostEntries
                    .Where(e => e.At.ToUniversalTime() >= todayStart)
                    .Sum(e => e.UsdCost);
                var fraction = (double)(spentToday / budget);
                if (fraction >= BudgetAlertFraction)
                {
                    items.Add(new MissionAttentionItem(
                        fraction >= 1.0 ? MissionSeverity.Critical : MissionSeverity.Warning,
                        "budget", lane.Project.Slug, lane.Project.Name, null,
                        MissionText.Of("MissionAttentionBudgetTitle",
                            spentToday.ToString("0.00"), budget.ToString("0.00")),
                        MissionText.Of("MissionAttentionBudget", (fraction * 100).ToString("0")),
                        nowUtc, Math.Min(fraction, 1.0)));
                }
            }
        }

        return items
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.SinceUtc ?? DateTime.MinValue)
            .ToList();
    }

    private List<MissionThroughput> BuildThroughput(List<Lane> lanes, DateTime nowUtc)
    {
        var thisWeekFrom = nowUtc.AddDays(-7);
        var lastWeekFrom = nowUtc.AddDays(-14);

        int DoneBy(Lane lane, Func<TicketSummary, bool> predicate, DateTime from, DateTime to) =>
            lane.Tickets.Count(t => Same(t.Status, DoneStatus)
                && t.UpdatedAt >= from && t.UpdatedAt < to && predicate(t));

        // Team membership is the simplest honest proxy for "which pipeline finished this ticket":
        // teams.json already maps every agent to its pipeline, and the assignee is the agent that
        // carried the ticket to Done. No commit/merge archaeology is involved, and a ticket with no
        // assignee counts toward nothing rather than being guessed into a pipeline.
        var code = TeamAgents(lanes, AgentTeamService.SoftwareEngineeringSlug);
        var content = TeamAgents(lanes, AgentTeamService.ContentEngineSlug);

        bool InTeam(TicketSummary t, HashSet<string> team) =>
            t.AssignedTo is not null && team.Contains(t.AssignedTo);

        bool IsDecision(TicketSummary t) => t.Labels.Any(l =>
            l.Name.Contains("decision", StringComparison.OrdinalIgnoreCase)
            || l.Name.Equals("adr", StringComparison.OrdinalIgnoreCase));

        int MediaApproved(DateTime from, DateTime to) => lanes.Sum(l => l.MediaJobs.Count(j =>
            string.Equals(j.Status, LocalMediaJobStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            && j.ReviewedAt is DateTime reviewed && reviewed >= from && reviewed < to));

        return
        [
            new MissionThroughput("CodeTicketsDone",
                lanes.Sum(l => DoneBy(l, t => InTeam(t, code), thisWeekFrom, nowUtc)),
                lanes.Sum(l => DoneBy(l, t => InTeam(t, code), lastWeekFrom, thisWeekFrom))),
            new MissionThroughput("PostsPublished",
                lanes.Sum(l => DoneBy(l, t => InTeam(t, content), thisWeekFrom, nowUtc)),
                lanes.Sum(l => DoneBy(l, t => InTeam(t, content), lastWeekFrom, thisWeekFrom))),
            new MissionThroughput("MediaApproved",
                MediaApproved(thisWeekFrom, nowUtc),
                MediaApproved(lastWeekFrom, thisWeekFrom)),
            new MissionThroughput("DecisionsRecorded",
                lanes.Sum(l => DoneBy(l, IsDecision, thisWeekFrom, nowUtc)),
                lanes.Sum(l => DoneBy(l, IsDecision, lastWeekFrom, thisWeekFrom))),
        ];
    }

    private HashSet<string> TeamAgents(List<Lane> lanes, string teamSlug)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A workspace may customize teams.json, so union every project's roster: the same agent slug
        // means the same role across projects, and a project that never customized still contributes
        // the built-in roster.
        foreach (var workspace in lanes.Select(l => l.Workspace).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var team = _teams.GetTeamBySlug(teamSlug, Directory.Exists(workspace) ? workspace : null);
            if (team is null) continue;
            foreach (var agent in team.AgentSlugs) agents.Add(agent);
        }
        if (agents.Count == 0)
        {
            var builtIn = _teams.GetTeamBySlug(teamSlug);
            if (builtIn is not null) foreach (var agent in builtIn.AgentSlugs) agents.Add(agent);
        }
        return agents;
    }

    private static Dictionary<string, int> BuildStatusMix(List<Lane> lanes) =>
        lanes.SelectMany(l => l.Tickets)
            .Where(t => !Same(t.Status, DoneStatus))
            .GroupBy(t => t.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    private static MissionCostStrip BuildCost(List<Lane> lanes, DateTime nowUtc)
    {
        var todayStart = nowUtc.Date;
        var weekStart = nowUtc.AddDays(-7);
        var all = lanes.SelectMany(l => l.CostEntries).ToList();
        var week = all.Where(e => e.At.ToUniversalTime() >= weekStart).ToList();
        var today = all.Where(e => e.At.ToUniversalTime() >= todayStart).ToList();

        var costliest = lanes
            .SelectMany(l => l.Tickets.Select(t => (Slug: l.Project.Slug, t.Id, t.AgentCostUsd)))
            .Where(t => t.AgentCostUsd > 0)
            .OrderByDescending(t => t.AgentCostUsd)
            .FirstOrDefault();

        return new MissionCostStrip(
            today.Sum(e => e.UsdCost),
            week.Sum(e => e.UsdCost),
            CostLogReader.CacheSavingsPercent(week),
            costliest.Slug,
            costliest.Slug is null ? null : costliest.Id,
            costliest.AgentCostUsd);
    }

    private List<MissionActivityEvent> BuildActivity(List<Lane> lanes, DateTime nowUtc)
    {
        var events = new List<MissionActivityEvent>();

        foreach (var lane in lanes)
        {
            foreach (var marker in lane.Markers)
            {
                if (marker.CreatedAt < nowUtc.AddDays(-ActivityWindowDays)) continue;
                var (kind, text) = DescribeMarker(marker.Content, marker.TicketId, marker.Author);
                if (kind is null) continue;
                events.Add(new MissionActivityEvent(
                    marker.CreatedAt, lane.Project.Slug, marker.Author, marker.TicketId, kind, text!, null));
            }
        }

        // Run start/stop from the registry. It only keeps 24h (AutomationEngine purges), which is
        // exactly the horizon this feed shows, so nothing is lost by not persisting more.
        var slugs = lanes.Select(l => l.Project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var run in lanes.SelectMany(l => _runs.AllForProject(l.Project.Slug)))
        {
            if (!slugs.Contains(run.ProjectSlug)) continue;
            if (run.Status == AgentRunStatus.Running)
                events.Add(new MissionActivityEvent(
                    run.StartedAt, run.ProjectSlug, run.AgentName, run.TicketId, "run-started",
                    MissionText.Of("MissionFeedRunStarted", run.AgentName), run.Model));
            else if (run.EndedAt is DateTime ended)
                events.Add(new MissionActivityEvent(
                    ended, run.ProjectSlug, run.AgentName, run.TicketId,
                    run.Status == AgentRunStatus.Completed ? "run-completed" : "run-failed",
                    // Stopped and Failed share the "run-failed" dot but not the verb.
                    MissionText.Of(run.Status switch
                    {
                        AgentRunStatus.Completed => "MissionFeedRunCompleted",
                        AgentRunStatus.Stopped => "MissionFeedRunStopped",
                        _ => "MissionFeedRunFailed"
                    }, run.AgentName),
                    run.Model));
        }

        return events
            .OrderByDescending(e => e.AtUtc)
            .Take(MaxActivityEvents)
            .ToList();
    }

    /// <summary>Classifies a receipt comment into a feed event. Unknown markers are dropped rather
    /// than shown raw — the feed is a summary, and the ticket already holds the full receipt. The
    /// sentence itself is left to the page: see <see cref="MissionText"/>.</summary>
    internal static (string? Kind, MissionText? Text) DescribeMarker(string content, int ticketId, string author)
    {
        var firstLine = content.Split('\n', 2)[0].Trim();
        var id = ticketId.ToString();
        if (firstLine.Contains("GIGACLAW-GATE", StringComparison.Ordinal))
        {
            // The verdict token is the receipt's own vocabulary, not prose — it stays as written.
            var verdict = firstLine.Contains("SHIP", StringComparison.Ordinal) ? "SHIP"
                : firstLine.Contains("BLOCK", StringComparison.Ordinal) ? "BLOCK"
                : firstLine.Contains("FIX", StringComparison.Ordinal) ? "FIX"
                : "gate";
            return ("gate", MissionText.Of("MissionFeedGate", author, verdict, id));
        }
        if (firstLine.Contains("GIGACLAW-REPAIR", StringComparison.Ordinal))
            return ("repair", MissionText.Of("MissionFeedRepair", author, id));
        if (firstLine.Contains("GIGACLAW-REREVIEW", StringComparison.Ordinal))
            return ("rereview", MissionText.Of("MissionFeedRereview", author, id));
        if (firstLine.Contains(TicketCostCap.MarkerPrefix, StringComparison.Ordinal))
            return ("costcap", MissionText.Of("MissionFeedCostCap", id));
        if (firstLine.Contains("GIGACLAW-HANDOFF", StringComparison.Ordinal))
            return ("handoff", MissionText.Of("MissionFeedHandoff", author, id));
        if (firstLine.Contains("GIGACLAW-VERDICT", StringComparison.Ordinal))
            return ("verdict", MissionText.Of("MissionFeedVerdict", author, id));
        if (firstLine.Contains("GIGACLAW-MERGE", StringComparison.Ordinal)
            || firstLine.Contains("merge-held", StringComparison.Ordinal))
            return ("merge", MissionText.Of("MissionFeedMerge", id));
        return (null, null);
    }

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

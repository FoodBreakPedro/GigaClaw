using GigaClaw.Core.Models;

namespace GigaClaw.Core.Services;

/// <summary>Value types rendered by the Mission Control page (Plan 4.2). Records, per the repo's
/// DTO convention; every "unknown" is modelled as null so the page can hide a figure instead of
/// inventing one.</summary>
public sealed record MissionKpi(string Column, int Count, int? Delta);

/// <summary>
/// A user-facing string the Mission Control page has still to render: a localization key plus its
/// ordered format arguments, resolved against the Mission.{en,es,fr} dictionaries by the component.
///
/// <para>Composing the sentence here instead would hard-code English into every locale — the whole
/// activity feed and attention queue used to read English on a French UI for exactly that reason.
/// A <see cref="Literal"/> carries text that is already final and must not be formatted: an
/// agent-authored blocked reason or a ticket title, which may itself contain braces.</para>
/// </summary>
public sealed record MissionText(string? Key, IReadOnlyList<string> Args)
{
    public static MissionText Of(string key, params string[] args) => new(key, args);

    public static MissionText Literal(string text) => new(null, [text]);
}

public sealed record MissionAgentWorkload(
    string Agent,
    int Dispatches,
    DateTime? LastRunAtUtc,
    bool Running)
{
    /// <summary>Whole days since this agent last ran. Null means it has not run at all inside the
    /// journal's lookback — i.e. as far as the durable record goes, never.</summary>
    public int? StaleDays => LastRunAtUtc is null ? null : (int)(DateTime.UtcNow - LastRunAtUtc.Value).TotalDays;

    /// <summary>fresh / stale (&gt;3d) / very-stale (&gt;7d or never run) — the mockup's workload
    /// colouring. A roster agent with no run at all is the most stale thing on the chart, not an
    /// unknown: it is exactly what the reddest bucket is for.</summary>
    public string Staleness => StaleDays switch
    {
        null => "very-stale",
        > 7 => "very-stale",
        > 3 => "stale",
        _ => "fresh"
    };

    /// <summary>True when the durable journal holds no run for this agent at all.</summary>
    public bool NeverRan => LastRunAtUtc is null;
}

public enum MissionSeverity { Info, Warning, Critical }

/// <summary>One row of the cross-project attention queue. <see cref="Kind"/> drives which actions the
/// page offers; <see cref="TicketId"/> is null for project-level alerts (a budget warning).</summary>
public sealed record MissionAttentionItem(
    MissionSeverity Severity,
    string Kind,
    string ProjectSlug,
    string ProjectName,
    int? TicketId,
    MissionText Title,
    MissionText Detail,
    DateTime? SinceUtc,
    double? Progress);

public sealed record MissionThroughput(string Metric, int ThisWeek, int LastWeek);

public sealed record MissionRecentTicket(
    string ProjectSlug,
    string ProjectName,
    int TicketId,
    string Title,
    string Status,
    TicketPriority Priority,
    DateTime UpdatedAtUtc);

public sealed record MissionActivityEvent(
    DateTime AtUtc,
    string ProjectSlug,
    string Agent,
    int? TicketId,
    string Kind,
    MissionText Text,
    string? Detail);

public sealed record MissionCostStrip(
    decimal TodayUsd,
    decimal WeekUsd,
    double? CacheSavingsPercent,
    string? CostliestProjectSlug,
    int? CostliestTicketId,
    double CostliestUsd);

public sealed record MissionProjectScores(string ProjectSlug, string ProjectName, IReadOnlyList<EvaluatorAgentScore> Scores);

/// <summary>The active (non-paused) projects the snapshot was built from — the page needs their
/// slugs to read live runs out of <c>AgentRunsState</c>, which is a Web-side service.</summary>
public sealed record MissionProjectRef(string Slug, string Name);

/// <summary>Everything the Mission Control page renders, assembled in one pass so the component does
/// no aggregation of its own.</summary>
public sealed record MissionControlSnapshot(
    DateTime GeneratedAtUtc,
    IReadOnlyList<MissionProjectRef> Projects,
    IReadOnlyList<MissionKpi> Kpis,
    IReadOnlyList<DailyTicketStats> Velocity,
    DateOnly? SnapshotsSince,
    IReadOnlyList<MissionAgentWorkload> Workload,
    IReadOnlyList<MissionAttentionItem> Attention,
    IReadOnlyList<MissionThroughput> Throughput,
    IReadOnlyDictionary<string, int> StatusMix,
    MissionCostStrip Cost,
    IReadOnlyList<MissionRecentTicket> RecentTickets,
    IReadOnlyList<MissionActivityEvent> Activity,
    IReadOnlyList<MissionProjectScores> EvaluatorScores);

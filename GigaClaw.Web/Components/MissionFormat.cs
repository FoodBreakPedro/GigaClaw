using System.Globalization;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;

namespace GigaClaw.Web.Components;

/// <summary>
/// Formatting and palette lookups shared by the Mission Control page and its section cards
/// (<c>Mission*Card</c>). They live here rather than on the page because several sections need the
/// same ones — the KPI row, the status mix and the recent-tickets table all colour and label by
/// column name — and a card cannot reach into its parent's <c>@code</c> block.
/// </summary>
internal static class MissionFormat
{
    /// <summary>Invariant-culture number rendering. SVG geometry and CSS percentages are parsed by
    /// the browser, which never accepts a comma decimal separator — so a French server locale would
    /// otherwise silently produce an unrenderable chart.</summary>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Avg(int total, int days) =>
        days == 0 ? "0" : (total / (double)days).ToString("0.0", CultureInfo.InvariantCulture);

    public static string Usd(decimal value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Since(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }

    public static string Elapsed(DateTime startedAt)
    {
        var span = DateTime.UtcNow - startedAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    public static string Score(LocalizationService l, double? value) =>
        value is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : l["MissionNoData"];

    public static string Percent(LocalizationService l, double? value) =>
        value is double v ? (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : l["MissionNoData"];

    // ------------------------------------------------------------------ labels

    public static string KpiLabel(LocalizationService l, string column) => column switch
    {
        "Backlog" => l["MissionKpiBacklog"],
        "Todo" => l["MissionKpiTodo"],
        "InProgress" => l["MissionKpiInProgress"],
        "Review" => l["MissionKpiReview"],
        "Blocked" => l["MissionKpiBlocked"],
        MissionControlService.ResolvedKpi => l["MissionKpiResolvedToday"],
        _ => column
    };

    /// <summary>The same friendly, localized priority names the board shows — the enum's
    /// "NiceToHave" is a code identifier, not a label a human reads in a table.</summary>
    public static string PriorityLabel(LocalizationService l, TicketPriority priority) => priority switch
    {
        TicketPriority.Idea => l["PriorityIdea"],
        TicketPriority.NiceToHave => l["PriorityNiceToHave"],
        TicketPriority.Required => l["PriorityRequired"],
        TicketPriority.Critical => l["PriorityCritical"],
        _ => priority.ToString()
    };

    public static string SeverityLabel(LocalizationService l, MissionSeverity severity) => severity switch
    {
        MissionSeverity.Critical => l["MissionSeverityCritical"],
        MissionSeverity.Warning => l["MissionSeverityWarning"],
        _ => l["MissionSeverityInfo"]
    };

    public static string ThroughputLabel(LocalizationService l, string metric) => metric switch
    {
        "CodeTicketsDone" => l["MissionCodeTicketsDone"],
        "PostsPublished" => l["MissionPostsPublished"],
        "MediaApproved" => l["MissionMediaApproved"],
        "DecisionsRecorded" => l["MissionDecisionsRecorded"],
        _ => metric
    };

    // ------------------------------------------------------------------ palette

    public static string KpiColor(string column) => column switch
    {
        "Backlog" => "var(--mc-purple)",
        "Todo" => "var(--mc-blue)",
        "InProgress" => "var(--mc-teal)",
        "Review" => "var(--mc-amber)",
        "Blocked" => "var(--mc-red)",
        "Scheduled" => "var(--mc-indigo)",
        "Done" => "var(--mc-green)",
        MissionControlService.ResolvedKpi => "var(--mc-green)",
        _ => "var(--border-strong)"
    };

    public static string SeverityColor(MissionSeverity severity) => severity switch
    {
        MissionSeverity.Critical => "var(--mc-red)",
        MissionSeverity.Warning => "var(--mc-amber)",
        _ => "var(--mc-blue)"
    };

    public static string EventColor(string kind) => kind switch
    {
        "gate" => "var(--mc-green)",
        "repair" => "var(--mc-amber)",
        "rereview" => "var(--mc-amber)",
        "costcap" => "var(--mc-red)",
        "merge" => "var(--mc-teal)",
        "run-failed" => "var(--mc-red)",
        "run-started" => "var(--mc-blue)",
        "run-completed" => "var(--mc-green)",
        _ => "var(--mc-purple)"
    };

    // Falling Backlog/Blocked is good news; falling Resolved is not. The arrow reports direction,
    // the colour reports whether the direction is welcome.
    public static string DeltaClass(string column, int delta)
    {
        if (delta == 0) return "flat";
        var lowerIsBetter = column is "Backlog" or "Blocked" or "Review";
        var good = lowerIsBetter ? delta < 0 : delta > 0;
        return good ? "up" : "down";
    }

    public static string DeltaArrow(int delta) => delta > 0 ? "▲" : delta < 0 ? "▼" : "▬";

    public static string StalenessClass(string staleness) => staleness switch
    {
        "very-stale" => "crit",
        "stale" => "warn",
        _ => ""
    };

    // ------------------------------------------------------------------ chart geometry

    // Chart geometry, in the mockup's 620×372 viewBox: plot area x∈[34,574], y∈[20,330].
    public static double PointX(int index, int count) =>
        count > 1 ? 34 + index * (540.0 / (count - 1)) : 34;

    public static double PointY(int value, int max) =>
        330 - value / (double)Math.Max(1, max) * 310;

    /// <summary>Renders one series as SVG polyline points, skipping days with no value — that is how
    /// the blocked series stays absent on days that were never snapshotted instead of drawing a
    /// zero nobody measured.</summary>
    public static string Polyline(IEnumerable<(int Index, int? Value)> points, int count, int max) =>
        string.Join(" ", points
            .Where(p => p.Value is not null)
            .Select(p => $"{Num(PointX(p.Index, count))},{Num(PointY(p.Value!.Value, max))}"));
}

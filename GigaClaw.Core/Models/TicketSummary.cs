namespace GigaClaw.Core.Models;

public record TicketSummary(
    int Id,
    string Title,
    string Description,
    string Status,
    TicketPriority Priority,
    int SortOrder,
    string? AssignedTo,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<Label> Labels,
    int CommentCount,
    DateTime? LastActivityAt,
    int? ParentId,
    List<SubTicketInfo> SubTickets)
{
    /// <summary>Auto-promotion instant for Scheduled tickets (UTC), null otherwise.</summary>
    public DateTime? FireAt { get; init; }

    /// <summary>Column a Scheduled ticket promotes to when it fires.</summary>
    public string? ScheduleTarget { get; init; }

    /// <summary>Cumulative tokens consumed by agent runs on this ticket (all token classes).</summary>
    public long AgentTokens { get; init; }

    /// <summary>Cumulative USD cost of agent runs on this ticket, as priced by the claude CLI.</summary>
    public double AgentCostUsd { get; init; }

    /// <summary>Tickets that must complete before this ticket can proceed.</summary>
    public List<TicketDependencyInfo> BlockedBy { get; init; } = [];

    /// <summary>Tickets that cannot proceed until this ticket completes.</summary>
    public List<TicketDependencyInfo> Blocks { get; init; } = [];

    /// <summary>
    /// Short (~60 char) reason extracted from the newest <c>GIGACLAW-GATE v1</c>/<c>GIGACLAW-REPAIR v1</c>
    /// comment on this ticket. Populated only for tickets in the Blocked status; null otherwise.
    /// See <see cref="Services.TicketService"/>'s blocked-reason extraction.
    /// </summary>
    public string? BlockedReason { get; init; }
}

public record SubTicketInfo(int Id, string Title, string Status, string? AssignedTo);

public record TicketDependencyInfo(int Id, string Title, string Status, string? AssignedTo);

public record TicketDependencies(
    IReadOnlyList<TicketDependencyInfo> BlockedBy,
    IReadOnlyList<TicketDependencyInfo> Blocks);

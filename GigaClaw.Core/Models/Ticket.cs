namespace GigaClaw.Core.Models;

public class Ticket
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Backlog";
    public TicketPriority Priority { get; set; } = TicketPriority.NiceToHave;
    public int SortOrder { get; set; }
    public string? AssignedTo { get; set; }
    public string? DeliverableType { get; set; }
    public string CreatedBy { get; set; } = "owner";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? ParentId { get; set; }

    /// <summary>
    /// When set (and Status == "Scheduled"), the ticket auto-promotes to <see cref="ScheduleTarget"/>
    /// once this UTC instant is reached. Cleared on promotion.
    /// </summary>
    public DateTime? FireAt { get; set; }

    /// <summary>
    /// Column the ticket moves to when <see cref="FireAt"/> fires. Defaults to "Todo".
    /// </summary>
    public string? ScheduleTarget { get; set; }

    /// <summary>
    /// Cumulative tokens consumed by agent runs on this ticket (input + output + cache read/write),
    /// accumulated at run completion. Durable — survives the 24h run-log purge.
    /// </summary>
    public long AgentTokens { get; set; }

    /// <summary>Cumulative USD cost of agent runs on this ticket, as priced by the claude CLI.</summary>
    public double AgentCostUsd { get; set; }

    /// <summary>
    /// R5 (doc/roadmap/lane-codex-runtime.md): the <c>ticket/&lt;id&gt;</c> branch backing this
    /// ticket's git worktree, once a <c>runAgent</c> action with <c>isolation: "worktree"</c> has
    /// dispatched against it. Null until then.
    /// </summary>
    public string? WorktreeBranch { get; set; }

    /// <summary>Absolute path to the ticket's git worktree checkout. See <see cref="WorktreeBranch"/>.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// <c>"active"</c> (in use or awaiting cleanup), <c>"cleaned"</c> (removed after the ticket
    /// reached Done with a merged branch), or <c>"dirty"</c> (reached Done but the worktree had
    /// uncommitted changes or an unmerged branch — flagged to the owner, never silently deleted).
    /// Null until a worktree is first created.
    /// </summary>
    public string? WorktreeStatus { get; set; }

    /// <summary>UTC instant <see cref="WorktreeStatus"/> was last set.</summary>
    public DateTime? WorktreeUpdatedAt { get; set; }

    public List<Comment> Comments { get; set; } = [];
    public List<ActivityEntry> Activities { get; set; } = [];
    public List<Label> Labels { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public List<TicketDependency> BlockedByEdges { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public List<TicketDependency> BlocksEdges { get; set; } = [];
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<SubTicketInfo> SubTickets { get; set; } = [];
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<TicketDependencyInfo> BlockedBy { get; set; } = [];
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<TicketDependencyInfo> Blocks { get; set; } = [];
}

/// <summary>
/// A directed dependency edge: <see cref="BlockingTicketId"/> must be completed before
/// <see cref="BlockedTicketId"/> can proceed.
/// </summary>
public sealed class TicketDependency
{
    public int BlockedTicketId { get; set; }
    public Ticket BlockedTicket { get; set; } = null!;
    public int BlockingTicketId { get; set; }
    public Ticket BlockingTicket { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

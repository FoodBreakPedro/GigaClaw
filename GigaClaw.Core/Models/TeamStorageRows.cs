namespace GigaClaw.Core.Models;

/// <summary>
/// Storage row for a project-scoped <see cref="TeamDefinition"/>. The denormalized display columns
/// exist so the team list can be read without parsing every payload; <see cref="Payload"/> is the
/// authoritative JSON.
/// </summary>
public sealed class TeamDefinitionRow
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Storage row for a <see cref="TeamRun"/>. See that type for the resumability contract.</summary>
public sealed class TeamRunRow
{
    public long Id { get; set; }
    public string TeamSlug { get; set; } = "";
    public int ParentTicketId { get; set; }
    public Ticket ParentTicket { get; set; } = null!;
    public TeamRunStatus Status { get; set; } = TeamRunStatus.Pending;
    /// <summary>JSON snapshot of the definition as it stood when the run was created.</summary>
    public string DefinitionSnapshot { get; set; } = "{}";
    public int? SynthesisTicketId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<TeamTaskRow> Tasks { get; set; } = [];
}

/// <summary>Storage row for a <see cref="TeamTask"/>. Blocking edges live in TicketDependencies.</summary>
public sealed class TeamTaskRow
{
    public long Id { get; set; }
    public long TeamRunId { get; set; }
    public TeamRunRow TeamRun { get; set; } = null!;
    public string TemplateKey { get; set; } = "";
    public string RoleId { get; set; } = "";
    public string AgentSlug { get; set; } = "";
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public TeamTaskStatus Status { get; set; } = TeamTaskStatus.Pending;
    /// <summary>JSON array of sibling template keys — provenance only, never the blocking truth.</summary>
    public string DependsOn { get; set; } = "[]";
    public string? ResultHandoffRef { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

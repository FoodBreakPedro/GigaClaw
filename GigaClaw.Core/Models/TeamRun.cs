namespace GigaClaw.Core.Models;

/// <summary>Lifecycle of a <see cref="TeamRun"/>. Terminal states are Completed, Failed and Cancelled.</summary>
public enum TeamRunStatus
{
    /// <summary>Created and persisted; no task has been dispatched yet.</summary>
    Pending,
    /// <summary>At least one task exists and the run is being worked.</summary>
    Running,
    /// <summary>Join policy satisfied; the synthesizer owns the run.</summary>
    Joining,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Lifecycle of a single <see cref="TeamTask"/>.</summary>
public enum TeamTaskStatus
{
    /// <summary>Sub-ticket exists; blocked or simply not dispatched yet.</summary>
    Pending,
    /// <summary>An agent run was started for the sub-ticket.</summary>
    Dispatched,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// One execution of a <see cref="TeamDefinition"/>, bound to the parent ticket that triggered it.
/// <para>
/// The run carries a <b>snapshot</b> of the definition it started from: a run stays resumable after
/// an engine restart even if the definition was edited or removed in the meantime, and the join
/// policy that was in force at fan-out is the one that decides the join. Everything a resume needs
/// lives in the project database — parent ticket, definition snapshot, task rows and the ticket
/// dependency edges — so no part of a run exists only in memory.
/// </para>
/// </summary>
public sealed record TeamRun(
    long Id,
    string TeamSlug,
    int ParentTicketId,
    TeamRunStatus Status,
    TeamDefinition Definition,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>Ticket holding the synthesizer's work, once the join produced one.</summary>
    public int? SynthesisTicketId { get; init; }

    /// <summary>Why the run failed or was cancelled. Null while healthy.</summary>
    public string? FailureReason { get; init; }

    public DateTime? CompletedAt { get; init; }

    public TeamJoinPolicy JoinPolicy => Definition.JoinPolicy;

    public string? SynthesizerRole => Definition.SynthesizerRole;

    /// <summary>True while the run may still change state — the set a restart must resume.</summary>
    public bool IsOpen => Status is TeamRunStatus.Pending or TeamRunStatus.Running or TeamRunStatus.Joining;
}

/// <summary>
/// A sub-ticket owned by a <see cref="TeamRun"/>: which role produced it, which ticket carries it,
/// and what it waits on.
/// <para>
/// Dependencies are <b>not</b> a second mechanism. <see cref="DependsOn"/> keeps the template-level
/// provenance (sibling task keys), while the blocking truth is the existing
/// <c>TicketDependencies</c> edge table — the same edges the board renders and
/// <c>dependenciesResolved</c> evaluates. <see cref="BlockedByTicketIds"/> is read back from those
/// edges, so a human editing dependencies on the board changes what the run waits on.
/// </para>
/// </summary>
public sealed record TeamTask(
    long Id,
    long TeamRunId,
    string TemplateKey,
    string RoleId,
    string AgentSlug,
    int TicketId,
    TeamTaskStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>Sibling task keys this task was fanned out behind.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Live blocking edges of the task's ticket, read from <c>TicketDependencies</c>.</summary>
    public IReadOnlyList<int> BlockedByTicketIds { get; init; } = [];

    /// <summary>Where the task's handoff artifact landed (see doc/handoff-contract.md). Set on completion.</summary>
    public string? ResultHandoffRef { get; init; }

    public string? FailureReason { get; init; }

    public DateTime? CompletedAt { get; init; }

    public bool IsOpen => Status is TeamTaskStatus.Pending or TeamTaskStatus.Dispatched;
}

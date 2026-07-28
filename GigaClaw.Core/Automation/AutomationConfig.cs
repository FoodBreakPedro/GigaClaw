using System.Text.Json.Serialization;

namespace GigaClaw.Core.Automation;

public sealed class AutomationConfig
{
    public List<Automation> Automations { get; set; } = new();
    public decimal? DailyBudgetUsd { get; set; }
    public int? MinDescriptionLength { get; set; }
}

public sealed class Automation
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required TriggerSpec Trigger { get; set; }
    public List<ConditionSpec> Conditions { get; set; } = new();
    public List<ActionSpec> Actions { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IntervalTriggerSpec), "interval")]
[JsonDerivedType(typeof(TicketInColumnTriggerSpec), "ticketInColumn")]
[JsonDerivedType(typeof(GitCommitTriggerSpec), "gitCommit")]
[JsonDerivedType(typeof(StatusChangeTriggerSpec), "statusChange")]
[JsonDerivedType(typeof(SubTicketStatusTriggerSpec), "subTicketStatus")]
[JsonDerivedType(typeof(BoardIdleTriggerSpec), "boardIdle")]
[JsonDerivedType(typeof(AgentInactivityTriggerSpec), "agentInactivity")]
[JsonDerivedType(typeof(TicketCommentAddedTriggerSpec), "ticketCommentAdded")]
public abstract class TriggerSpec
{
    public abstract string UiTypeKey { get; }
}

public sealed class IntervalTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "interval";
    public string? Cron { get; set; }
    /// <summary>Legacy fixed-interval seconds, pre-dating the cron-only model. Converted to an
    /// equivalent cron expression at trigger-build time if <see cref="Cron"/> is unset (see
    /// <c>IntervalTrigger.SecondsToCron</c>). The trigger editor UI no longer writes this field —
    /// new automations should always set <see cref="Cron"/>.</summary>
    public int? Seconds { get; set; }
}

public sealed class TicketInColumnTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "ticketInColumn";
    public int Seconds { get; set; } = 30;
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public int DebounceSeconds { get; set; } = 0;
    /// <summary>
    /// Maximum number of consecutive action-chain completions while a ticket remains
    /// unchanged in a matching column. Prevents a parked ticket from dispatching forever.
    /// A ticket edit or status transition resets the counter. Set to 0 to opt out.
    /// </summary>
    public int MaxConsecutiveFirings { get; set; } = 3;
    /// <summary>
    /// Minimum delay after either a successful or failed action chain before the same
    /// unchanged ticket may be dispatched again. This state is persisted across restarts.
    /// </summary>
    public int RetryBackoffSeconds { get; set; } = 30;
    /// <summary>
    /// Optional column to move the ticket to exactly once when the consecutive firing cap
    /// is reached. The ticket service validates that the configured column exists.
    /// </summary>
    public string? ExhaustedStatus { get; set; }
    /// <summary>Optional automation-authored comment added once when the cap is reached.</summary>
    public string? ExhaustedComment { get; set; }
}

public sealed class GitCommitTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "gitCommit";
    public int PollSeconds { get; set; } = 60;
    public List<string> IgnoreAuthors { get; set; } = new() { "noreply@anthropic.com" };
}

public sealed class StatusChangeTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "statusChange";
    public int PollSeconds { get; set; } = 30;
    public string? From { get; set; }
    public string? To { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class SubTicketStatusTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "subTicketStatus";
    public int PollSeconds { get; set; } = 30;
    public string? ParentColumn { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class BoardIdleTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "boardIdle";
    public int PollSeconds { get; set; } = 60;
    public List<string> IdleColumns { get; set; } = new() { "Done", "Review" };
}

public sealed class AgentInactivityTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "agentInactivity";
    public int PollSeconds { get; set; } = 60;
    public int MinutesIdle { get; set; } = 45;
}

public sealed class TicketCommentAddedTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "ticketCommentAdded";
    public int PollSeconds { get; set; } = 30;
    public List<string> Authors { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TicketInColumnConditionSpec), "ticketInColumn")]
[JsonDerivedType(typeof(MinDescriptionLengthConditionSpec), "minDescriptionLength")]
[JsonDerivedType(typeof(FieldLengthConditionSpec), "fieldLength")]
[JsonDerivedType(typeof(PriorityConditionSpec), "priority")]
[JsonDerivedType(typeof(LabelsConditionSpec), "labels")]
[JsonDerivedType(typeof(AssignedToConditionSpec), "assignedTo")]
[JsonDerivedType(typeof(TicketAgeConditionSpec), "ticketAge")]
[JsonDerivedType(typeof(HasParentConditionSpec), "hasParent")]
[JsonDerivedType(typeof(AllSubTicketsInStatusConditionSpec), "allSubTicketsInStatus")]
[JsonDerivedType(typeof(TicketCountInColumnConditionSpec), "ticketCountInColumn")]
public abstract class ConditionSpec
{
    public abstract string UiTypeKey { get; }
    /// <summary>When true, the condition result is inverted (NOT logic).</summary>
    public bool Negate { get; set; }
}

public sealed class TicketInColumnConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "ticketInColumn";
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
}

/// <summary>Kept for backward-compat with existing automations.json files.</summary>
public sealed class MinDescriptionLengthConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "minDescriptionLength";
    public int Length { get; set; } = 50;
}

public sealed class FieldLengthConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "fieldLength";
    /// <summary>"title" or "description"</summary>
    public string Field { get; set; } = "description";
    /// <summary>"min" or "max"</summary>
    public string Mode { get; set; } = "min";
    public int Length { get; set; } = 50;
}

public sealed class PriorityConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "priority";
    public List<string> Priorities { get; set; } = new();
}

public sealed class LabelsConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "labels";
    /// <summary>Ticket must have at least one of these labels.</summary>
    public List<string> Labels { get; set; } = new();
}

public sealed class AssignedToConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "assignedTo";
    /// <summary>Matches if ticket is assigned to one of these slugs. Empty = unassigned.</summary>
    public List<string> Slugs { get; set; } = new();
}

public sealed class HasParentConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "hasParent";
    /// <summary>true = ticket must have a parent; false = ticket must be a root ticket.</summary>
    public bool Value { get; set; }
}

/// <summary>
/// Matches if the firing ticket has sub-tickets AND every sub-ticket's status is in <see cref="Statuses"/>.
/// A ticket with zero sub-tickets does NOT match (safer default — otherwise every leaf ticket would match).
/// </summary>
public sealed class AllSubTicketsInStatusConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "allSubTicketsInStatus";
    public List<string> Statuses { get; set; } = new() { "Done" };
}

/// <summary>
/// Generic count-based condition: matches if the number of tickets assigned to a given member
/// (or the firing ticket's assignee when <see cref="SameAssignee"/>) in the listed columns
/// satisfies the operator/value comparison. Generalizes NoPendingTickets (which is
/// equivalent to Operator="==" Value=0).
/// </summary>
public sealed class TicketCountInColumnConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "ticketCountInColumn";
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public bool SameAssignee { get; set; }
    /// <summary>One of "==", "!=", "&lt;", "&lt;=", "&gt;", "&gt;=".</summary>
    public string Operator { get; set; } = "==";
    public int Value { get; set; }
}

public sealed class TicketAgeConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "ticketAge";
    /// <summary>"createdAt" or "updatedAt"</summary>
    public string Field { get; set; } = "createdAt";
    /// <summary>"olderThan" or "newerThan"</summary>
    public string Mode { get; set; } = "olderThan";
    public int Hours { get; set; } = 24;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunAgentActionSpec), "runAgent")]
[JsonDerivedType(typeof(MoveTicketStatusActionSpec), "moveTicketStatus")]
[JsonDerivedType(typeof(SetLabelsActionSpec), "setLabels")]
[JsonDerivedType(typeof(AssignTicketActionSpec), "assignTicket")]
[JsonDerivedType(typeof(AddCommentActionSpec), "addComment")]
[JsonDerivedType(typeof(CommitAgentMemoryActionSpec), "commitAgentMemory")]
[JsonDerivedType(typeof(ConsolidateAgentMemoryActionSpec), "consolidateAgentMemory")]
[JsonDerivedType(typeof(ExecutePowerShellActionSpec), "executePowerShell")]
[JsonDerivedType(typeof(CreateTicketActionSpec), "createTicket")]
[JsonDerivedType(typeof(HttpRequestActionSpec), "httpRequest")]
public abstract class ActionSpec
{
    public abstract string UiTypeKey { get; }
}

public sealed class RunAgentActionSpec : ActionSpec
{
    public override string UiTypeKey => "runAgent";
    /// <summary>
    /// Name of the agent to run. Must match a member slug in the project.
    /// Resolved to <c>.agents/{Agent}/SKILL.md</c> at dispatch time.
    /// </summary>
    public required string Agent { get; set; }
    public int MaxTurns { get; set; } = 200;
    public string? ConcurrencyGroup { get; set; }
    /// <summary>Dead man's switch: if the run holding this concurrency group emits no activity for
    /// this many minutes, the reaper force-releases the lock. Null (default) disables the timeout.
    /// Guards against a hung subprocess that never returns nor throws (see ticket #98).</summary>
    public int? LockTimeoutMinutes { get; set; }
    public List<string> MutuallyExclusiveWith { get; set; } = new();
    public string? Context { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Model { get; set; }
    public bool RestoreStatusOnFail { get; set; } = true;
}

public sealed class MoveTicketStatusActionSpec : ActionSpec
{
    public override string UiTypeKey => "moveTicketStatus";
    public required string To { get; set; }
}

public sealed class SetLabelsActionSpec : ActionSpec
{
    public override string UiTypeKey => "setLabels";
    /// <summary>Label names to add to the ticket.</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Label names to remove from the ticket.</summary>
    public List<string> Remove { get; set; } = new();
}

public sealed class AssignTicketActionSpec : ActionSpec
{
    public override string UiTypeKey => "assignTicket";
    /// <summary>Member slug to assign. Empty or null to unassign. Supports {previousAssignee} placeholder.</summary>
    public string? Slug { get; set; }
}

public sealed class AddCommentActionSpec : ActionSpec
{
    public override string UiTypeKey => "addComment";
    /// <summary>Comment content. Supports placeholders: {ticketId}, {ticketTitle}, {assignee}.</summary>
    public string Content { get; set; } = "";
    /// <summary>Author of the comment (member slug).</summary>
    public string Author { get; set; } = "";
}

/// <summary>Git-commits the given agent's memory (the .agents/{agent}/memory/ topic layout and/or
/// the legacy flat memory.md) after a run.</summary>
public sealed class CommitAgentMemoryActionSpec : ActionSpec
{
    public override string UiTypeKey => "commitAgentMemory";
    public required string Agent { get; set; }
}

/// <summary>
/// Spawns a focused claude pass whose only job is to distill lessons from the parent run
/// into the agent's memory (the .agents/{agent}/memory/ topic layout). Instructions are read
/// from an external markdown file so they can be tweaked without rebuilding.
/// </summary>
public sealed class ConsolidateAgentMemoryActionSpec : ActionSpec
{
    public override string UiTypeKey => "consolidateAgentMemory";
    /// <summary>Agent slug. Supports {assignee} placeholder.</summary>
    public required string Agent { get; set; }
    /// <summary>Max turns for the consolidation pass.</summary>
    public int MaxTurns { get; set; } = 5;
    /// <summary>Path to the instruction markdown file, relative to workspace root.</summary>
    public string InstructionFile { get; set; } = ".agents/memory-consolidation.md";
}

/// <summary>
/// Creates a new ticket in the project. Works without a triggering ticket (interval, cron, board-idle, …).
/// Supports date placeholders in Title and Description: {date} (today), {monday} (Monday of current week), {firstOfMonth}.
/// When <see cref="SkipIfExists"/> is true (default), creation is skipped if an open ticket with the resolved title already exists.
/// </summary>
public sealed class CreateTicketActionSpec : ActionSpec
{
    public override string UiTypeKey => "createTicket";
    /// <summary>Ticket title. Supports {date}, {monday}, {firstOfMonth}.</summary>
    public string Title { get; set; } = "";
    /// <summary>Ticket description (optional). Supports {date}, {monday}, {firstOfMonth}.</summary>
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Todo";
    public string? AssignedTo { get; set; }
    public string Priority { get; set; } = "NiceToHave";
    /// <summary>Label names to attach to the new ticket.</summary>
    public List<string> Labels { get; set; } = new();
    public int? ParentId { get; set; }
    public string CreatedBy { get; set; } = "automation";
    /// <summary>Skip creation if an open ticket with the same resolved title already exists.</summary>
    public bool SkipIfExists { get; set; } = true;
}

/// <summary>
/// Runs a PowerShell script or file with optional arguments and timeout.
/// <para>
/// <c>Script</c>/<c>ScriptFile</c> and every entry in <c>Arguments</c> are templated with
/// <c>{ticketId}</c>, <c>{ticketTitle}</c>, <c>{slug}</c>, plus any chain value published by an
/// earlier action in the same chain (e.g. <c>{http.body.adminUrl}</c> from a preceding
/// <c>httpRequest</c>). <c>{draft.*}</c> is deliberately NOT wired here — those values are
/// JSON-escaped for splicing into an httpRequest <c>BodyTemplate</c> and are not safe to hand to a
/// shell verbatim; a script that needs the draft should fetch and parse the ticket itself via the
/// GigaClaw API. On completion (success, non-zero exit, or timeout) the trimmed stdout and exit
/// code are published back into the chain as <c>{powershell.stdout}</c> (capped at 4 KB) and
/// <c>{powershell.exitCode}</c>, so a later <c>addComment</c> can report the script's outcome —
/// mirroring how <c>httpRequest</c> publishes <c>{http.status}</c>/<c>{http.body}</c>.
/// </para>
/// </summary>
public sealed class ExecutePowerShellActionSpec : ActionSpec
{
    public override string UiTypeKey => "executePowerShell";
    public string Script { get; set; } = "";
    public string? ScriptFile { get; set; }
    public List<string> Arguments { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
    public bool AbortOnFailure { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
}

/// <summary>
/// Performs an outbound HTTP request (webhook / CMS publish / n8n kick-off) and captures the
/// response into the action chain so later actions can template it.
/// <para>
/// <see cref="Url"/>, <see cref="BodyTemplate"/> and every header value support the standard
/// chain placeholders (<c>{ticketId}</c>, <c>{ticketTitle}</c>, <c>{slug}</c>/<c>{projectSlug}</c>
/// — both name the firing project's slug) plus any chain values captured by earlier actions.
/// After the request completes, the executor publishes <c>{http.status}</c>, <c>{http.body}</c>
/// (raw, trimmed) and one <c>{http.body.&lt;field&gt;}</c> per first-level field of a JSON object
/// response, so an <c>addComment</c> later in the chain can write the receipt back onto the ticket.
/// </para>
/// <para>
/// <b>Draft frontmatter (AD-7).</b> When <see cref="BodyTemplate"/> references any
/// <c>{draft.*}</c> placeholder, the executor fetches the firing ticket's description and parses
/// it as <see cref="DraftFrontmatter"/> before sending the request:
/// <c>{draft.title}</c>, <c>{draft.slug}</c>, <c>{draft.excerpt}</c>, <c>{draft.contentType}</c>,
/// <c>{draft.imagePrompt}</c>, <c>{draft.seo.title}</c>, <c>{draft.seo.description}</c>,
/// <c>{draft.seo.primaryKeyword}</c>, <c>{draft.body}</c>. Every <c>{draft.*}</c> value is
/// JSON-string-escaped (quotes/backslashes/newlines) before substitution, since it is meant to be
/// spliced between a literal <c>"</c> pair in a JSON body — do not use it in <see cref="Url"/> or
/// <see cref="Headers"/>. <c>{draft.*}</c> placeholders are only recognized in
/// <see cref="BodyTemplate"/>; they are left verbatim anywhere else.
/// </para>
/// <para>
/// If the description has no valid frontmatter (missing fence or missing <c>title</c>), the
/// request is never sent — this action fails the same way a non-2xx response would (readable
/// error in the run log, <see cref="AbortOnFailure"/> and <see cref="FailureComment"/>/
/// <see cref="FailureStatus"/> honored) rather than POSTing a malformed draft.
/// </para>
/// </summary>
public sealed class HttpRequestActionSpec : ActionSpec
{
    /// <summary>Name of the <c>IHttpClientFactory</c> client this action uses. Registered by the
    /// host (see GigaClaw.Web/Program.cs) so its handler pipeline stays independent of the app's
    /// other outbound HTTP.</summary>
    public const string HttpClientName = "gigaclaw-automation";

    public override string UiTypeKey => "httpRequest";
    /// <summary>Absolute request URL. Supports placeholders.</summary>
    public string Url { get; set; } = "";
    /// <summary>HTTP verb. Defaults to POST; anything <see cref="System.Net.Http.HttpMethod"/> accepts works.</summary>
    public string Method { get; set; } = "POST";
    /// <summary>Extra request headers. Values support placeholders.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Request body. Sent as <c>application/json</c> unless a Content-Type header is set. Supports placeholders, including <c>{draft.*}</c> (see class remarks).</summary>
    public string BodyTemplate { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Abort the remaining actions in the chain when the request fails (non-2xx, timeout, transport error, or — when <c>{draft.*}</c> is used — a frontmatter parse failure).</summary>
    public bool AbortOnFailure { get; set; }
    /// <summary>
    /// Name of an environment variable on the GigaClaw server process holding a bearer token.
    /// Resolved at execution time and injected as <c>Authorization: Bearer &lt;value&gt;</c> unless an
    /// Authorization header is already present. Only the variable NAME is stored in automations.json —
    /// the secret itself is never persisted and never logged. A missing variable logs a warning and
    /// the request proceeds unauthenticated.
    /// </summary>
    public string? SecretRef { get; set; }
    /// <summary>
    /// Optional comment posted (author <c>"automation"</c>) when the request fails — non-2xx,
    /// timeout/transport error, or a <c>{draft.*}</c> frontmatter parse failure. Independent of
    /// <see cref="AbortOnFailure"/> (posted either way) and only when the firing has a ticket.
    /// Supports the standard chain placeholders plus <c>{http.status}</c>, <c>{http.body}</c> and
    /// <c>{http.error}</c> (a short, human-readable failure reason: the parse error, the timeout
    /// message, the transport exception message, or <c>"HTTP &lt;status&gt;"</c>).
    /// </summary>
    public string? FailureComment { get; set; }
    /// <summary>
    /// Optional ticket status to move to when the request fails, applied after
    /// <see cref="FailureComment"/> is posted. Only when the firing has a ticket.
    /// </summary>
    public string? FailureStatus { get; set; }
}

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
[JsonDerivedType(typeof(GitHubPrCommentTriggerSpec), "githubPrComment")]
[JsonDerivedType(typeof(GitHubCheckStatusTriggerSpec), "githubCheckStatus")]
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

/// <summary>
/// C7 part 2 (see <c>doc/github-surface.md</c>). Fires when a pull-request review comment from a
/// configured owner login arrives for a ticket's PR. Inert unless the project has a GitHub
/// configuration, a token, and at least one owner login — this trigger cannot enable itself.
/// </summary>
public sealed class GitHubPrCommentTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "githubPrComment";
    public int PollSeconds { get; set; } = 120;
    /// <summary>
    /// Optional narrowing of the project's owner logins. An automation lives in the workspace and
    /// is therefore agent-editable, so this list can only intersect the trusted one in settings —
    /// never add to it.
    /// </summary>
    public List<string> OwnerLogins { get; set; } = new();
}

/// <summary>
/// C7 part 3 (see <c>doc/github-surface.md</c>). The <c>gitCommit</c> family's CI sibling: fires
/// when a GitHub check run reaches one of <see cref="Conclusions"/> for the commit under watch.
/// Like <see cref="GitCommitTriggerSpec"/> it is about a commit, so by default it watches the
/// workspace's own HEAD.
/// </summary>
public sealed class GitHubCheckStatusTriggerSpec : TriggerSpec
{
    public override string UiTypeKey => "githubCheckStatus";
    public int PollSeconds { get; set; } = 120;
    /// <summary>Conclusions worth firing on. Empty means every concluded check run.</summary>
    public List<string> Conclusions { get; set; } = new() { "failure" };
    /// <summary>Branch or SHA to watch. Null (the default) means the workspace's own HEAD.</summary>
    public string? Ref { get; set; }
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
[JsonDerivedType(typeof(VerdictIsConditionSpec), "verdictIs")]
[JsonDerivedType(typeof(RepairBudgetConditionSpec), "repairBudget")]
[JsonDerivedType(typeof(DependenciesResolvedConditionSpec), "dependenciesResolved")]
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

/// <summary>
/// Gates on the newest verdict posted to the firing ticket (see <c>doc/verdict-contract.md</c>).
/// <para>
/// <see cref="Verdicts"/> lists the outcomes that match. Besides the three decisions
/// (<c>SHIP</c>, <c>FIX</c>, <c>BLOCK</c>) it accepts three routing outcomes so a reviewer that
/// answers with prose fails loudly instead of looking un-reviewed: <c>MISSING</c> (no verdict
/// comment), <c>INVALID</c> (a verdict that violates the contract) and <c>STALE</c> (a valid
/// verdict whose artifact changed after it was written). An escalation automation therefore reads
/// <c>["BLOCK", "INVALID", "STALE"]</c>, a gate reads <c>["SHIP"]</c>, and the repair loop reads
/// <c>["FIX"]</c>. Unknown entries never match — the gate fails closed.
/// </para>
/// </summary>
public sealed class VerdictIsConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "verdictIs";
    public List<string> Verdicts { get; set; } = new() { "SHIP" };
    /// <summary>Only consider verdicts claimed by this agent. Supports <c>{assignee}</c>. Empty = any agent.</summary>
    public string? Agent { get; set; }
    /// <summary>
    /// Re-hash the artifact the verdict names in its <c>path</c> evidence and treat the verdict as
    /// <c>STALE</c> unless one of them still matches <c>inputDigest</c>. Turn this off only for
    /// reviewers whose input is not a workspace file.
    /// </summary>
    public bool RequireFreshArtifact { get; set; } = true;
}

/// <summary>
/// The cap half of the bounded repair loop (see <c>doc/verdict-contract.md</c>). Pairs with
/// <c>verdictIs: ["FIX"]</c>: one automation carries <c>mode: "withinCap"</c> and re-dispatches the
/// producing agent, its twin carries <c>mode: "exhausted"</c> and escalates the ticket to the owner.
/// <para>
/// The number of rounds already spent is recounted from the ticket's comment trail on every
/// evaluation — one per FIX verdict since the last SHIP, BLOCK or escalation receipt — so it
/// survives an engine restart and a resumed run cannot restart it. On its own this condition says
/// nothing about whether a repair is outstanding (a fresh ticket is trivially "within cap"); it is
/// meaningful only next to <c>verdictIs</c>.
/// </para>
/// </summary>
public sealed class RepairBudgetConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "repairBudget";

    /// <summary><c>withinCap</c> (another round is allowed) or <c>exhausted</c> (escalate).
    /// Anything else matches nothing, so a typo stalls the ticket instead of looping it.</summary>
    public string Mode { get; set; } = "withinCap";

    /// <summary>Only count verdicts from this reviewer. Supports <c>{assignee}</c>. Empty = any reviewer.</summary>
    public string? Agent { get; set; }

    /// <summary>
    /// Explicit cap. Null (default) reads <c>maxReviewCycles</c> from the workspace's
    /// <c>.agents/contracts.json</c> — the ticket's assignee first, then the reviewer named in
    /// <see cref="Agent"/>, then the manifest defaults — and falls back to
    /// <see cref="Verdicts.RepairLoop.DefaultMaxCycles"/> when the manifest is silent. A manifest
    /// that is present but unreadable resolves to "exhausted": an unknowable budget escalates
    /// rather than loops.
    /// </summary>
    public int? MaxCycles { get; set; }
}

/// <summary>
/// Matches when every ticket the firing ticket is <c>blockedBy</c> has reached one of
/// <see cref="ResolvedStatuses"/>. A ticket with no dependency edges matches — nothing is
/// blocking it. (This is the opposite default from <see cref="AllSubTicketsInStatusConditionSpec"/>,
/// where "no sub-tickets" must not look like "all sub-tickets finished".)
/// <para>
/// Only direct blockers are checked. A transitive blocker cannot be an issue while the ticket
/// between them is unresolved, and once that one is Done its own blockers are irrelevant.
/// </para>
/// </summary>
public sealed class DependenciesResolvedConditionSpec : ConditionSpec
{
    public override string UiTypeKey => "dependenciesResolved";
    public List<string> ResolvedStatuses { get; set; } = new() { "Done" };
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
[JsonDerivedType(typeof(StartTeamRunActionSpec), "startTeamRun")]
[JsonDerivedType(typeof(ParallelRunAgentsActionSpec), "parallelRunAgents")]
[JsonDerivedType(typeof(EnqueueMergeActionSpec), "enqueueMerge")]
[JsonDerivedType(typeof(StartWorkflowActionSpec), "startWorkflow")]
[JsonDerivedType(typeof(OpenPullRequestActionSpec), "openPullRequest")]
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
    /// <summary>
    /// R5 (doc/roadmap/lane-codex-runtime.md): when set to <c>"worktree"</c>, the dispatch runs in
    /// a per-ticket git worktree (branch <c>ticket/&lt;id&gt;</c>) instead of the shared workspace —
    /// see <c>WorktreeManager</c>. Absent, null, or any other value means no isolation — the current,
    /// pre-R5 behavior of executing directly in the workspace. A firing with no ticket cannot be
    /// isolated (there is no `&lt;id&gt;` to key the worktree on) and fails the dispatch closed rather
    /// than silently running in place.
    /// </summary>
    public string? Isolation { get; set; }
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
    /// <summary>Comment content. Supports placeholders: {ticketId}, {ticketTitle}, {assignee}, plus
    /// {checkName}/{checkConclusion} on a firing produced by the githubCheckStatus trigger.</summary>
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
/// Starts a <c>TeamRun</c> of the named team definition against the firing ticket, which becomes
/// the run's parent. The run fans out one sub-ticket per task template, assigned to the role's
/// agent, with the template's <c>dependsOn</c> materialized as ordinary ticket dependency edges —
/// see <c>doc/executable-teams.md</c>.
/// <para>
/// The action is <b>idempotent per (ticket, team)</b>: firing again while the run is still open
/// re-attaches to it instead of fanning out a second set of sub-tickets, so it is safe under a
/// repeating <c>ticketInColumn</c> trigger. A filter-only team (empty task graph) is refused —
/// there is nothing to execute.
/// </para>
/// The action only <i>starts</i> the run. Ordering, release and cancellation are then driven from
/// the board by <c>TeamRunService</c>; dispatch itself stays the job of the ordinary per-agent
/// automations that watch the dispatch column.
/// </summary>
public sealed class StartTeamRunActionSpec : ActionSpec
{
    public override string UiTypeKey => "startTeamRun";

    /// <summary>Slug of the team definition to run. A project-scoped definition wins over the
    /// built-in team of the same slug.</summary>
    public required string Team { get; set; }
}

/// <summary>
/// Opens a walk of the workspace's workflow graph (<c>.agents/workflow.json</c>) on the firing
/// ticket — how a ticket opts in to a declared workflow. See <c>doc/workflow-graph.md</c>.
/// <para>
/// The action only <b>records intent</b>, exactly as <see cref="EnqueueMergeActionSpec"/> does: it
/// writes the walk's opening receipt onto the ticket and returns. The engine's own poll
/// (<c>WorkflowWalker.ReconcileProjectAsync</c>, run each tick beside the team-run reconcile) is what
/// enters the first state and every one after it — so the walk advances on restart, on a verdict
/// posted an hour later, and on a branch finishing, none of which are this automation's firing.
/// </para>
/// <para>
/// Idempotent per ticket: firing again while a walk is still running re-attaches to it rather than
/// restarting one, mirroring <see cref="StartTeamRunActionSpec"/>'s contract, so it is safe under a
/// repeating <c>ticketInColumn</c> trigger. A ticket whose walk parked or finished may be walked
/// again — that is how an owner re-runs a workflow after fixing what parked it.
/// </para>
/// </summary>
public sealed class StartWorkflowActionSpec : ActionSpec
{
    public override string UiTypeKey => "startWorkflow";

    /// <summary>
    /// State to begin at. Null (the default) means the graph's own entry state — naming one is for
    /// resuming a workflow partway, and a name that is not a state of the graph is refused with a
    /// receipt rather than silently falling back to the entry.
    /// </summary>
    public string? At { get; set; }
}

/// <summary>
/// One branch of a <see cref="ParallelRunAgentsActionSpec"/>: an agent and what to ask it.
/// </summary>
public sealed class ParallelBranchSpec
{
    /// <summary>Agent slug the branch is dispatched to. Must be a member of the project.</summary>
    public string Agent { get; set; } = "";

    /// <summary>
    /// Prompt for the branch, which becomes its sub-ticket's description. Empty means "the agent
    /// reads the parent ticket", the same as a task template with no prompt.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>Sub-ticket title. Defaults to the agent slug when empty.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Stable identity of the branch inside the run. Defaults to the agent slug, which is enough
    /// until the same agent runs two branches — then name them, or the second one collides.
    /// </summary>
    public string? Key { get; set; }
}

/// <summary>
/// Fans the firing ticket out into declared parallel branches, joins them by policy, and optionally
/// hands the result to a synthesizer. The engine-level generalization of <c>startTeamRun</c>: the
/// branches are written inline in the automation instead of being looked up as a stored
/// <c>TeamDefinition</c>.
/// <para>
/// It is <b>implemented as</b> a team run. The action materializes an ad-hoc
/// <c>TeamDefinition</c> — one role and one task template per branch — and starts it through
/// <c>TeamRunService</c>. A run already carries its own definition snapshot for restart-safe resume,
/// so an inline definition needs no stored row and resumes on the same terms as a named team. Every
/// C4 guarantee therefore applies unchanged: fan-out to sub-tickets, join policy, cancellation
/// propagation, synthesize-with-gaps, and the restart reconcile.
/// </para>
/// <para>
/// Branches are <b>not</b> dispatched by this action. Each one is a sub-ticket in the dispatch
/// column, started by the ordinary per-agent <c>ticketInColumn</c> automation — which is what makes
/// a branch queue behind <c>RunConcurrencyGate</c> and take its file leases like any other run.
/// <see cref="MaxConcurrency"/> is a second, per-run ceiling on top of that, not a replacement.
/// </para>
/// Idempotent per (ticket, <see cref="RunSlug"/>): firing again while the run is open re-attaches
/// instead of fanning out a second set of branches.
/// </summary>
public sealed class ParallelRunAgentsActionSpec : ActionSpec
{
    public override string UiTypeKey => "parallelRunAgents";

    /// <summary>The branches to run in parallel. An empty list is a no-op with a receipt.</summary>
    public List<ParallelBranchSpec> Branches { get; set; } = new();

    /// <summary>
    /// Most branches allowed in the dispatch column at once. <c>0</c> (default) is unlimited and
    /// leaves the host-wide <c>RunConcurrencyGate</c> as the only ceiling.
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// <c>allDone</c> (default), <c>quorum</c> or <c>firstFailure</c>. An unrecognized value is
    /// refused when the action runs rather than silently read as all-done.
    /// </summary>
    public string Join { get; set; } = "allDone";

    /// <summary>Successful branches <c>quorum</c> waits for. Ignored by the other modes.</summary>
    public int Quorum { get; set; }

    /// <summary>
    /// <c>synthesize</c> (default) hands the synthesizer what reported plus a named list of the
    /// gaps; <c>failFast</c> skips synthesis and closes the run failed. Both leave a receipt on the
    /// parent ticket. Only meaningful together with <see cref="Synthesizer"/>.
    /// </summary>
    public string OnPartialFailure { get; set; } = "synthesize";

    /// <summary>
    /// Agent that synthesizes the join result. Null means the run finishes at the join with no
    /// synthesis step.
    /// </summary>
    public string? Synthesizer { get; set; }

    /// <summary>
    /// Slug the ad-hoc run is recorded under, and the key idempotency is measured on. Defaults to
    /// <c>parallel-run</c>; give two parallel actions on the same ticket different slugs or the
    /// second will re-attach to the first one's run instead of starting its own.
    /// </summary>
    public string RunSlug { get; set; } = "parallel-run";

    /// <summary>Human-readable name of the ad-hoc team, used in receipts and the synthesis brief.</summary>
    public string? Name { get; set; }
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

/// <summary>
/// R6 (doc/roadmap/lane-codex-runtime.md, Task R6): enqueues the firing ticket's R5 worktree branch
/// (<c>ticket/&lt;id&gt;</c>) onto the project's durable merge queue (<c>MergeQueueStore</c>). The
/// action itself only records intent — it never rebases or merges inline, because the whole point
/// of a queue is that candidates land one at a time, serialized by <c>MergeQueueProcessor</c>
/// (a hosted background service, the same shape as <c>FileLeaseReaper</c>) rather than by whichever
/// automation chain happens to fire first.
/// <para>
/// <b>Trust gate.</b> Landing a branch is a stronger action than writing a ticket label — an agent
/// with board-write could set a "ready-to-merge" label on its own ticket, so a label is never
/// treated as authorization here (see <c>MergeApprovalGate</c>, modeled on R3's
/// <c>OutboundApprovalGate</c>). Without the project's slug listed in the owner's
/// <c>settings.json</c> (<see cref="Services.AppSettingsService.GetApprovedMergeProjects"/>), the
/// entry is enqueued in a <c>held</c> state and a <c>merge-held/v1</c> receipt is written — nothing
/// merges until the owner approves the project, and approval is re-read on every poll so flipping it
/// takes effect without an engine restart.
/// </para>
/// <para>
/// Re-firing this action against a ticket that already has an active (queued/held/merging) entry is
/// idempotent — it returns the existing entry rather than enqueuing a duplicate — so it is safe
/// under a repeating <c>ticketInColumn</c> trigger, mirroring <c>StartTeamRunActionSpec</c>'s
/// idempotent-per-ticket contract.
/// </para>
/// </summary>
public sealed class EnqueueMergeActionSpec : ActionSpec
{
    public override string UiTypeKey => "enqueueMerge";

    /// <summary>
    /// Shell command run in the candidate's rebased worktree before it merges. Null/blank falls
    /// back to the project's own <see cref="Models.Project.IntegrationCommand"/>; when both are
    /// unset the integration step is skipped and the skip is recorded on the merge receipt (never
    /// silently treated as green). Snapshotted onto the queue entry at enqueue time, not re-resolved
    /// at merge time, so a queued candidate's gate cannot shift out from under it mid-queue.
    /// </summary>
    public string? IntegrationCommand { get; set; }
}

/// <summary>
/// U6 follow-up (<c>doc/roadmap/U6-EVIDENCE.md</c> "What remains" #2): pushes the firing ticket's R5
/// worktree branch and opens (or re-finds) its pull request via
/// <see cref="Github.GitHubPullRequestService.OpenForTicketAsync"/>. The natural home is beside
/// <see cref="EnqueueMergeActionSpec"/> in a <c>verdict-gate-*</c> chain: <c>verdict SHIP → open PR
/// → wait for CI → enqueue merge</c>.
/// <para>
/// The action only <b>records intent</b>, exactly like <see cref="EnqueueMergeActionSpec"/>: it pushes
/// the branch, opens or re-finds the pull request, and returns — CI, review and the merge queue are
/// each driven by their own poll, never by this action re-firing.
/// </para>
/// <para>
/// <b>Fails closed, never throws.</b> A project with no GitHub remote/token configured, a ticket
/// never dispatched with <c>isolation: "worktree"</c>, or a policy refusal on the push/API host are
/// all ordinary outcomes of <see cref="Github.GitHubPullRequestService"/> — it returns a result
/// rather than throwing, and this action records why on the ticket rather than raising. Every
/// policy-gate refusal already writes its own <c>outbound-denial/v1</c> receipt (the service does
/// that); this action adds the one case the service leaves silent — "not configured at all" — so a
/// project that never opted in costs nothing and still leaves a visible reason.
/// </para>
/// </summary>
public sealed class OpenPullRequestActionSpec : ActionSpec
{
    public override string UiTypeKey => "openPullRequest";
}

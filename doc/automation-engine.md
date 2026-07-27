# Automation engine

## Purpose
Background service that watches each project for events and dispatches agents in response. Drives the agentic workflow: when a ticket moves, a comment is posted, a commit lands, an interval elapses, etc., the engine evaluates configured automations and runs the matching actions.

## Key components
- `GigaClaw.Core/Automation/AutomationEngine.cs` — top-level wiring only; delegates to `TriggerHandler` and `RunStateManager`.
- `GigaClaw.Core/Automation/TriggerHandler.cs` — owns the tick loop (urgent drain + per-project poll).
- `GigaClaw.Core/Automation/RunStateManager.cs` — encapsulates the 5 dispatch-gate checks (`ShouldSkipAsync`); shared by `AutomationEngine` and `ActionExecutor`.
- `GigaClaw.Core/Automation/ActionExecutor.cs` — condition evaluation and all `Execute*Async` action implementations; delegates skip checks to `RunStateManager`. Holds `_inFlightChains` (`ConcurrentDictionary` keyed by `"{automationId}:{ticketId}"`) to serialize action chains per (automation, ticket) pair — a second firing is dropped while one is already in progress.
- `GigaClaw.Core/Automation/ProjectRuntimeManager.cs` — per-project runtime dictionary and signal fan-out.
- `GigaClaw.Core/Automation/ProjectRuntime.cs` — data class holding per-project run state.
- `GigaClaw.Core/Automation/AutomationConfig.cs` — JSON-deserialized automation definitions (triggers, conditions, actions).
- `GigaClaw.Core/Automation/AutomationStore.cs` — loads/persists `automations.json` from each workspace's `.agents/` folder.
- `GigaClaw.Core/Automation/Triggers/` — trigger implementations.
- `GigaClaw.Core/Automation/GitRepositoryWatcher.cs` — backs the `gitCommit` trigger.
- `GigaClaw.Core/Automation/RunConcurrencyGate.cs` — serializes runs sharing a `concurrencyGroup`.
- `GigaClaw.Core/Automation/ConcurrencyLockReaper.cs` — background service polling every 30s; force-stops any `runAgent` run that has been idle past its `RunAgentActionSpec.LockTimeoutMinutes` (opt-in per-automation). Idleness is measured via `AgentRun.LastActivityAt` (heartbeat updated on every streamed event). Prevents a hung subprocess from holding a `concurrencyGroup` lock forever and starving later dispatches.
- `GigaClaw.Core/Automation/TriggerStateStore.cs` — persists each interval/cron automation's next scheduled fire time (`NextRunAt`) in the per-project SQLite DB (`automation_trigger_state` table). Computed once at registration and saved immediately (not recomputed from "now" on every tick), so a restart that straddles the scheduled moment still fires on time; a missed occurrence catches up with a single immediate fire on the next tick.

## Model
- **Triggers**: `interval`, `ticketInColumn`, `statusChange`, `subTicketStatus`, `ticketCommentAdded`, `gitCommit`, `boardIdle`, `agentInactivity`.
- **Conditions**: `ticketInColumn`, `ticketCountInColumn`, `fieldLength`, `priority`, `labels`, `assignedTo`, `hasParent`, `allSubTicketsInStatus`, `ticketAge`.
- **Actions**: `runAgent`, `moveTicketStatus`, `setLabels`, `assignTicket`, `addComment`, `consolidateAgentMemory`, `commitAgentMemory`, `executePowerShell`, `createTicket`.
- `{assignee}` placeholder in `runAgent.agent` / `runAgent.concurrencyGroup` resolves from the firing ticket's `assignedTo`.
- `{ticketId}` placeholder in `concurrencyGroup` and `mutuallyExclusiveWith` resolves to the firing ticket's ID, enabling per-ticket serialization while preserving parallelism across distinct tickets.
- `commitAgentMemory` detects whether `.agents/` is a standalone git repo (`.agents/.git` present) and commits there; otherwise falls back to the main workspace repo. It commits both the per-topic memory layout (`.agents/{agent}/memory/`) and any legacy flat `.agents/{agent}/memory.md`.
- Canonical post-run chain: `runAgent` → `consolidateAgentMemory` → `commitAgentMemory`.
- **`statusChange` trigger re-fire on failure**: for `runAgent` actions backed by a `statusChange` (or `subTicketStatus`) trigger, `ActionExecutor` defers advancing the trigger snapshot (`commitAsync`) until after the run completes. If the run ends with `AgentRunStatus.Failed` or `Stopped`, the snapshot is left at the pre-transition value, so the next poll detects the transition again and re-dispatches the agent. On `Completed`, the snapshot advances normally and subsequent polls stay silent. This means a rate-limited or crashed agent is automatically retried on the next poll cycle (≤ `pollSeconds`) without any manual intervention.
- **Status snapshots are isolated per automation**: each `statusChange` rule owns its persisted acknowledgement state. A successful lightweight rule can no longer acknowledge the same transition on behalf of a different rule that was skipped by a concurrency or budget gate. Existing workspaces lazily seed each isolated snapshot from the legacy shared snapshot.
- **Bounded `ticketInColumn` retries**: `maxConsecutiveFirings` limits completed chains for an unchanged ticket, while `retryBackoffSeconds` persists a cooldown across restarts. Status, assignee, title, or description changes reset the series; comments and `UpdatedAt` churn do not. Optional `exhaustedStatus` and `exhaustedComment` escalate exactly once when the cap is reached. Set `maxConsecutiveFirings` to `0` only for an intentional unbounded workflow.
- **Completion recorded after the whole chain**: `ITrigger.CompleteFiringAsync` receives the final success/failure outcome after status restoration and all post-run actions. Automation-owned runs keep their registry status at `Running` until this bookkeeping is complete, so observers cannot see a terminal run while its ticket/trigger state is still unsettled.
- **Debounce stamped at chain completion**: successful legacy triggers still commit through `ITrigger.CommitFiringAsync`; `ActionExecutor` passes `DateTime.UtcNow` at the moment the entire action chain finishes. Interval/cron debounce timestamps therefore reflect chain completion time rather than emission time.
- **Atomic label actions**: `setLabels` creates missing label definitions and applies additions/removals through one ticket mutation. A label mutation failure fails the action chain instead of being logged as a false success.

## Entry points
- Hosted at app startup via DI in `GigaClaw.Web/Program.cs`.
- Per-project configuration loaded from `<workspace>/.agents/automations.json` (seeded by the [project template](./project-template.md)).
- Editable from the in-app **Automations** page.

## External dependencies
- [Agent dispatch](./agent-dispatch.md) — the `runAgent` action launches the `claude` CLI through it.
- [Storage](./storage.md) — reads ticket/column/comment state from per-project SQLite DBs.
- `git` on PATH — the `gitCommit` trigger polls the workspace's git log.

# Executable teams

## Purpose
Turns a team from a static member filter into a runnable object. A `TeamDefinition` describes roles,
entry conditions, a task-graph template, a join policy and a synthesizer role; a `TeamRun` is one
execution of that definition bound to a parent ticket; a `TeamTask` is a sub-ticket owned by a run.

A definition with an **empty task graph is valid** and means "pure member filter". All nine built-in
teams are exactly that, so team filtering behaves as it always has — see [Kanban UI](./kanban-ui.md).

## Key components
- `GigaClaw.Core/Models/TeamDefinition.cs` — `TeamDefinition`, `TeamRole`, `TeamTaskTemplate`,
  `TeamJoinPolicy` (`AllDone` / `Quorum` / `FirstFailure`) and `Validate()`, the I/O-free structural
  verdict (unknown roles, duplicate keys, dangling or cyclic dependencies, out-of-range quorum).
- `GigaClaw.Core/Models/TeamRun.cs` — `TeamRun`, `TeamTask` and their status enums.
- `GigaClaw.Core/Services/TeamStore.cs` — persistence for all three, plus the inline migration.
- `GigaClaw.Core/Services/AgentTeamService.cs` — the nine built-in definitions and the `AgentTeam`
  projection the team filter and the catalog consume.

## Storage
Three tables in the **per-project** database (see [Storage](./storage.md)): `TeamDefinitions`,
`TeamRuns`, `TeamTasks`. They live there because a run is bound to a parent ticket and its tasks are
sub-tickets, so the rows carry real foreign keys into `Tickets` and are cascaded away with them.
Created by the usual `CREATE TABLE IF NOT EXISTS` inline migration, which adds no column to any
existing table and therefore cannot touch a pre-existing row.

## Dependencies between tasks
There is no second dependency mechanism. A task's `DependsOn` (sibling template keys) is provenance;
the blocking truth is the ordinary `TicketDependencies` edge table that the board renders and the
`dependenciesResolved` condition evaluates — see [Automation engine](./automation-engine.md).
`TeamStore.AddTaskAsync` materializes the edges through `TicketService.AddTicketDependencyAsync`, so
cycle detection and validation stay in one place, and removing an edge on the board really does change
what a task waits on.

## Run lifecycle
`GigaClaw.Core/Services/TeamRunService.cs` owns fan-out, ordering, the join, the synthesizer and
cancellation. Everything it does, it does **on the board** — there is no in-memory graph, queue or
scheduler.

- **Starting a run** — the `startTeamRun` automation action (see
  [Automation engine](./automation-engine.md)) names a team; the firing ticket becomes the run's
  parent. A project-scoped definition wins over the built-in of the same slug. The action is
  **idempotent per (ticket, team)**: firing again while the run is open re-attaches instead of
  fanning out twice, so it is safe under a repeating `ticketInColumn` trigger. A filter-only team is
  refused, and so is a role whose agent is not a member of the project — checked before the run row
  exists, so a misconfigured team never leaves half a graph behind.
- **Fan-out** — one sub-ticket per task template, titled from the template, described by its
  `Prompt`, assigned to the role's agent, parented to the run's ticket. Templates are materialized in
  dependency order because an edge can only point at a sibling that already exists. A task with no
  blockers is born in **Todo**, the dispatch column the per-agent automations already watch; a task
  with blockers is born in **Blocked**, so the ordinary dispatch cannot start it early. Fan-out is
  re-entrant: a run interrupted mid-fan-out is completed by the next reconcile rather than left with
  a truncated graph.
- **Dispatch ordering** — `ReconcileRunAsync` releases a task (Blocked → Todo) exactly when
  `ConditionEvaluators.DependenciesResolved` — the evaluator behind the `dependenciesResolved`
  condition — says every live `blockedBy` edge of its ticket is resolved. That is the only readiness
  rule in the system, which is why removing an edge on the board really does unblock a task.
  Dispatch itself is still the ordinary per-agent automation; a team run only decides *when* a
  sub-ticket is allowed to be in the dispatch column.
- **Reporting a failure** — a lane that could not deliver is reported in through
  `FailTaskAsync(slug, ticketId, reason)`: the task row becomes `Failed`, its sub-ticket is parked
  out of the dispatch column (a failed sub-ticket left in **Todo** is simply re-dispatched next
  tick), and the join is evaluated in the same call. Failure is *reported*, not sniffed out of the
  agent-run registry, because that registry is in memory: a lane that died before a restart would
  otherwise look open forever. A row write is the only failure signal that survives.
- **Cancellation** — `CancelRunAsync` cancels every still-open task and moves its sub-ticket to
  **Backlog**, a column no dispatch automation watches: the board is what starts agents, so a
  cancelled task left in Todo would be picked up on the next tick regardless of its row. Tasks that
  already reached a terminal state are left exactly as they are, and a run that is already terminal
  is returned unchanged — a late cancellation cannot rewrite history. Closing the parent ticket
  (moving it to Done) or deleting it cancels the run the same way, on the next reconcile. A run
  cancelled while the synthesizer had the floor parks the synthesis sub-ticket too.

`TriggerHandler` reconciles every open run of a project at the start of each tick, before the
triggers are polled, so a task released this tick is already in the dispatch column when its agent's
trigger looks.

## Joining
`TeamJoinEvaluator.Evaluate(policy, tasks)` (`GigaClaw.Core/Models/TeamJoin.cs`) is a **pure**
function of the join policy and the task rows — I/O-free like `TeamDefinition.Validate()`, so the
reconcile pass, the tests and any future UI read the same three words the same way. It answers two
separate questions: *may the run stop waiting* (`Fires`) and *did it get what it asked for*
(`Success`). They are separate because a quorum join fires **and** succeeds while lanes are still
open, and an all-done join fires **without** succeeding when a lane failed.

| Mode | Fires when | Succeeds when |
|---|---|---|
| `AllDone` | every task is terminal | every task is `Done` |
| `Quorum(n)` | `n` tasks are `Done`, **or** so few can still report that `n` is out of reach | `n` tasks are `Done` |
| `FirstFailure` | a task reports `Failed`; otherwise like `AllDone` | every task is `Done` |

**Lanes still open when a join fires are cancelled**, through the same path as `CancelRunAsync`:
the task row becomes `Cancelled` with the join as its reason and the sub-ticket is parked in
**Backlog**. A join is a decision, and the board is what starts agents — a lane left in the dispatch
column would go on spending tokens on an answer nobody is going to read.

## Synthesizing
When the join fires and the definition names a `SynthesizerRole`, the run creates **one** sub-ticket
assigned to that role's agent, in the dispatch column, parented to the run's parent ticket, and moves
to `Joining` with `SynthesisTicketId` set. Its description is the synthesizer's brief:

- the join mode and the one-line reason it fired;
- **lanes that reported** — each one's newest handoff, rendered by `HandoffReader.Render`, the same
  rendering a serial hand-off injects (see [Handoff contract](./handoff-contract.md)). There is no
  second summary format. A lane that finished without posting a handoff says so and points at its
  ticket;
- **lanes missing** — every task that is not `Done`, named with its agent, its sub-ticket, whether it
  failed or was cancelled, and the recorded reason, followed by an instruction not to present their
  subject matter as covered.

Partial failure is an outcome, not an error: the synthesizer always runs, and it always learns which
lanes are absent and why. A synthesis that silently drops a failed lane is worse than no synthesis,
because it reads as complete.

`TeamTask.ResultHandoffRef` is written the moment a lane completes, as `ticket-<id>/run-<runId>` —
the marker identity of the handoff comment the contract calls authoritative, not a copy of it.

The run finishes when the synthesis sub-ticket resolves. `FinalizeAsync` recomputes success from the
terminal task rows (`TeamJoinEvaluator.Succeeded`) rather than from anything remembered at join time,
so a run that sat in `Joining` across a restart cannot drift from its own rows: `Completed` when the
policy got its lanes, `Failed` otherwise, with a `FailureReason` naming every lane that did not
report. Either way the parent ticket gets an activity receipt — including on a quorum run that
completed *with* gaps. A definition with no synthesizer goes straight from the join to its terminal
state.

**Terminal is terminal, and reconcile is idempotent.** `ReconcileRunAsync` returns a terminal run
untouched; a run already in `Joining` is only checked for whether its synthesis ticket resolved, so a
second reconcile in the same tick cannot join twice or dispatch a second synthesizer. A late
`FailTaskAsync` or `CancelRunAsync` against a closed run is ignored rather than rewriting history.

## Resumability
Every state change is a committed row write before any in-memory reaction, and each run stores a
**snapshot** of the definition it started from — an edited or deleted definition never rewrites a run
already in flight. A restarted engine rebuilds the world from `ListRunsAsync(openOnly: true)`, each
run's snapshot, `ListTasksAsync` and the live edges. Terminal runs and tasks are final, so a late
callback from a killed process cannot revive one.

Concretely, that is `TeamRunService.ReconcileProjectAsync`: it takes nothing but a project slug and
continues every open run from the tickets. Nothing is handed across a restart boundary, because
nothing about a run ever lived outside the project database.

## Entry points
- `TeamStore` and `TeamRunService`, DI-registered singletons in `GigaClaw.Web/Program.cs`.
- The `startTeamRun` automation action, for starting a run from the board.
- `TeamRunService.ReconcileProjectAsync(slug)` (engine tick / restart), `FailTaskAsync` and
  `CancelRunAsync` / `CancelRunsForParentAsync`.
- `TeamJoinEvaluator.Evaluate` / `Succeeded` for the join decision, with no I/O.
- `AgentTeamService.GetDefinitions()` / `GetDefinitionBySlug(slug)` for the built-ins.

## Not implemented yet
- **File-ownership leases** — two lanes writing the same file still race; `ownedFiles` from the
  handoff is the declared scope a lease will be taken on (lane CX-R's R4).
- **Team presets** — no built-in definition ships a task graph yet, so every executable team is one
  a project defines itself. The nine built-ins remain pure member filters.

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
`GigaClaw.Core/Services/TeamRunService.cs` owns fan-out, ordering and cancellation. Everything it
does, it does **on the board** — there is no in-memory graph, queue or scheduler.

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
- **Cancellation** — `CancelRunAsync` cancels every still-open task and moves its sub-ticket to
  **Backlog**, a column no dispatch automation watches: the board is what starts agents, so a
  cancelled task left in Todo would be picked up on the next tick regardless of its row. Tasks that
  already reached a terminal state are left exactly as they are, and a run that is already terminal
  is returned unchanged — a late cancellation cannot rewrite history. Closing the parent ticket
  (moving it to Done) or deleting it cancels the run the same way, on the next reconcile.

`TriggerHandler` reconciles every open run of a project at the start of each tick, before the
triggers are polled, so a task released this tick is already in the dispatch column when its agent's
trigger looks.

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
- `TeamRunService.ReconcileProjectAsync(slug)` (engine tick / restart) and
  `CancelRunAsync` / `CancelRunsForParentAsync`.
- `AgentTeamService.GetDefinitions()` / `GetDefinitionBySlug(slug)` for the built-ins.

## Not implemented yet
The **join/synthesizer** behavior is a separate slice: `JoinPolicy` is stored and snapshotted but
never evaluated, `SynthesizerRole` never dispatches, and `TeamTask.ResultHandoffRef` is never
written. A run whose tasks have all finished therefore stays `Running` — deliberately, so the join
has something left to close. Task results will travel as handoff artifacts
(see [Handoff contract](./handoff-contract.md)) via `TeamTask.ResultHandoffRef`.

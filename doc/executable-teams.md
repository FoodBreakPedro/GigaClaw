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

## Resumability
Every state change is a committed row write before any in-memory reaction, and each run stores a
**snapshot** of the definition it started from — an edited or deleted definition never rewrites a run
already in flight. A restarted engine rebuilds the world from `ListRunsAsync(openOnly: true)`, each
run's snapshot, `ListTasksAsync` and the live edges. Terminal runs and tasks are final, so a late
callback from a killed process cannot revive one.

## Entry points
- `TeamStore`, DI-registered singleton in `GigaClaw.Web/Program.cs`.
- `AgentTeamService.GetDefinitions()` / `GetDefinitionBySlug(slug)` for the built-ins.

## Not implemented yet
The run **lifecycle** (fan-out, dispatch, cancellation propagation) and the **join/synthesizer**
behavior are separate slices; task results travel as handoff artifacts
(see [Handoff contract](./handoff-contract.md)) via `TeamTask.ResultHandoffRef`.

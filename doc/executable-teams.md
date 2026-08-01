# Executable teams

## Purpose
Turns a team from a static member filter into a runnable object. A `TeamDefinition` describes roles,
entry conditions, a task-graph template, a join policy and a synthesizer role; a `TeamRun` is one
execution of that definition bound to a parent ticket; a `TeamTask` is a sub-ticket owned by a run.

A definition with an **empty task graph is valid** and means "pure member filter". Nine of the
eleven built-in teams are exactly that, so team filtering behaves as it always has — see
[Kanban UI](./kanban-ui.md). The other two, `parallel-review` and `hypothesis-debug` (C8), are real
task graphs — see [Team presets](#team-presets-c8) below.

## Key components
- `GigaClaw.Core/Models/TeamDefinition.cs` — `TeamDefinition`, `TeamRole`, `TeamTaskTemplate`,
  `TeamJoinPolicy` (`AllDone` / `Quorum` / `FirstFailure`) and `Validate()`, the I/O-free structural
  verdict (unknown roles, duplicate keys, dangling or cyclic dependencies, out-of-range quorum).
- `GigaClaw.Core/Models/TeamRun.cs` — `TeamRun`, `TeamTask` and their status enums.
- `GigaClaw.Core/Services/TeamStore.cs` — persistence for all three, the inline migration, and the
  seed pass that writes the roster into a project.
- `GigaClaw.Core/Models/TeamSeed.cs` — the `teams.json` document format, its parser and the
  composition rules.
- `GigaClaw.Core/Services/AgentTeamService.cs` — resolves the roster and produces the `AgentTeam`
  projection the team filter and the catalog consume.

## Where team definitions come from

Teams are **data, not code**. The eleven built-ins live in `ProjectTemplate/Agents/teams.json`,
embedded as `GigaClaw.Core.AgentsTemplate/teams.json` and written to `<workspace>/.agents/teams.json`
by Initialize like every other template asset — see [Project template](./project-template.md). That
is what makes a team addable by something other than a `GigaClaw.Core` rebuild.

```jsonc
{
  "schemaVersion": 1,
  "teams": [
    { "slug": "software-engineering", "name": "Software Engineering", "description": "…",
      "icon": "💻", "agentSlugs": ["programmer", "groomer", "…"] }
  ],
  "teamMembership": { "software-engineering": ["security-auditor"] }
}
```

`agentSlugs` is the shorthand for a pure member filter (one seat per agent, role id = agent slug); a
team that spells out `roles` / `taskGraph` / `joinPolicy` / `synthesizerRole` instead is an
executable team declared entirely in data. A bare JSON array of teams is also accepted, which is the
shape a single contributor's fragment takes. A `schemaVersion` above the one this build understands
is **refused**, not best-effort parsed — `System.Text.Json` drops unknown members silently, so a
newer roster would otherwise load as a quietly wrong one.

**Composition.** `TeamSeed.Compose` unions teams by slug and a duplicate slug is a hard error: team
slugs are one flat namespace, and letting the last contributor win would make the roster depend on
composition order. `teamMembership` is applied after every team exists — so a contributor may join a
team declared by another — and it only ever **adds** seats, never removes one.

**Resolution**, in order:

1. `<workspace>/.agents/teams.json`, when the workspace has a readable one. This is what a pack
   composes into, so a team or membership it contributes is live with no rebuild.
2. The embedded core roster — every workspace with no file of its own, which is every workspace
   initialized before the roster became data.
3. `AgentTeamService.CompiledFallbackDefinitions`, the pre-data C# list. It exists only so a broken
   or missing resource cannot empty the member filter; a test asserts it is seat-for-seat equal to
   the embedded roster, so the two cannot drift.

A malformed or structurally invalid roster degrades to the built-ins rather than being partially
applied — a bad file must not be able to hide every agent on the board.

`AgentTeamService.AllTeamsSlug` is unchanged: `all` is the no-filter sentinel, ships first in the
roster with no members, and is what `GetTeamBySlug` falls back to for an unknown slug.

### Seeding a project

`TeamStore.SeedDefinitionsAsync(projectSlug)` writes the workspace's roster into that project's
`TeamDefinitions`. It runs automatically, once per database file per process, on the first
definition call — not only at Initialize — so a project created before the roster became data
migrates on its own.

Nothing the owner authored is ever overwritten. `TeamDefinitionRow.SeedHash` is the SHA-256 of the
payload **as the seed wrote it**, and it is the ownership marker:

| Row state | Seed does |
|---|---|
| no row for the slug | insert it, with `SeedHash` |
| `SeedHash` still matches the stored payload | refresh it — a roster change, or a pack's new seat, reaches projects that already exist |
| `SeedHash` is null (owner-authored) or no longer matches (owner-edited) | leave it exactly as it is |

`SaveDefinitionAsync` clears `SeedHash`, so an owner write takes the row out of the seed's hands for
good. The column arrives through the usual inline `ALTER TABLE … ADD COLUMN SeedHash TEXT NULL`,
which rewrites no row: every definition that predates it reads back as owner-authored, which is what
it is.

Seeded rows are also what makes a data-added team *runnable* per project, because
`TeamRunService.ResolveDefinitionAsync` reads the project row first.

### What a pack supplies

To add a team: one entry under `teams` in the `teams.json` it composes into the workspace. To add a
member to a team it does not own: one entry under `teamMembership`. Neither requires recompiling
`GigaClaw.Core`. The manifest, the composer that merges several contributors' fragments into the one
workspace file, and the CI gate over them are [pack infrastructure](./pack-infrastructure.md) (T6);
this file describes the format and the runtime that consume their output.

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
- **Starting an ad-hoc run** — `StartRunAsync(slug, TeamDefinition, parentTicketId)` takes a
  definition directly instead of looking one up. This is what the `parallelRunAgents` action uses:
  its branches are translated into an inline definition (`ParallelRunPlan`) that is *never stored as
  a `TeamDefinition` row*. It resumes anyway, because the run's own snapshot — not a definition row —
  is what every later reconcile reads.
- **Fan-out** — one sub-ticket per task template, titled from the template, described by its
  `Prompt`, assigned to the role's agent, parented to the run's ticket. Templates are materialized in
  dependency order because an edge can only point at a sibling that already exists. A task with no
  blockers is born in **Todo**, the dispatch column the per-agent automations already watch; a task
  with blockers is born in **Blocked**, so the ordinary dispatch cannot start it early. Fan-out is
  re-entrant: a run interrupted mid-fan-out is completed by the next reconcile rather than left with
  a truncated graph.
- **Concurrency ceiling** — `TeamDefinition.MaxConcurrency` (0 = unlimited) caps how many of a run's
  tasks may sit in the dispatch column at once. Over the cap, an unblocked task waits in **Blocked**
  like a blocked one and is released when a lane reports. The ceiling is expressed as a column, not a
  flag, so it survives a restart with everything else. It is a *second* limit, not a replacement for
  the host-wide `RunConcurrencyGate` or the R4 file leases: a branch is still started by the ordinary
  per-agent automation and still queues behind both.
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

Partial failure is an outcome, not an error: by default the synthesizer always runs, and it always
learns which lanes are absent and why. A synthesis that silently drops a failed lane is worse than no
synthesis, because it reads as complete.

`TeamDefinition.PartialFailure` chooses between the two honest answers when the join fires without
every lane reporting:

| Mode | The run does |
|---|---|
| `Synthesize` (default) | dispatches the synthesizer with the results **and** the named gaps — synthesize-with-gaps |
| `FailFast` | skips the synthesizer and closes `Failed`, the gaps in its receipt |

Both leave a receipt on the parent ticket, and `FailFast` leaves a second one naming the synthesizer
it deliberately did not dispatch. The choice changes what happens next, never whether it is recorded.

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
- The `startTeamRun` automation action, for starting a run of a named team from the board, and
  `parallelRunAgents` for one whose branches are declared inline
  (`GigaClaw.Core/Automation/ParallelRunPlan.cs` translates the second into the first's shape).
- `TeamRunService.ReconcileProjectAsync(slug)` (engine tick / restart), `FailTaskAsync` and
  `CancelRunAsync` / `CancelRunsForParentAsync`.
- `TeamJoinEvaluator.Evaluate` / `Succeeded` for the join decision, with no I/O.
- `AgentTeamService.GetDefinitions()` / `GetDefinitionBySlug(slug)` for the built-ins, and their
  `(…, workspacePath)` overloads for a workspace's composed roster.
- `TeamStore.SeedDefinitionsAsync(slug)` to write the roster into a project explicitly.

## Team presets (C8)

Two built-ins ship with real task graphs, proving the C4/C5 machinery end to end with agents that
already exist in the core roster. Both wire `TeamDefinition` fields the presets above introduced:
`DedupeFindings` and `RequireEvidenceCitingArbitration`.

- **`parallel-review`** — an `accessibility-lane` (`ui-auditor`) and a `coverage-lane` (`qa-tester`)
  run in parallel, `AllDone` join, synthesized by `producer`. `security-reviewer`,
  `performance-reviewer` and `architecture-reviewer` are reserved role names: no core agent reviews
  those dimensions today, and the specialists ship with the Security and Architecture & Data packs
  ([packs-and-later.md](./roadmap/packs-and-later.md)) — add a role plus a task template once they
  land. `DedupeFindings: true` makes `TeamRunService.ComposeBrief` prepend a merged, per-lane
  attributed view of every reporting lane's `RunHandoff.OpenLoops`
  (`GigaClaw.Core/Automation/Handoffs/FindingDeduplicator.cs`, a pure function keyed on a normalized
  `location|category` string — no schema change, since open loops are the closest the frozen v1
  handoff contract has to "a lane's finding") and posts a host-authored `GIGACLAW-VERDICT` receipt
  (agent `team-synthesis`) on the parent ticket — SHIP with nothing blocking, FIX if a deduped
  finding is, BLOCK if the join did not get what it asked for. That receipt is what lets an ordinary
  `verdictIs` automation gate on the run without parsing the dispatched synthesizer's own prose.
- **`hypothesis-debug`** — two investigator lanes (`qa-tester`, standing in twice for the reserved
  `hypothesis-investigator` role under different `TeamRole`s) investigate independent hypotheses in
  parallel; `debug-lead` (`producer`, standing in for the reserved `debug-lead` role) arbitrates.
  `RequireEvidenceCitingArbitration: true` appends an instruction to the brief naming the
  `GIGACLAW-ARBITRATION v1 winner=<task-key>` / `reason: …` shape the lead must emit; once the
  synthesis ticket resolves, `TeamRunService.FinalizeAsync` reads that marker
  (`GigaClaw.Core/Automation/Handoffs/ArbitrationReader.cs`) and posts a closing comment naming the
  winner and reason on every other reported lane's own ticket. No marker is a no-op — the lead's
  prose only has to trigger the mechanism, never perform the closing itself.
- Both reserved-role agents are authored pending GM's G5 pass
  ([lane-gemini-templates.md](./roadmap/lane-gemini-templates.md)); the task-template prompts above
  are deliberately minimal placeholders, not the specialists' eventual prose.
- Started by two core automations (`parallel-review-on-labeled`, label-gated on `Review`;
  `hypothesis-debug-on-qa-block`, gated on a `qa-tester` `BLOCK` verdict via the existing `verdictIs`
  vocabulary — see [Automation engine](./automation-engine.md)) — no new trigger/condition/action
  vocabulary, only new `automations.json` entries.

## Not implemented yet
- **File-ownership leases** — two lanes writing the same file still race; `ownedFiles` from the
  handoff is the declared scope a lease will be taken on (lane CX-R's R4).
- **A pack team in the board's team picker** — the Blazor filter still calls the project-less
  `AgentTeamService.GetTeams()`, so a team contributed by a workspace roster resolves and runs but is
  not yet offered in the dropdown. Passing the project's workspace path to the `(…, workspacePath)`
  overloads is the one-line wiring that closes it, and it belongs with the composer in T6.

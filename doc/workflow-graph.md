# Workflow graph

## Purpose
A **typed, declarative graph over tickets**: named states, fan-out and join nodes, gates that route on
the existing automation conditions, terminal states, visited-role tracking and a cycle bound. It says
what shape a ticket's journey is allowed to have, in one document, instead of leaving that shape
implicit in a pile of automations that each know only their own hop.

Its job is refusal. Two mistakes are cheap to make and expensive to discover in production, so both
are rejected at config load:

- a state **nothing can reach** — usually a transition renamed on one side only, invisible until the
  ticket that needed it is the one that is stuck;
- a **cycle with no gate on it** — an unbounded loop with a ticket and a token budget inside.

The graph is the plan; [executable teams](./executable-teams.md) and the
[automation engine](./automation-engine.md) are what execute a plan's parallel and serial hops today.

## Key components
- `GigaClaw.Core/Automation/Workflow/WorkflowGraph.cs` — `WorkflowGraph`, `WorkflowState`,
  `WorkflowTransition`, `WorkflowStateKind`, and `Validate()`, the I/O-free structural verdict.
  I/O-free for the same reason `TeamDefinition.Validate()` is: the loader, the tests and any future
  editor share one verdict rather than three readings of the same document.
- `GigaClaw.Core/Automation/Workflow/WorkflowGraphFile.cs` — reads and validates
  `<workspace>/.agents/workflow.json`, and owns the JSON options (camelCase, string enums).
- `GigaClaw.Core/Automation/AutomationStore.cs` — the load point. `LoadAsync` reads the graph beside
  the automations and caches it; `GetCachedWorkflow(slug)` hands it back.
- `GigaClaw.Core/Automation/ProjectRuntime.cs` — `ProjectRuntime.Workflow`, the validated graph the
  engine sees for a project.

## Where the graph lives
`<workspace>/.agents/workflow.json`, beside `automations.json`, because it is the same kind of thing:
declarative workspace config the engine reads, the owner edits, and a pack may compose into. A
workspace **without** the file is the normal case and not an error — nothing today requires a graph.

```jsonc
{
  "schemaVersion": 1,
  "initial": "draft",
  "maxCycles": 3,
  "states": [
    { "name": "draft",  "kind": "task", "role": "blog-writer",
      "next": [{ "to": "review" }] },
    { "name": "review", "kind": "task", "role": "blog-reviewer",
      "next": [{ "to": "verdict" }] },
    { "name": "verdict", "kind": "gate",
      "gate": { "type": "verdictIs", "verdicts": ["SHIP"] },
      "next": [
        { "to": "publish",   "when": "SHIP" },
        { "to": "draft",     "when": "FIX"  },
        { "to": "escalated", "when": "BLOCK" }
      ] },
    { "name": "publish",   "kind": "terminal" },
    { "name": "escalated", "kind": "terminal" }
  ]
}
```

`schemaVersion` above the one this build understands is **refused**, not best-effort parsed —
`System.Text.Json` drops unknown members silently, so a newer graph would otherwise load as a quietly
wrong older one. This is the same rule `teams.json` follows.

## The model

| Kind | Means | Must have |
|---|---|---|
| `task` | one role works the ticket | a `role`, and somewhere to go |
| `fanOut` | the ticket splits into every branch in `next` at once | at least 2 `next` |
| `join` | closes the fan-out named by `joinOf` | a `joinOf` that names a `fanOut` state |
| `gate` | routes on a condition | a `gate` condition |
| `terminal` | the ticket stops here | no `next` |

**Gates reuse the automation condition vocabulary.** A gate's `gate` field is an ordinary
`ConditionSpec`, so `verdictIs` — the typed `SHIP`/`FIX`/`BLOCK` gate from the
[verdict contract](./verdict-contract.md) — is what a review gate is written with, evaluated by the
evaluators that already exist. A transition's `when` is the outcome label the gate routes on, drawn
from the same vocabulary (`SHIP`, `FIX`, `BLOCK`, and the routing outcomes `MISSING`, `INVALID`,
`STALE`). Inventing a second condition language was rejected for the reason
`TeamDefinition.EntryConditions` rejected it: one evaluator set, one editor, one meaning.

**Visited-role tracking.** Every `task` state must name its `role` — a task state with nobody to
dispatch to is not a state, it is a typo — and `trackVisitedRoles` (default on) is what records the
role of each state a ticket enters, so a gate can ask what has already been seen. `maxCycles`
(default 3) bounds how often one state may be re-entered before the run escalates instead of looping;
0 on a graph that *has* a cycle is refused, because that loop could never be taken.

## Validation
`WorkflowGraph.Validate()` returns every problem at once — a broken graph is fixed in one pass, not
one load at a time. It reports:

- duplicate state names, nameless states, transitions to a state that does not exist;
- **unreachable states**, by breadth-first search from the entry state (`initial`, or the first
  declared state);
- **gate-less cycles**, found by deleting every `gate` state and looking for a cycle in what is left.
  That is exact rather than approximate: a cycle survives the deletion precisely when it contained no
  gate, and if nothing survives then every cycle in the full graph passes through one. A gate is the
  only thing in this model that can decide to leave a loop. A *gated* cycle is explicitly legal — the
  repair loop is one, and a validator that refused every cycle would make it undeclarable;
- a terminal state with an exit, a non-terminal state with nowhere to go, and a graph with no
  terminal state at all;
- a `task` with no role, a `gate` with no condition, a condition on something that is not a gate, a
  one-armed fan-out, a join that closes nothing or closes something that is not a fan-out;
- an `initial` that names no state; a negative or unusable `maxCycles`.

## How errors are reported
Exactly like a malformed `automations.json`, because it is the same code path.
`WorkflowGraphFile.Read` throws `WorkflowGraphException` out of `AutomationStore.LoadAsync`;
`ProjectRuntimeManager.ReloadProjectAsync` already catches everything that method can throw and logs
`Failed to reload automations for {Slug}` with the exception attached. The consequences follow from
that and are deliberate:

- the reload fails **as a whole** — a config that cannot be trusted is never half-applied;
- the project keeps whatever runtime it already had, so a bad edit degrades to "the last good
  config is still running" rather than to "the automations are gone";
- `GetCachedWorkflow` is only ever updated by a load that succeeded, so no caller can be handed a
  graph that failed validation.

## Entry points
- `WorkflowGraph.Validate()` — the structural verdict, no I/O.
- `WorkflowGraphFile.Read(agentsDir)` — parse + validate; null when the workspace has no graph,
  `WorkflowGraphException` when it has a bad one.
- `AutomationStore.LoadAsync(slug)` — the load point, which validates the graph beside the
  automations; `AutomationStore.GetCachedWorkflow(slug)` for the result.
- `ProjectRuntime.Workflow` — the validated graph as the engine sees it.

## Not implemented yet
The graph is **declared and validated, not yet executed**. Nothing moves a ticket along it today:
parallel hops are run by the `parallelRunAgents` action and team runs
([executable teams](./executable-teams.md)), and serial hops by the ordinary column automations. The
runtime that walks a ticket through named states — entering, gating, fanning out, joining, counting
cycles against `maxCycles` and recording visited roles — is future work; validating the document
first is what makes that runtime safe to write.

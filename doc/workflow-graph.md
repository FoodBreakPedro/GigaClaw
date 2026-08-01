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

The graph is the plan; the walk described below is what executes it, over the
[executable teams](./executable-teams.md) and [automation engine](./automation-engine.md) machinery
rather than beside it.

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

## The walk — runtime semantics

A ticket **walks** the graph. The runtime is `GigaClaw.Core/Automation/Workflow/WorkflowWalker.cs`,
and its durable form is `WorkflowWalk.cs`.

### How a ticket opts in
The `startWorkflow` action, alongside `startTeamRun` and `parallelRunAgents` in the
[automation vocabulary](./automation-engine.md). Any trigger and any conditions decide *when*; the
action decides *what*. A label or a column would have needed a new trigger to watch it and would have
hard-coded the board's vocabulary into the graph; an action composes with the whole existing one.

```jsonc
{ "type": "startWorkflow" }              // begin at the graph's entry state
{ "type": "startWorkflow", "at": "review" }   // resume partway
```

The action **only records intent**, exactly as `enqueueMerge` does: it writes the walk's opening
receipt and returns. Everything after that is the walker's poll — because what unblocks a walk (a
sub-ticket reporting, a verdict arriving an hour later, a fan-out closing) is never this automation
firing again. It is idempotent per ticket: firing while a walk is running re-attaches instead of
restarting, so it is safe under a repeating `ticketInColumn` trigger. A ticket whose walk parked or
finished may be walked again — that is how an owner re-runs a workflow after fixing what stopped it.
A workspace with no `workflow.json`, or an `at` that names no state, leaves an activity note and
changes nothing.

### Where the walk state lives
**On the ticket, as receipt comments.** There is no walker table and no walker memory. Every
traversal writes one `GIGACLAW-WALK v1 ticket-<id> step-<n> <event>` comment carrying a `json`
payload, and every pass replays those comments before it does anything.

This is the C3 [repair loop](./verdict-contract.md)'s pattern one level up, chosen for the same three
reasons: the walk is **auditable** (an owner rereads the ticket and gets the walker's own answer),
**restart-proof by construction** rather than by a resume routine, and **immune to a resumed run**
restarting it — a replayed pass either writes another receipt, which counts, or writes nothing. The
shape is the verdict contract's: a marker line that makes the receipt greppable, plus a `json` block,
so a state name or a role may be any string the graph declares. A receipt whose marker contradicts
its payload is skipped, never half-believed.

The receipts are also the **index**: the walker finds walking tickets by searching for the marker, so
a restart rebuilds nothing and a project with no walks costs one query.

| Event | Means |
|---|---|
| `started` | a walk was opened. Resets the walk — the only thing that opens one |
| `entered` | the walk entered a state; carries its kind, its role, and its subject ticket |
| `left` | the walk left, naming the outcome it routed on and the target |
| `parked` | undecidable. Terminal, always with a reason, and carries the whole walk history |
| `finished` | a terminal state was reached |

### What each kind does
- **`task`** — materializes a sub-ticket of the walking ticket, titled `[wf:<step>:<state>]`, assigned
  to the state's role, in the dispatch column (`Todo`). The walker **starts no agent**: the ordinary
  per-agent `ticketInColumn` automation does, which is what makes a walk's work queue behind
  `RunConcurrencyGate`, take its file leases and honour its contract's worktree isolation for free. A
  second execution engine here would have bypassed all three. The state completes when its sub-ticket
  reaches `Done`. The sub-ticket is found back by its deterministic title, so a crash between
  creating it and writing the receipt adopts it rather than dispatching a second one.
- **`gate`** — evaluated through the ordinary `ConditionSpec` path, and routed on the **label** rather
  than a bool. For `verdictIs` the label is the resolved verdict outcome — `SHIP`, `FIX`, `BLOCK`,
  `MISSING`, `INVALID`, `STALE` — which is what makes `verdictIs` the gate *language* and not merely
  one gate: a graph writes `when: "FIX"` and gets the repair arm. Every other condition routes on
  `PASS`/`FAIL`. The gate's **subject** is the ticket the previous step produced (the last task
  state's sub-ticket), falling back to the walking ticket — so the reviewer's verdict, posted where it
  worked, is what the gate right after it reads.
- **`fanOut`** — its `next` branches (which must be `task` states) become an ad-hoc `TeamDefinition`
  handed to `TeamRunService`, the same translation `parallelRunAgents` performs. C4/C5's fan-out to
  sub-tickets, dependency edges, join policy, cancellation propagation and restart reconcile are
  therefore reused unchanged. The run's slug carries the step number, so a repeated pass re-attaches
  while a later cycle starts a fresh run.
- **`join`** — entered once the fan-out it names in `joinOf` reaches `Completed`, then a pass-through
  along its unconditional exit.
- **`terminal`** — writes `finished`. The walker changes no column: a terminal state is a fact about
  the walk, not a board policy, and an automation that wants one can watch the receipt with
  `ticketCommentAdded`.

### Visited-role tracking
Every `entered` receipt records the role that handled the traversal, in order and with repeats — a
role that worked the same state twice really did work it twice. `trackVisitedRoles: false` stops the
recording, not the dispatch. Gates, joins and terminals name no role and record none, because
claiming one would be a lie about who worked it.

### Fail closed, with receipts
An undecidable transition **parks** the ticket: a `parked` receipt carrying the whole walk history,
then a move to `Blocked`. The receipt is written before the move — a ticket that reaches the owner's
column with no explanation on it is worse than one still where it was. Parking is what happens when:

- a gate resolves to an outcome none of its arms declare (a reviewer who answered with prose resolves
  to `MISSING`, and a graph that declares no `MISSING` arm stops rather than guessing);
- the gate condition throws;
- a `task` state's role is nobody in the project — checked *before* the sub-ticket exists;
- a `task` or `join` state has no unconditional exit to take;
- a fan-out ends other than `Completed`, or no `join` closes it;
- a state's sub-ticket or a fan-out's run has disappeared;
- **`maxCycles` is exhausted** — checked before the state is re-entered, so a spent budget never
  dispatches one more round first. This escalates the way C3's repair-budget exhaustion does: the
  receipt carries every step's reasons, so an owner never has to open a run log to see why it stopped.

A state may be entered `maxCycles + 1` times: the first visit plus that many re-entries.

### When the walk advances
`WorkflowWalker.ReconcileProjectAsync` runs each engine tick from `TriggerHandler.ProcessTickAsync`,
immediately after `TeamRunService.ReconcileProjectAsync` and before the triggers are polled — so a
state entered this tick already has its sub-ticket in the dispatch column when the agent's trigger
looks. Polling rather than event-driven, for the same reason team runs are: the walk also has to react
to things that are not ticket transitions. One pass crosses as many states as it can without waiting
(gates and joins resolve immediately) and stops at the first state with outstanding work. "Still
working" writes no receipt, because it is not an event.

### Entry points
- `WorkflowWalker.AdvanceAsync(runtime, ticketId)` — advance one ticket; the unit of the whole
  runtime, and idempotent.
- `WorkflowWalker.Replay(ticket)` — the walk as the ticket records it. The only place walk state
  comes from.
- `WorkflowWalk.Render` / `TryRead` / `Replay` — the receipt format, I/O-free.
- `ActionExecutor.EvaluateWorkflowGateAsync` — a gate's condition as a routing label.

Proven end-to-end in `GigaClaw.Core.Tests/Automation/WorkflowWalkerTests.cs`: a ticket walking
entry → task → task → verdict gate → fan-out → join → terminal hermetically, the gate blocking without
a valid verdict, a `FIX` cycling back within `maxCycles` and exhausting it into an escalation, and a
restart mid-walk resuming without repeating a state or dispatching twice.

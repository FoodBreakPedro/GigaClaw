# Lane CL — Claude Orchestration (collaborative, feedback-dependent)

**Tool:** Claude Code + subagents. **Branch:** `lane/claude-orch` (own worktree).
**File scope (single writer):** `ProjectTemplate/Agents/automations.json`, `contracts.json`, `GigaClaw.Core/Automation/` trigger/condition/action **vocabulary** (`AutomationConfig.cs`, `ActionExecutor.cs`, `ConditionEvaluators.cs`, `Triggers/`), `AgentTeamService.cs` and new team runtime, verdict schemas, GitHub integration.
**Standing duty:** merge-queue owner — five-axis review of every lane branch before main; runs the sync-point checkpoints with the owner.
**Out of scope:** runner internals (CX-R), catalog/eval projects (CX-T), `ProjectTemplate/**` SKILL.md prose (GM — CL provides schemas/specs, GM writes the volume).

## Progress log

| Task | State | Evidence |
|---|---|---|
| C1 | **Landed** — branch `claude-orch/c1-verdict-schema` | `verdict.schema.json`, `verdict_contract.py`, worked example per gating reviewer + rejection corpus, `TemplateVerdictContractTests`, [`doc/verdict-contract.md`](../verdict-contract.md). Schema v1 is **frozen**: additive only until SP-2 |
| C2 | **Vocabulary landed** — branch `claude-orch/c2-verdict-gate`; per-reviewer wiring waits on GM G2 | `verdictIs` condition + host-side reader/freshness (`GigaClaw.Core/Automation/Verdicts/`), automation editor + palette + API docs, `VerdictGateTests`. Wiring the five reviewers into `automations.json` today would Block every ticket, since no reviewer emits verdicts yet — that edit lands per agent as G2 rewrites arrive |
| P4 (CL half) | **Landed** — branch `claude-orch/p4-dependencies-resolved`, based on `lane/cx-tooling` | `dependenciesResolved` condition over CX-T's edges: holds a ticket until every blocker reaches a resolved status; no edges = not blocked; ticketless firing fails closed. Integration test blocks then unblocks a real ticket. 645 tests green |
| C3 | **Landed** — branch `claude-orch/c3-repair-loop`; per-reviewer wiring waits on GM G2 with C2's | `repairBudget` condition (`withinCap`/`exhausted`) + `RepairLoop` (`GigaClaw.Core/Automation/Verdicts/`), FIX findings injected at dispatch beside the handoff, `{verdictHistory}` placeholder for the escalation comment, editor/palette/API docs, `RepairLoopTests`. The round count is **re-derived from the ticket's comments** on every evaluation — one per FIX verdict since the last SHIP/BLOCK/escalation receipt — so it is auditable, restart-proof, and a resumed run cannot restart it. Deduplicating identical verdicts was rejected: a reviewer re-reviewing unchanged bytes is the runaway case the cap exists for. 639 tests green |
| C4 (part 1) | **Models + store landed** — branch `claude-orch/c4-executable-teams` | `TeamDefinition`/`TeamRun`/`TeamTask` + `TeamStore` over three per-project SQLite tables (inline migration, no data loss), task dependencies materialized as ordinary `TicketDependencies` edges, run carries a definition snapshot for restart-safe resume, nine built-in teams migrated to empty-task-graph definitions (filtering byte-identical, catalog unchanged), [`doc/executable-teams.md`](../executable-teams.md). Run lifecycle (part 2) and join/synthesizer (part 3) still open. 685 tests green |
| C4 (part 2) | **Run lifecycle landed** — branch `claude-orch/c4-executable-teams` | `TeamRunService` + the `startTeamRun` action (editor, palette, en/fr, generated API action table, `ActionEditorCoverageTests`). Fan-out materializes one sub-ticket per task template, assigned to the role's agent, in dependency order; readiness is `ConditionEvaluators.DependenciesResolved` over the ticket's live `blockedBy` edges — no second rule. Blocked tasks are born in Blocked and released to Todo; cancelling or closing the parent cancels every open task and parks its sub-ticket out of the dispatch column, terminal states staying terminal. Resumability proven by rebuilding every service against the same project and continuing. Join policy and synthesizer (part 3) still open. 703 tests green |
| C4 (part 3) | **Join + synthesizer landed — C4 complete** — branch `claude-orch/c4-executable-teams` | `TeamJoinEvaluator`, a pure function of policy and task rows, decides `Fires` (stop waiting) separately from `Success` (got what it asked for): all-done waits for every terminal task, quorum fires on the *n*-th success or when *n* becomes unreachable, first-failure fires on the first `Failed`. Lanes still open when a join fires are cancelled and parked, so a decided run stops spending tokens. `FailTaskAsync` is the board-native failure report (the agent-run registry is in-memory and would not survive a restart). The synthesizer gets one sub-ticket whose brief renders each reporting lane's handoff through `HandoffReader.Render` **and** names every missing lane with its reason — synthesize-with-gaps, never silent-drop. `TeamTask.ResultHandoffRef` written at lane completion as `ticket-<id>/run-<runId>`. Outcome recomputed from the rows at finalize, so `Joining` survives a restart; terminal runs are untouchable and a double reconcile cannot dispatch a second synthesizer. 713 tests green |
| C5 (part 1) | **`parallelRunAgents` landed** — branch `worktree-c5-c7-c8` | Declarative parallel branches as **vocabulary, not a second engine**: `ParallelRunPlan` translates the action's inline branches into an ad-hoc `TeamDefinition`, and `TeamRunService.StartRunAsync(slug, definition, ticket)` drives it through the C4 machinery. That works because a `TeamRun` already stores its own definition snapshot — an ad-hoc run needs no `TeamDefinition` row and resumes on identical terms (proved by rebuilding every service over the same data dir). Fan-out, join policy, cancellation propagation and synthesize-with-gaps are inherited, not re-implemented. New: `TeamDefinition.MaxConcurrency` (a *second* ceiling expressed as a column, so it survives a restart) and `TeamDefinition.PartialFailure` (`Synthesize` \| `FailFast`, receipts either way). The action dispatches **nothing** itself — branches are sub-tickets the ordinary per-agent automation starts, which is what puts them behind `RunConcurrencyGate` and the R4 leases; the regression test holds the gate's only slot and shows the branch cannot start until it is handed back. Mirrored on every `startTeamRun` surface (editor, palette, en/fr, generated API table, `ActionEditorCoverageTests`), which also exposed that `Automations.razor`'s `DescribeAction` had no arm for `startTeamRun` or `enqueueMerge` — an unopenable page, now a test. 1151 tests green (1131 before). Part 2 is the typed workflow graph |
| C5 (part 2) | **Typed workflow graph landed — C5 complete** — branch `worktree-c5-c7-c8` | `WorkflowGraph` in `GigaClaw.Core/Automation/Workflow/`: named states with kinds (`task`/`fanOut`/`join`/`gate`/`terminal`), gates that carry an ordinary `ConditionSpec` so `verdictIs` *is* the review gate rather than a second condition language, visited-role tracking (every task state must name its role) and `maxCycles`. Lives at `.agents/workflow.json` beside `automations.json` and is validated inside `AutomationStore.LoadAsync` — the same load point — so an invalid graph throws out of it and `ProjectRuntimeManager`'s existing catch logs the same `Failed to reload automations` warning a malformed `automations.json` produces; the reload fails whole and the project keeps its last good runtime. **Unreachable states** are found by BFS from the entry state. **Gate-less cycles** are found by deleting every gate state and looking for a cycle in what is left — exact, not approximate: a cycle survives that deletion precisely when it had no gate, so a *gated* repair loop stays declarable (a validator that refused every cycle would have banned C3's own shape). [`doc/workflow-graph.md`](../workflow-graph.md). The graph is declared and validated, not yet executed — the walker is future work, and validating the document first is what makes it safe to write. 1174 tests green (1151 after part 1) |
| C8 | **Landed** — branch `worktree-c5-c7-c8` | Two built-in `TeamDefinition`s with real task graphs, the first since C4's nine were migrated to empty ones: `parallel-review` (`accessibility-lane`→`ui-auditor`, `coverage-lane`→`qa-tester`, `AllDone` join, synthesized by `producer`) and `hypothesis-debug` (two `investigator-*-lane`s→`qa-tester`, `AllDone` join, arbitrated by `debug-lead`→`producer`). The roadmap also names `security`/`performance`/`architecture` lanes and dedicated `hypothesis-investigator`/`debug-lead` agents; none of those exist in the 33-agent core template (`security-reviewer`, `performance-reviewer`, `architecture-reviewer` ship with the Security and Architecture & Data packs per [`packs-and-later.md`](../packs-and-later.md), same for the Incident & Debug pack's investigator/lead) — every role above binds to a *real, already-dispatchable* core agent instead, with the reserved role name kept as the `TeamRole.RoleId` so swapping in the specialist later is a one-line `teams.json` edit. `TeamDefinition.DedupeFindings` (parallel-review) makes `TeamRunService.ComposeBrief` prepend a deduplicated, per-lane-attributed view of every reporting lane's `RunHandoff.OpenLoops` — `FindingDeduplicator` (`GigaClaw.Core/Automation/Handoffs/`), a pure function keyed on a normalized `location\|category` string, no handoff-schema change — and posts a host-authored `GIGACLAW-VERDICT` receipt (agent `team-synthesis`) on the parent ticket so an ordinary `verdictIs` automation can gate on the run without parsing anyone's prose. `TeamDefinition.RequireEvidenceCitingArbitration` (hypothesis-debug) appends the brief with the `GIGACLAW-ARBITRATION v1 winner=<key>` / `reason: …` shape the lead must emit; once its synthesis ticket resolves, `FinalizeAsync` reads that marker (`ArbitrationReader`) and posts the closing comment on every losing lane's own ticket itself — mechanically enforced, not a prose instruction the lead might skip. Both started by new core automations (`parallel-review-on-labeled`, label-gated; `hypothesis-debug-on-qa-block`, gated on a `qa-tester` `BLOCK` verdict via the existing `verdictIs`+`repairBudget` vocabulary) using the existing `startTeamRun` action — no new trigger/condition/action types. `TeamStoreTests`/`AgentTeamService` built-in-count guards extended (9 filter-only + 2 executable = 11); catalog counts (agents unchanged, +2 teams, +2 automations) updated. Task-template prompts are deliberately minimal placeholders marked "pending GM G5" — [`lane-gemini-templates.md`](../lane-gemini-templates.md)'s G5 owns the specialists' real prose. [`doc/executable-teams.md`](../executable-teams.md#team-presets-c8). 1246 tests green (1230 before) |
| C6 | **Landed** — branch `claude-orch/c6-handoff-artifacts` | Handoff schema v1 + shared `schema_check.py` engine, host-side reader, dispatch injection via `ActionExecutor` (no `ClaudeRunner` edit needed), fixtures both ways, [`doc/handoff-contract.md`](../handoff-contract.md). `ownedFiles` is the interface CX-R's R4 leases consume — path discipline enforced on both sides. 621 tests green |
| C3 | **Landed** — branch `claude-orch/c3-repair-loop` | Bounded repair loop: one round per FIX since the episode opened (a SHIP, BLOCK or escalation receipt opens a new one), budget from `maxReviewCycles`, unparseable manifest resolves to *exhausted* rather than to a default. Escalation quotes every round's reasons and carries no verdict marker, so it cannot be misread as a new judgement. 642 tests |
| C9 | **Drafted — awaiting owner approval** — branch `claude-orch/c9-pack-spec` | [`doc/pack-infrastructure.md`](../pack-infrastructure.md): manifest schema, composition order and merge table, the staged install + `packs.lock.json` with per-file hashes (uninstall never deletes owner-edited files), integer runtime compatibility with quarantine rather than auto-upgrade, the core-pack extraction invariant, the five-binding CI gate with the six changes it needs, and a full Security Assurance manifest. Four questions escalated to the owner (§10). Docs-only; 624 tests green |
| C4 | **Parts 1–2 landed**, part 3 in flight — branch `claude-orch/c4-executable-teams` | Models + store (entry conditions reuse `ConditionSpec`, task edges reuse the ticket dependency API), then `startTeamRun`, fan-out to sub-tickets, readiness from `dependenciesResolved`, cancellation, and restart resumability via the reconcile pass. 703 tests. Join policy and synthesizer are part 3 |

---

## Task C1: Verdict schema v1 (A11/P8, part 1) — freeze early

**Description:** Design the versioned verdict schema all reviewers, gates, and the eval judge share: `{schemaVersion, agent, ticketId, verdict: SHIP|FIX|BLOCK, categories: [{name, score, max, notes}], vetoItems: [], evidence: [{path|hash|link}], reviewedAtUtc, inputDigest}`. Publish as JSON Schema in `ProjectTemplate/Agents/scripts/verdict.schema.json` and extend `content_contract.py` (already the enforcement point) to validate it. Freeze v1 at Phase 0 end; additive-only until SP-2 — GM's rewrites and CX-T's judge depend on it.

**Acceptance criteria:**
- [ ] Schema validates worked examples for all five reviewers (blog-reviewer, ui-auditor, qa-tester, local-media-reviewer, evaluator) — examples committed as fixtures
- [ ] `inputDigest` binds the verdict to what was reviewed (stale-artifact rejection is possible)
- [ ] Validator callable standalone and from automations; malformed verdict = BLOCK, fail closed

**Dependencies:** none. **Size:** S–M.

## Task C2: Verdict enforcement at the automation boundary (A11, part 2)

**Description:** New condition `verdictIs` (SHIP/FIX/BLOCK, with schema validation as a precondition): Review-column automations gate ticket exit on a valid verdict instead of prose. Wire for the five reviewers as GM's rewrites land; invalid or stale (`inputDigest` mismatch) verdicts route to Blocked with a receipt.

**Acceptance criteria:**
- [ ] Debug-instance scenario: reviewer emits SHIP → ticket advances; FIX → returns per repair loop; BLOCK/invalid/stale → Blocked + owner nudge
- [ ] Prose-only reviewer output (pre-rewrite agents) fails the gate loudly, not silently
- [ ] `TemplateAutomationContractTests` extended to cover verdict-gated exits

**Dependencies:** C1; GM tasks G2 rolling. **Size:** M.

## Task C3: Bounded repair loop (U10)

**Description:** On FIX verdict: re-dispatch the producing agent with the verdict's categories/veto items injected, up to `maxReviewCycles` (already in contracts.json defaults); on exhaustion escalate to owner (Blocked + comment). Loop counter persists on the ticket; returning to Todo resets per existing approval-invalidation semantics.

**Acceptance criteria:**
- [ ] Integration test: FIX→fix→SHIP within cap advances; FIX×(cap) escalates with the full verdict history in the escalation comment
- [ ] No infinite loops possible: counter survives engine restart, resume attempts respect the cap
- [ ] Cost of the loop visible in ticket cost badge (existing CostTracker, regression only)

**Dependencies:** C2. **Size:** S.

**Checkpoint SP-2 (owner):** demo verdict-gated blog + dev pipelines; review O6 baselines from CX-T.

---

## Task C4: Executable teams — TeamDefinition/TeamRun/TeamTask (T2)

**Description:** The most consequential adjudicated gap. Turn teams from static member filters into runnable objects: `TeamDefinition` (roles, entry conditions, task graph template, join policy, synthesizer role), `TeamRun` (instance bound to a parent ticket), `TeamTask` (sub-tickets with P4 dependency edges and, later, T11 leases). Implement through tickets/subtickets — the board stays the system of record. Migrate the nine current teams to definitions with empty task graphs (pure filters keep working) plus one real preset to prove the machinery.

**Acceptance criteria:**
- [ ] A TeamRun fans out sub-tickets per its graph, respects `blockedBy` edges, joins per policy (all-done / quorum / first-failure), and hands the join result to a synthesizer agent
- [ ] Cancellation propagates: cancelling the parent cancels/leases-releases all open TeamTasks
- [ ] Existing team filtering UI unchanged for definition-only teams; content-writer regression guarded by GM's membership test

**Dependencies:** P4 (CX-T T3) merged; P9 schema (C6) for handoffs. **Size:** L → split: models+store, run lifecycle, join+synthesizer.

## Task C5: Declarative parallel branches + typed workflow graph (U7/P10)

**Description:** Automation vocabulary additions: `parallelRunAgents` action (max concurrency, per-branch agent+prompt, join policy, partial-failure behavior, synthesizer) and a typed workflow-graph layer over tickets (fan-out, joins, gates referencing `verdictIs`, terminal states, visited-role tracking, max cycles). This is the engine-level generalization of C4's team runs.

**Acceptance criteria:**
- [ ] Graph validation rejects unreachable states and gate-less cycles at config load, reported like existing automation config errors
- [ ] Partial-failure test: one branch fails → join policy decides (fail-fast vs synthesize-with-gaps), receipts either way
- [ ] Concurrency respects RunConcurrencyGate and (once landed) T11 leases

**Dependencies:** C4; T11 (CX-R R4) for safe file-level parallelism. **Size:** L → split action vs graph.

## Task C6: Structured handoff artifacts (P9)

**Description:** Per-run handoff artifact schema: inputs, outputs, evidence links/hashes, assumptions, owned files (feeds T11 leases), open loops, acceptance criteria, next role. Stored as ticket comment attachment + `runs/<id>/handoff.json`. CL owns schema + engine plumbing; GM writes the per-agent template guidance.

**Acceptance criteria:**
- [ ] Dispatch prompt injects the previous run's handoff; chain of ≥2 agents on the debug instance shows information surviving the hop
- [ ] `ownedFiles` consumed by R4 leases (interface agreed with CX-R in writing before either implements)
- [ ] Schema versioned alongside verdict schema; validator shared

**Dependencies:** C1 pattern; coordinate with CX-R R4. **Size:** M.

**Checkpoint SP-3 (owner):** competing demo — one ticket through the old serial path vs a TeamRun with parallel branches; go/no-go on enabling U6 by default.

---

## Task C7: GitHub surface (U5)

**Description:** Optional GitHub integration: issues → tickets (labeled import), PR review comments → owner-feedback trigger (re-dispatch assignee), CI status → gitCommit-family trigger. Local-first stays default; this is additive for repos with remotes. Evaluate claude-code-action patterns; implement natively against the GitHub API (PAT config per project).

**Acceptance criteria:**
- [ ] Issue import round-trip: close ticket → optionally comment/close issue; idempotent sync, no dupes on restart
- [ ] PR comment from owner re-dispatches the assignee with the comment as steering input
- [ ] All tokens stored in settings, never in ticket content; network calls pass the P3 policy layer

**Dependencies:** P3 block mode (receipts for outbound), U6 (branches/PRs exist to comment on). **Size:** L → split import, PR-feedback, CI-status.

## Task C8: Team presets — parallel review + hypothesis debug (T5/T6)

**Description:** Two TeamDefinitions using C4/C5 machinery: `parallel-review` (security / performance / architecture / accessibility / coverage lanes with finding dedup by the synthesizer) and `hypothesis-debug` (N investigators, one hypothesis each, evidence-based arbitration by a lead). Agent prose authored by GM (G5); CL wires definitions, automations, and dedup logic.

**Acceptance criteria:**
- [ ] `parallel-review` on a seeded defective fixture produces deduped findings attributed per lane, verdict-gated
- [ ] `hypothesis-debug` on a seeded bug: ≥2 competing hypotheses recorded, lead's arbitration cites evidence, losing hypotheses closed with reasons
- [ ] Both run within cost caps and appear correctly in team UI

**Dependencies:** C4, C5, GM G5; security lane roles arrive with the Security pack (packs-and-later.md). **Size:** M.

## Task C9: O7 pack-infrastructure design spec (with owner)

**Description:** Design doc for packs (manifest schema, composition rules, permission/dependency declarations, versioning, install/uninstall semantics, core-pack extraction) reviewed with the owner, then handed to CX-T T6 for implementation. Includes the binding rule: a pack agent ships with contract + model + team + automation + eval fixture or CI rejects it.

**Acceptance criteria:**
- [ ] Owner-approved written spec in `doc/roadmap/` (or promoted to `doc/`) with worked manifest example for the Security pack
- [ ] Composition edge cases decided: slug collisions, cross-pack automation references, partial installs
- [ ] P20 catalog and O6 eval integration points specified

**Dependencies:** SP-2 done (verdicts + eval exist to bind against). **Size:** S (design only).

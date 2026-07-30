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
| C6 | **Landed** — branch `claude-orch/c6-handoff-artifacts` | Handoff schema v1 + shared `schema_check.py` engine, host-side reader, dispatch injection via `ActionExecutor` (no `ClaudeRunner` edit needed), fixtures both ways, [`doc/handoff-contract.md`](../handoff-contract.md). `ownedFiles` is the interface CX-R's R4 leases consume — path discipline enforced on both sides. 621 tests green |
| C3 | **Landed** — branch `claude-orch/c3-repair-loop` | Bounded repair loop: one round per FIX since the episode opened (a SHIP, BLOCK or escalation receipt opens a new one), budget from `maxReviewCycles`, unparseable manifest resolves to *exhausted* rather than to a default. Escalation quotes every round's reasons and carries no verdict marker, so it cannot be misread as a new judgement. 642 tests |
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

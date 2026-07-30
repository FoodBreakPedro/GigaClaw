# Lane CX-R — Codex Runtime (surgical C# changes)

**Tool:** Codex CLI (+ Codex subagents for test authoring). **Branch:** `lane/cx-runtime` (own worktree).
**File scope (single writer):** `GigaClaw.Core/Automation/ClaudeRunner.cs`, new `GigaClaw.Core/Automation/Policy/`, new `GigaClaw.Core/Automation/Runners/`, `ProcessLifecycleManager.cs`, adjacent tests in `GigaClaw.Core.Tests/Automation/`.
**Out of scope:** `automations.json`, `contracts.json` semantics (lane CL), `ProjectTemplate/**` markdown (lane GM).

Verification bar for every task: `dotnet test GigaClaw.Core.Tests -c Release` green; no `error CS####` in build; behavior proven by a failing-then-passing test (Prove-It). New helper projects must be built and tested explicitly.

## Validated implementation boundaries

- `contracts.json` currently has no network or enforcement fields and uses more risk classes than the six coarse capabilities. R1 types the existing versioned schema and maps every current class; schema changes stay with CL. Unknown classes and malformed entries evaluate to Block.
- Claude hooks cover Claude tool calls only. The host-side `httpRequest` action runs in `ActionExecutor` and requires a separate CL/shared preflight. It is not considered protected merely because runner hooks are installed.
- R2 begins with a hook-transport spike. Generated settings must be validated, passed via `--settings`, and proven loaded. The selected bridge must reuse `ContractPolicy` rather than duplicate matching logic. The mock CLI must emulate the relevant hook exchange, or the hook bridge receives a separate integration harness.
- R3 file/git enforcement can be implemented after R2. Outbound host actions stay dry-run-only until a trusted approval primitive and the shared `ActionExecutor` preflight land; an ordinary agent-mutable label is not a security boundary.
- R4 creates a durable SQLite lease store; it does not “reuse” an existing durable lock store because current run locks are in memory. Worktree separation does not waive logical ownership overlap.
- R5/R6 require a shared merge window for `AutomationConfig.cs`, `ActionExecutor.cs`, ticket persistence/API, and UI status. R8 likewise requires a shared member/config field for harness selection. These tasks cannot be completed inside the original runner-only file list.

---

## Task R1: Policy chokepoint — read contracts at the runner boundary (P3)

**Description:** Introduce `Policy/ContractPolicy.cs` that loads the dispatched agent's versioned `contracts.json` entry (`dispatches`, `allowedWriteGlobs`, `riskClass`, `ticketExit`, optional `maxReviewCycles`) and exposes an evaluation API (`Evaluate(toolCall) → Allow | Warn | Block + reason`). No enforcement yet — this task is the model + loader + evaluator, fully unit-tested against the real template contracts. Network expectations are a later schema addition owned by CL, not an invented R1 default.

**Acceptance criteria:**
- [ ] Policy loads for all 33 template agents; missing/malformed contract entries produce an explicit `Block`-by-default result with a diagnostic
- [ ] Glob evaluation has documented gitignore-compatible positive-pattern semantics and table-driven tests for inside/outside scope, rooted paths, `**`, traversal, absolute paths, symlink escape, and explicit case mode. Negation/directory-only behavior is either implemented and tested or rejected at schema validation
- [ ] Risk-class → capability mapping covers every class present in the real contracts (including test/review/design/research/media/approval/monitoring variants); unknown classes Block with a diagnostic

**Dependencies:** none. **Size:** M (3–5 files).

## Task R2: Shadow-mode enforcement + violation receipts (P3)

**Description:** First prove the hook bridge (command helper, local HTTP hook, or another measured transport) can invoke the same `ContractPolicy` implementation. Then wire it into the Claude CLI invocation using `PreToolUse` hooks with schema-validated settings JSON generated per run and passed by `--settings`. File writes, Bash capability calls, and Claude network tools are evaluated. Shadow mode logs violations and appends a structured ticket comment (`policy-violation` receipt); nothing is denied. Host-side `httpRequest` is explicitly not covered here. This produces the glob-failure inventory that gates SP-1.

**Acceptance criteria:**
- [ ] Every dispatched run gets a validated hook config and a test proves Claude/mock acknowledged or invoked it; invalid/ignored settings fail the run visibly
- [ ] Hook latency has a cross-platform p50/p95 benchmark and an owner-approved budget before rollout (50 ms is a target, not an unmeasured assertion)
- [ ] Violations recorded per run: agent, tool, path/target, rule violated — queryable enough to review 33 agents' inventories
- [ ] A run with zero violations adds no noise (no empty receipts)

**Verification:** the mock CLI explicitly emulates the hook request/decision exchange, or a separate bridge integration test deliberately writes out of glob. Canned NDJSON replay by itself is insufficient because the current mock does not execute hooks.
**Dependencies:** R1. **Size:** M.

## Task R3: Block mode + git/outbound guards (P3 + U18 + U17)

**Description:** Add per-agent enforcement mode (`warn` | `block`) defaulting to `warn`, flipped per agent after owner review (SP-1). In block mode, out-of-glob writes fail closed and `git commit --no-verify` plus defined destructive git forms are hard-blocked (U18). Claude `WebFetch`/`WebSearch` and network-capable Bash calls follow the typed contract. Host-side `httpRequest` remains dry-run until the CL/shared `ActionExecutor` preflight can verify trusted owner approval; an agent-mutable label alone is insufficient. Denials produce receipts just like warnings.

**Acceptance criteria:**
- [ ] Runner integration tests: unauthorized write, `--no-verify`, destructive git, and unapproved Claude network tool each fail closed with a receipt; approved equivalents pass
- [ ] Shared-boundary integration test: host `httpRequest` is dry-run without trusted approval and sends only with it
- [ ] Enforcement mode is per-agent config, hot-reloadable without engine restart
- [ ] CMS dispatch automation still works end-to-end when labels align (regression)

**Dependencies:** R2 + owner sign-off at SP-1. **Size:** M.

**Checkpoint SP-1 (with lanes CX-T, GM):** inventory reviewed, contracts corrected, block mode enabled per agent.

---

## Task R4: File-ownership leases for parallel runs (T11)

**Description:** Extend the policy layer with per-ticket file leases: when an automation dispatches an agent against a ticket, the ticket's declared file scope (from P9 handoff artifact, or fallback: agent globs) is leased; a second concurrent run whose scope intersects an active lease is queued or blocked. Introduce a SQLite lease table in the per-project DB with atomic acquire/release and a durable reaper modeled on, but not reusing, the current in-memory concurrency reaper.

**Acceptance criteria:**
- [ ] Two concurrent runs with disjoint scopes both proceed; intersecting scopes serialize with a receipt explaining why
- [ ] Stale leases from crashed runs are reaped on a defined cadence and reassignable; acquire/reap races are transaction-tested
- [ ] Lease violations in block mode fail closed (this is enforcement, not prompt guidance)
- [ ] Worktrees do not bypass overlapping logical leases; overlap is still serialized for eventual merge safety

**Dependencies:** R3 (block mode real), P9 schema frozen (lane CL). **Size:** M.

## Task R5: Worktree-per-ticket execution (U6, part 1)

**Description:** In a documented shared merge window, CL adds the serialized per-automation `isolation: worktree` field and CX-R consumes it. The runtime creates/reuses a git worktree + branch (`ticket/<id>`) for the run, executes there, and records branch/worktree state durably on the ticket. Builds on the existing opt-in recipe in `doc/worktree-workflow.md`, productized.

**Acceptance criteria:**
- [ ] Worktree created idempotently per ticket; re-dispatches reuse it; Done+merged tickets get worktree cleanup
- [ ] Runs in worktrees satisfy leases automatically (disjoint checkouts)
- [ ] Dirty/failed worktrees are never silently deleted; flagged to owner instead

**Dependencies:** R4 plus shared ownership contract for `AutomationConfig.cs`, `ActionExecutor.cs`, ticket persistence/API, and branch-state UI. **Size:** M.

## Task R6: Merge queue + integration gate (U6, part 2)

**Description:** In a shared CL/CX-R merge window, add the serialized `enqueueMerge` action and a durable ordered queue owned by committer. Each candidate is rebased, its configured integration command runs, and conflicts or red tests bounce the ticket to Blocked with a receipt. Merge requires a trusted owner approval record; an ordinary mutable label is orchestration metadata, not sufficient authorization.

**Acceptance criteria:**
- [ ] Two tickets editing the same file merge sequentially with the second rebased; a manufactured conflict routes to Blocked with actionable receipt
- [ ] Queue state survives engine restart
- [ ] Programmer + code-janitor + qa can run simultaneously on separate tickets end-to-end (the U6 headline)

**Dependencies:** R5; SP-3 (T2/P4 landed) before enabling by default. **Size:** L → split at implementation time into queue-mechanics and automation-wiring PRs.

---

## Task R7: Host-neutral runner interface (P13, part 1)

**Description:** Extract `IAgentRunner` from `ClaudeRunner` (dispatch, stream events, next-turn resume/queued steering, cost accounting, policy hooks, process containment). `ClaudeRunner` becomes the first implementation; existing mock-based runner tests keep working. Interface is the narrow adapter boundary — no six-way generation.

**Acceptance criteria:**
- [ ] Zero behavior change for Claude dispatches (full regression suite green, debug-instance scenarios replay identically)
- [ ] Interface covers: normalized stream events, session resume/restart, queued steering delivery, per-run usage/cost when reported, policy hook injection, process containment
- [ ] Runner consumers depend on `IAgentRunner`; `GigaClaw.ClaudeMock` remains a process fixture and does not pretend to implement the in-process interface

**Dependencies:** R3 (policy hooks are part of the contract). **Size:** M.

## Task R8: Codex CLI harness (P13, part 2)

**Description:** After capturing and committing real `codex exec --json` fixtures, implement `CodexRunner : IAgentRunner` for Codex CLI subprocesses. Parity is defined in terms the CLI supports: JSONL streaming, next-turn resume or explicit restart, queued steering, usage/cost when available, policy preflight/hooks, and process containment. Agents opt in through a new typed harness field, not by overloading the model string. Fall back cleanly (route to Claude + receipt) when a required capability is missing.

**Acceptance criteria:**
- [ ] Parity for one designated agent (suggest: programmer on the debug instance): streaming output, resume-or-restart semantics, queued steering, usage/cost capture when emitted (otherwise explicit “unavailable”), policy enforcement, exit-column contract
- [ ] Harness selection is per-member config; default remains Claude everywhere
- [ ] A mock Codex CLI scenario exists so QaRunner stays hermetic

**Dependencies:** R7 plus shared ownership contract for `Member`, member migration/API, initialization, and runner resolution. **Size:** L → split: fixture + process/stream adapter, then resume/usage, then policy parity.

**Checkpoint SP-4:** U6 end-to-end demo + P13 parity report to owner.

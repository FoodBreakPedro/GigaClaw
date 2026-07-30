# Lane CX-T — Codex Tooling (greenfield, isolated)

**Tool:** Codex CLI (+ subagents). **Branch:** `lane/cx-tooling` (own worktree).
**File scope (single writer):** new `GigaClaw.Catalog/` and `GigaClaw.Eval/` projects, generated `catalog.json` and `doc/catalog.md`, `tools/`, `.github/workflows/`; for P4, `Ticket.cs`, `TicketSummary.cs`, `TicketService.cs`, `TodoDbContext.cs`, ticket API contracts/endpoints, and the board dependency badge. CL owns the `dependenciesResolved` condition spec/evaluation/editor and receives the CX-T edge-query interface at the SP-2→SP-3 merge window. T6 separately expands scope to the exact template-composition files listed in that task.
**Out of scope:** runner internals (CX-R), automation vocabulary semantics (CL).

Verification bar: existing suite green in Release plus explicit build/test commands for each new project (Prove-It, no CS errors). `dotnet test GigaClaw.Core.Tests` alone does not discover tests in a new project.

## Validated catalog/eval definitions

- An agent is a direct child directory of `ProjectTemplate/Agents/` containing `SKILL.md`; `scripts/` is not an agent.
- Baseline truth is 33 agents, 33 contracts, 29 automations (28 enabled), 12 explicit model mappings plus project fallback, and 9 team definitions (`all` plus 8 specialty teams). “Model present” reports explicit and resolved values separately.
- A dispatch binding includes either an explicit `runAgent.agent` or membership in the assignee placeholder's allowed set. Enabled and disabled bindings are reported separately.
- CI gates deterministic generation, schema, and committed drift immediately. Missing binding invariants are reported in baseline mode and become hard failures with `--strict` only at SP-1 after GM/CL fixes (including `content-writer` team membership and model completion).
- `tools/check-automation-drift.sh` compares initialized projects/ventures against the template. Catalog checks do not replace that behavior, so the script is not retired without a per-project equivalent.
- README currently has no canonical numeric inventory marker. A count mismatch check requires an explicit generated marker or section; it must not scrape incidental prose.
- Eval baselines are deterministic committed inputs under `GigaClaw.Eval/baselines/`. Per-run reports go to a declared gitignored artifact root. Prompt budget source, units, and thresholds are versioned configuration, not hard-coded assumptions.

---

## Task T1: Typed system catalog generator (P20, part 1)

**Description:** A generator console tool that produces a template-static typed catalog — agents × contracts × explicit/resolved models × teams × enabled/disabled automations × scripts — by parsing `ProjectTemplate/Agents/**`, `AgentTeamService.cs`, and the automation config. Output: root `catalog.json` plus generated `doc/catalog.md`. This is not a claim about the mutable member rows in any initialized project.

**Acceptance criteria:**
- [ ] Catalog reports, per agent: contract present, explicit model mapping and resolved fallback separately, ≥1 specialty-team membership, ≥1 enabled/disabled dispatching automation, eval baseline present (field added now, populated by T4)
- [ ] Current known truths reproduce exactly: 33 agents, 33 contracts, 29 automations (28 enabled), 12 explicit model mappings, 9 team definitions, and the `content-writer` specialty-team gap
- [ ] `doc/catalog.md` regenerates deterministically (stable ordering, no timestamps)

**Dependencies:** none. **Size:** M.

## Task T2: CI enforcement of catalog invariants (P20, part 2)

**Description:** CI regenerates the catalog and fails immediately on malformed inputs or drift between generated and committed output. Baseline mode reports missing contract/model/team/automation bindings; `--strict` fails on them and becomes the CI default at SP-1 after owning fixes land. README count enforcement is enabled only for an explicit generated inventory marker. The initialized-project drift checker remains until catalog tooling implements equivalent per-project comparison.

**Acceptance criteria:**
- [ ] Baseline CI red on uncommitted catalog drift and malformed/unreadable bindings; strict-mode fixture is red for a deliberately introduced unbound agent
- [ ] If a README generated marker is added, a mismatch fixture fails; otherwise no incidental prose is treated as a count contract
- [ ] `tools/check-automation-drift.sh` is retained until a separately tested per-project parity feature replaces it; “one green week” is an operational rollout gate, not code acceptance
- [ ] Local `dotnet run --project GigaClaw.Catalog -- check` gives the same baseline verdict as CI; `--strict` gives the future SP-1 verdict

**Dependencies:** T1. **Size:** S.

**Checkpoint SP-1 (with CX-R, GM):** catalog green including the GM defect fixes.

---

## Task T3: blockedBy / blocks dependency edges (P4)

**Description:** First-class directed dependency edges in a normalized SQLite join table (not serialized list columns). CX-T owns atomic persistence, cycle validation, REST endpoints, `TicketSummary` projection, an edge-query interface for automation, and the board badge. CL owns the `dependenciesResolved` condition spec/evaluator/editor in the coordinated merge window. Parent/subticket stays as-is; edges are the critical-path primitive T2/U7/T5/T6 build on.

**Acceptance criteria:**
- [ ] Adding an edge that forms a cycle fails with a clear API error; test covers 2-node and transitive cycles
- [ ] CX-T tests edge CRUD, duplicate/self-edge rejection, 2-node/transitive cycles, deletion cleanup, migration, and query shape; CL integration test proves `dependenciesResolved` blocks dispatch and unblocks when every blocker reaches Done
- [ ] Existing boards migrate cleanly (ALTER TABLE try/catch pattern), zero data loss on the debug instance
- [ ] Concurrent edge creation is transaction-safe and cannot admit a cycle through a check/write race

**Dependencies:** none technically; merge window coordinated with CL before their T2 work starts. **Size:** M.

---

## Task T4: Eval harness — static layer (O6, part 1)

**Description:** `dotnet run --project GigaClaw.Eval -- <agent|all>` (optionally packaged later as `gigaclaw eval`) performs static checks first: SKILL.md/frontmatter parses, contract entry is catalog-consistent, memory stub is present, model resolves through explicit mapping or fallback, referenced scripts exist, and prompt-size budget from versioned eval config is respected. A warmed Release run targets <2 seconds for all agents; CI records the actual duration.

**Acceptance criteria:**
- [ ] All 33 template agents evaluated; current failures (if any) reported, not hidden
- [ ] Baseline and strict exit modes are defined like the catalog; JSON reports go to a configured gitignored artifact directory, while reviewed baselines are committed under `GigaClaw.Eval/baselines/`
- [ ] Wired into the catalog's `evalBaseline` field

**Dependencies:** T1 (catalog is the input). **Size:** M.

## Task T5: Eval harness — replay + LLM judge (O6, part 2)

**Description:** Canned-ticket replay dispatches via the mock CLI (hermetic) or real CLI (opt-in, costed) against fixture tickets. A deterministic golden/rule judge makes mock replay CI-stable. An optional LLM judge scores the same output against the agent rubric using CL's A11 verdict schema, recording model/version/settings and tolerances. Monte Carlo mode applies only to real model runs (N runs, variance/confidence interval, hard cost cap).

**Acceptance criteria:**
- [ ] ≥1 fixture ticket per pipeline family (dev, blog, growth, media, governance); fixtures live in `GigaClaw.Eval/fixtures/`
- [ ] Both judges emit schema-valid verdicts; same fixture + mock + deterministic judge is byte-identical, while LLM-judge variance is measured rather than asserted away
- [ ] Monte Carlo mode caps cost and prints per-run + aggregate cost

**Dependencies:** T4, A11 schema frozen (CL, SP-1→SP-2 window). **Size:** L → split: replay runner, then judge, then Monte Carlo.

**Checkpoint SP-2:** baseline eval recorded for all 33 agents; regressions block CI for changed agents.

## Task T6: Pack infrastructure implementation (O7 — spec from lane CL)

**Description:** Implement the pack manifest + installer designed by CL (see packs-and-later.md): packs as versioned directories with manifest (agents, automations, contracts fragment, models fragment, team definitions, dependencies, permissions), `AgentsTemplateService` composes selected packs at Initialize, catalog + eval validate composed output. Before work starts, expand CX-T ownership explicitly to `AgentsTemplateService.cs`, `GigaClaw.Core.csproj` embedded-resource rules, initialization API/UI flow, and adjacent tests; CL/GM must cede or merge their config fragments in a scheduled window.

**Acceptance criteria:**
- [ ] Core template refactors into `core` pack with zero behavior change (init diff empty vs today)
- [ ] Installing/removing a pack on the debug instance adds/removes exactly its manifest contents; unused packs add no runtime context
- [ ] Catalog CI (T2) and eval static layer (T4) run per-pack

**Dependencies:** T1/T2/T4; CL's O7 design (Phase 4 start). **Size:** L → split per manifest/composition/validation.

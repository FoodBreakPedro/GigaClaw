# Packs, Later Pilots, and Do-Not-Build

## Phase 4 — Packs (gated on SP-2 + O7)

O7 flow: CL designs (C9) → CX-T implements (T6) → core template extracted as `core` pack with zero behavior change → packs land in priority order below. **Binding rule (CI-enforced via P20):** every pack agent ships with a contract entry, a model default with stated criterion, a team membership, a dispatching automation, and an eval fixture. No exceptions — this is what separates these packs from the 203-agent catalog anti-pattern.

### Pack 1: Security Assurance (A3/T3) — proves the pack infra

Agents: security-auditor, threat-modeler, dependency/supply-chain reviewer, secrets reviewer. Team: `security-review` — the 4-lane parallel preset (plugs into C8's parallel-review machinery as the security lanes). Automations: security gate on Review for `code`-labeled tickets; weekly dependency-audit cron. All verdicts typed (BLOCK on any critical finding = hard veto). Unanimous blind spot across both source-verified analyses; port role shape from wshobson `plugins/agent-teams` security preset + `comprehensive-review`.
*Note (wshobson caveat): avoid the Fable tier for security-analysis agents — safety classifiers silently fall back to Opus at higher price.*

### Pack 2: Incident & Debug (A5/T6)

Agents: error-investigator, hypothesis-investigator ×N (from G5), debug-lead, rollback specialist. Team: `hypothesis-debug` (C8 wiring). Automations: `bug`-labeled ticket in Todo → investigator dispatch; repeated qa-tester BLOCK verdicts on one ticket → escalate to hypothesis-debug TeamRun.

### Pack 3: Architecture & Data (A7)

Agents: api-architect, db-architect, migration-specialist, performance-engineer. Automations: `design`-labeled tickets get an architecture pass before programmer dispatch; performance-engineer joins parallel-review as an optional lane. Fills the design phase the dev pipeline currently skips (groomer → programmer directly).

### Pack 4: Language specialists (A9)

csharp-pro (GigaClaw's own stack first), python-pro, typescript-pro. Dispatch rule: decision-engine (or the C5 router work, if landed) selects specialist vs generic programmer from ticket labels/content; generic programmer remains the default and the fallback.

### Pack 5: Truth registries (P7/P12) — pattern port, prerequisite for Pack 6

Append-only NDJSON registries with sole-writer proposal rule: entities, claims, consent, launches, channels. Engine support: `appendRegistry` action + registry read injection for marketing agents; canonical facts / run evidence / retained knowledge kept as three distinct stores (P12). CL implements the action; GM authors the guidance. Adopt the shape of aaron's protocol layer, not its file inventory.

### Pack 6: Marketing — optimized pack (A8, owner-selected scope)

Disciplines now: **Email lifecycle** (SEND-style gate; extends email-copywriter into setup→engage→nurture→deliver), **Launch orchestration** (RAMP as a parent-ticket TeamRun with sub-tickets per channel — maps directly onto existing decomposition machinery), **Social program** (ECHO; extends growth-writer), **SEO/GEO deepening** (CORE-EEAT + CITE gates upgrading blog-seo). All quality gates are A11 typed verdicts; all claims/consent flow through Pack 5 registries; all outbound mutations dry-run-by-default (U17).
Deferred, product-dependent: **Paid Ads (ROAS)** and **Influencer (STAR)** — complete the full funnel later; they need external ad-platform/outreach integrations the board cannot yet verify, and the panel scored the full pack product-dependent (60).

## Later pilots (post-SP-4, shadow mode, each individually gated)

| Pilot | Gate | Shape |
|---|---|---|
| P16 semantic memory shadow index | A real retrieval problem measured at current scale | Read-only index over MEMORY.md topics + ticket evidence; markdown stays authoritative; benchmark exact vs semantic recall with human judgment before any authority. Discount "sub-ms HNSW" claims (ruflo's own audit: 1.48×, not 150×) |
| O3/O4 outcome-grounded model routing | O6 baselines exist **and** something consumes `scores.json` | Shadow recommendations logged beside deterministic choices; learn only from QA/owner/evaluator outcomes, never process exit codes; calibrated confidence + one-click rollback before any live routing |
| O5 context compression | Measured prompt-size pain (catalog can report prompt budgets) | Compress large tool outputs pre-prompt; headroom/claude-context-mode patterns |
| O2 cost dashboards + forecasting | Nice-to-have; CostTracker data already durable | Visual/predictive layer over existing per-ticket cost records. **2026-08-02**: being delivered by the system-level Mission Control page (Option A — Operations, Phase 4 of the automation & UX improvement plan); its ops/cost strip is the O2 slot. |
| U15 background-worker catalog | After U6; workers = plain interval automations | testgaps-style workers as named cron automations, not a new subsystem |
| U16 TDD-guard hook | After P3 hooks exist | PreToolUse block on implementation-before-failing-test for `code` tickets |
| Full-funnel marketing completion (paid ads, influencer) | Product demand + Pack 6 in real use | Extends Pack 6 under the same binding rule |

## Do-not-build (owner-confirmed 2026-07-30)

| Item | Reason (adjudicated) |
|---|---|
| T16 zero-trust federation | Federating one laptop with itself; both source-verified analyses scored 2 |
| T15 Raft/Byzantine/Gossip consensus | ruflo ships topology metadata; consensus lives in alpha libraries; own audit grades claims fabricated |
| A14 queen/swarm-leader agent | AutomationEngine in a costume; ruflo's `agent_spawn` writes a record, doesn't spawn. **Revisit triggers (2026-08-02, D1)**: reconsider a standing coordinator only when (a) Pack 4 specialist packs land and label-routing proves insufficient, (b) cross-pipeline prioritization under budget is needed, or (c) `scores.json` gains a consumer enabling outcome-informed routing. |
| P14 GOAP A* planning | README-only, zero code corroboration; five columns and a groomer don't need A* |
| P17 Ed25519 hash-chained receipts | Single owner — you are the threat model. (Plain policy receipts ship with P3) |
| O15 quantization / cross-cloud | Wrong scale; benchmarks self-graded fabricated |
| A12 business-ops packs | Owner deselected; lowest adjudicated value (2) |
| DevOps/k8s/cloud/terraform roster (A4) | No deploy target; revisit only if `publish-stable.ps1` grows into a real release lane |
| Six-way harness generation | P13 is deliberately narrow: one interface, one proven second harness (Codex). Gemini-as-harness only after Codex parity holds |

Removed from do-not-build by owner decision: **O7 modular packs** (now Phase 4 foundation) and **P13 second harness** (now CX-R R7/R8, narrow).

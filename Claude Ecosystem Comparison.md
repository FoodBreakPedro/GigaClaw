# GigaClaw vs. the Ecosystem — Agents, Teams, Patterns, Automations, Optimizations

**Date:** 2026-07-30 **Method:** Opus subagent deep-dive on GigaClaw; Sonnet subagents on wshobson/agents and ruvnet/ruflo; Haiku subagents on Filimindji/awesome-claude-code and aaron-he-zhu/aaron-marketing-skills. All headline numbers and cited files were then re-verified directly against source.

**Ranking scale used in all tables:** 1 = GigaClaw does it better · 2 = about the same, no real value in changing · 3 = decent improvement · 4 = big improvement · 5 = covers a blind spot GigaClaw has

---

## 1. GigaClaw Baseline (verified ground truth)

GigaClaw is a Blazor Server + .NET kanban board that _is_ the orchestrator: agents are markdown skills, the board is the message bus, the runtime is C#. A background `AutomationEngine` polls triggers, evaluates conditions, and dispatches `claude` CLI subprocesses against tickets.

**Verified scale:** 33 agents · 29 automations (28 enabled) · 8 trigger types · 10 condition types · 10 action types · 9 teams · 15 helper scripts · 525 xUnit tests.

### 1.1 Agents (33)

Each agent = `ProjectTemplate/Agents/<slug>/` with `SKILL.md` + `memory/MEMORY.md`. Shared context: `preamble.md`, `VOICE.md`, `BRAND.md`. Models seeded from `models.json` (haiku: committer, groomer, documentalist, trend-researcher · sonnet: blog-researcher, growth-writer, qa-tester, content-writer · opus: blog-reviewer, decision-engine, approval-gatekeeper, evaluator · rest fall back to project default).

|Group|Agents|
|---|---|
|Board lifecycle & governance|producer, groomer, decision-engine, approval-gatekeeper, system-watchdog, evaluator, documentalist, committer|
|Engineering|programmer, qa-tester, code-janitor|
|Content|blog-researcher, blog-writer, blog-reviewer, blog-seo, blog-translator, content-writer, content-series-planner, wellness-coach|
|Growth|growth-writer, lead-magnet-creator, email-copywriter, trend-researcher|
|UX|ui-designer, ui-auditor, design-researcher|
|Data|data-analyst, competitive-analyst|
|Local media|local-media-director, local-image-artist, local-motion-artist, local-media-compositor, local-media-reviewer|

### 1.2 Organization

Nine teams in `GigaClaw.Core/Services/AgentTeamService.cs`: All Teams, Software Engineering, Content Engine, Growth Marketing, UX & Product Design, Data & Intelligence, Governance & Ops, Health & Performance, Local Media Creation. Teams filter the member roster per board view; producer/committer/evaluator/documentalist are embedded in nearly every team as shared connective tissue.

`ProjectTemplate/Agents/contracts.json` is the governance layer: per-agent `dispatches` (which automations may run it), `ticketExit` (allowed exit columns), `allowedWriteGlobs` (filesystem write scope), `riskClass` (code-write, board-write, git-write, content-write, memory-write, …), `maxReviewCycles`, plus defaults for atomic handoff and retry backoff.

### 1.3 Automations (29, in `ProjectTemplate/Agents/automations.json`)

Engine vocabulary (`GigaClaw.Core/Automation/AutomationConfig.cs`, `Triggers/`):

- **Triggers (8):** ticketInColumn, statusChange, subTicketStatus, interval (cron), gitCommit, ticketCommentAdded, agentInactivity, boardIdle
- **Conditions (10):** assignedTo, labels (negatable), ticketCountInColumn, allSubTicketsInStatus, ticketInColumn, ticketAge, hasParent, fieldLength, priority, minDescriptionLength
- **Actions (10):** runAgent, moveTicketStatus, addComment, createTicket, assignTicket, setLabels, httpRequest, executePowerShell, consolidateAgentMemory, commitAgentMemory

Every runAgent carries concurrencyGroup, maxTurns, retry backoff, debounce, and maxConsecutiveFirings; nearly every automation ends with consolidate + commit of agent memory.

### 1.4 Agent linking — the pipelines

- **Dev:** groomer enriches Backlog → dispatch (Todo→InProgress, run assignee) → programmer → Review → qa-tester gate → Done → committer (skips decision/adr) + evaluator scoring → documentalist on gitCommit; code-janitor nightly at 03:00.
- **Blog:** blog-researcher → blog-writer → blog-reviewer 5-category gate on Review → blog-seo → label `translate` hands off to blog-translator (assignTicket + move to Todo) → `ready-for-cms` + `approved` on Done fires httpRequest POST to CMS + executePowerShell archive + `image-upgrade-pending` label.
- **Content (AD-7):** content-writer Todo/InProgress loops → blog-reviewer gate on Review → auto-approve on Done when no outbound/publish/external/deploy label → CMS dispatch.
- **Growth:** growth-writer / lead-magnet-creator / email-copywriter on Review → approval-gatekeeper (opus); label lifecycle `pending-approval` → `approved` on owner Done, invalidated if the ticket returns to Todo.
- **Media:** local-media-director → local-image-artist / local-motion-artist → local-media-compositor → local-media-reviewer provenance gate on Review; system-watchdog hourly health probe.
- **Governance:** decision-engine writes ADR receipts when decision-labeled tickets close; owner comments re-dispatch the assignee; Blocked tickets >72h nudge the owner.

### 1.5 Infrastructure

`GigaClaw.Core/Automation/`: RunConcurrencyGate (per-group serialization), CostTracker, SessionRegistry, TriggerStateStore, RunStateManager, ClaudeStreamPump, ProcessJobObject (child-process containment), GitRepositoryWatcher. Drift detection via `tools/check-automation-drift.sh`. 525 tests.

---

## 2. The Four Comparison Repos — Same Questions

### 2.1 wshobson/agents — 203 agents, 91 plugins, 109 commands

- **Agents:** ~203 distinct agents (204 agent .md files on disk, duplicated into plugins for isolation). Frontmatter: name, description with trigger phrase, model, optional tools allowlist. Explicit five-tier model policy (fable/opus/inherit/sonnet/haiku: 55 opus, 71 sonnet, 25 haiku, 52 inherit) with the tier→model map centralized in `tools/adapters/capabilities.py`.
- **Organization:** plugin marketplace (`.claude-plugin/marketplace.json`); "duplicate, don't depend" — agents copied into each plugin rather than cross-referenced, with CI name-collision checks. Single source, published to 6 harnesses via adapters.
- **Automations:** 109 slash commands; 16 orchestrator commands that persist every step to `.{command}/NN-step.md` + `state.json`, hard-stop at human checkpoints, and resume from an in-progress session. Real hooks: `protect-mcp` (PreToolUse Cedar policy evaluation + PostToolUse Ed25519-signed hash-chained receipts of every tool call), `review-agent-governance` (approval-flag-file gate), `block-no-verify` (hard-blocks `git --no-verify`).
- **Linking:** Pattern A — ephemeral subagents chained by an orchestrator command, context passed exclusively through files on disk; parallel fan-out at fixed steps. Pattern B — persistent Agent Teams (`team-lead` spawns teammates with TeamCreate/TaskCreate/SendMessage, exclusive file-ownership boundaries).
- **Teams/workflows:** full-stack-feature (9 steps, 3 checkpoints), smart-fix incident response (10 steps), security-hardening, ml-pipeline (data-scientist → data-engineer → ml-engineer → mlops-engineer → performance-engineer), c4-architecture docs (bottom-up 4-level chain), team-review / team-debug (competing hypotheses) / team-spawn security / team-spawn migration presets.

### 2.2 ruvnet/ruflo (claude-flow successor)

- **Reality check first:** primarily TypeScript, not Rust (`Cargo.toml` says so itself). `agent_spawn` does not spawn a worker — it writes a coordination record; the host LLM executes ("claude-flow = LEDGER, Codex = EXECUTOR", `AGENTS.md`). The project's own in-tree audit (`docs/reviews/intelligence-system-audit-2026-05-29.md`) grades several headline claims as fabricated (Flash-Attention speedups were `Math.random()`; "HNSW 150x" measured 1.48x) while confirming others as real. Its hooks also contain opt-out monetization nudges (`hook-handler.cjs`). Adopt patterns, not the platform.
- **Agents:** 108 persona markdown files across 25 categories (core coder/tester/reviewer/researcher/planner; swarm coordinators; consensus roles; github; sparc; hive-mind queen/worker with 3 queen types × 8 worker specializations).
- **Organization:** swarm topologies (hierarchical, mesh, ring, star, hybrid); memory blackboard over namespaced keys (`swarm/{agent}/status`, `swarm/shared/*`); full MCP server (OAuth, rate limiting, pooling); real PBFT/Raft consensus in the `@claude-flow/swarm` layer.
- **Automations:** 8 Claude Code lifecycle hooks → a defensively engineered `hook-handler.cjs` (global 5s force-exit timeout, sha256 dedup lockfiles, explicit failure-signal capture feeding the learning loop); 12 named background workers on cron-like schedules (map, audit, optimize, consolidate, testgaps, ultralearn, deepdive, document, refactor, benchmark, predict, preload).
- **Linking:** memory-key blackboard + EventEmitter message bus + task dependency resolution; confirmed-real closed-loop learning: success/failure verdicts shift persisted pattern confidence and Q-learning routing weights (`persistent-sona.ts`), plus a Thompson-sampling bandit for 3-tier model routing.

### 2.3 aaron-he-zhu/aaron-marketing-skills — 120 skills, v19

- **Agents/skills:** 120 = 7 disciplines × 16 (4 phases × 4 skills) + 8 protocol skills. Disciplines: Narrative (TALE), SEO/GEO (CORE-EEAT + CITE), Social (ECHO), Email (SEND), Paid Ads (ROAS), Influencer (STAR), Launch (RAMP).
- **Organization:** a four-layer "marketing OS": L1 Strategy (Narrative) → L2 Channels → L3 Orchestration (Launch) → L4 Protocol. One shared 7-section skill contract; every skill self-declares `discipline` + `phase` and its next-best handoff. Authoritative typed topology in `references/system-catalog.json`, human docs generated by script, counts CI-enforced.
- **Automations:** 8 slash commands per discipline + `/auto` which routes any natural-language goal to the right skill chain; Bash/Python-stdlib runtimes for hooks, scoring, registry events; outbound mutations dry-run by default (require `--live`).
- **Linking:** 8 benchmark frameworks drive 8 auditor-class gates emitting structured machine-checkable verdicts with veto items (e.g., `content-quality-auditor` CORE-EEAT gate); 7 append-only truth registries (entity, creator, offer/claims, consent, launch, channel, narrative) are canonical state; HOT/WARM/COLD memory carries findings, scores, and open loops between skills and sessions.
- **Extras:** AI-slop detector + banned-phrase list; keyless "Tier 1" capability profiles; per-agent compatibility matrix across 70+ hosts.

### 2.4 Filimindji/awesome-claude-code — the ecosystem map

A curated README. Most relevant clusters for GigaClaw: kanban (vibe-kanban, 27k★ — validation that GigaClaw's core concept is the ecosystem's most-wanted UI); orchestration (Claude-Flow, Citadel, ai-maestro, claude-squad); memory (claude-mem 82k★, cipher, claude-cognitive); context compression (headroom 62k★, claude-context-mode: "315 KB becomes 5.4 KB"); parallelism (ccpm GitHub-Issues + git-worktrees, crystal, claude-code-merge-queue); observability (ccusage, codeburn, claude-code-hooks-multi-agent-observability); CI/remote (claude-code-action GitHub Action, claude-hub autonomous GitHub bot, Claude-Code-Remote email control); enforcement (tdd-guard); security scanning (medusa, skill-scanner, raptor).

---

## 3. Category Tables (ranked 1–5)

### A. Agents

|Finding|Source|Rank|
|---|---|---|
|Board-native lifecycle agents (producer, groomer, approval-gatekeeper, system-watchdog, evaluator, documentalist, committer) — no other repo has agents that operate a live board|GigaClaw `ProjectTemplate/Agents/`, `automations.json`|**1**|
|Security agents: security-auditor, threat-modeling-expert, backend/frontend/mobile-security-coder|wshobson `plugins/comprehensive-review/`, `plugins/security-scanning/`|**5**|
|DevOps/deployment agents: deployment-engineer, kubernetes-architect, cloud-architect, terraform|wshobson `plugins/cloud-infrastructure/`, `plugins/kubernetes-operations/`|**5**|
|Paid-ads discipline (16 skills, ROAS framework + ad-account-auditor)|aaron `ad/`, `references/roas-benchmark.md`|**5**|
|Influencer discipline (16 skills, STAR framework, fit-scorer)|aaron `influencer/`|**5**|
|Launch orchestration discipline (16 skills, RAMP)|aaron `launch/`|**5**|
|Database/data/ML-engineering agents (database-architect, data-engineer, ml-engineer, mlops-engineer)|wshobson `plugins/machine-learning-ops/`|**4**|
|Email as a full discipline (setup→engage→nurture→deliver, SEND gate, deliverability) — GigaClaw's email-copywriter is copy only|aaron `email/`|**4**|
|Organic-social program management (ECHO, 16 skills) — growth-writer covers ghostwriting only|aaron `social/`|**4**|
|Language/framework specialists (python-pro, rust-pro, fastapi-pro, …) vs. one generic programmer|wshobson `plugins/python-development/`, `plugins/systems-programming/`|**3**|
|SEO as a 4-phase 16-skill line with CORE-EEAT + CITE gates vs. single blog-seo agent|aaron `seo-geo/`|**3**|
|error-detective + debugger root-cause duo|wshobson `plugins/error-debugging/`|**3**|
|accessibility-expert (WCAG audits) — ui-auditor covers style, not accessibility|wshobson `plugins/ui-design/`|**3**|
|context-manager agent for multi-agent handoff hygiene|wshobson `plugins/agent-orchestration/`|**3**|
|Generic persona collections (coder/tester/reviewer personas)|ruflo `.claude/agents/core/`|**2**|

### B. Teams

|Finding|Source|Rank|
|---|---|---|
|9 persistent role-teams bound to a live board with shared connective agents|GigaClaw `AgentTeamService.cs`|**1**|
|Security-audit team: 4 parallel reviewers (OWASP / auth / dependency supply-chain / secrets)|wshobson `plugins/agent-teams/` (`/team-spawn security`)|**5**|
|Launch team — time-boxed cross-channel campaign orchestration|aaron `launch/`, `commands/launch.md`|**5**|
|Incident-response team (error detection → root cause → fix → parallel verification)|wshobson `plugins/incident-response/commands/smart-fix.md`|**4**|
|ML-pipeline team (data-scientist → data-engineer → ml-engineer → mlops → perf)|wshobson `plugins/machine-learning-ops/commands/ml-pipeline.md`|**4**|
|Competing-hypotheses debug team (N debuggers investigate in parallel, lead arbitrates evidence)|wshobson `/team-debug`|**3**|
|Migration team (plan → 2 parallel implementers → verifier)|wshobson `/team-spawn migration`|**3**|
|Full-stack feature team with 3 human checkpoints — GigaClaw's dev pipeline covers the flow, less structured on architecture/DB phases|wshobson `plugins/full-stack-orchestration/`|**3**|
|C4 architecture-documentation chain (c4-code → component → container → context)|wshobson `plugins/c4-architecture/`|**3**|
|Queen/worker hive-mind (3 queen types × 8 worker specializations)|ruflo `docs/USERGUIDE.md`, `.claude/agents/hive-mind/`|**2**|

### C. Patterns

|Finding|Source|Rank|
|---|---|---|
|Approval-flag gating before agent action — GigaClaw's label lifecycle + opus gatekeeper agent is richer than a flag file|wshobson `plugins/review-agent-governance/hooks/hooks.json` vs. GigaClaw approval automations|**1**|
|Machine-checkable auditor gates: numeric frameworks, structured verdicts, veto items — GigaClaw's reviewers emit prose rubrics|aaron 8 gates, e.g. `seo-geo/tune/content-quality-auditor/`|**4**|
|Natural-language router (`/auto`) that picks the right agent chain from a goal — GigaClaw dispatch is assignee-driven; the owner routes by hand|aaron `commands/auto.md`|**4**|
|File-ownership boundaries for parallel implementer agents ("one owner per file") — GigaClaw has static `allowedWriteGlobs` but serializes instead of parallelizing|wshobson `plugins/agent-teams/agents/team-lead.md`|**4**|
|Append-only truth registries (entity, claims, consent, launch, channel) as canonical state|aaron `protocol/`, `references/system-catalog.json`|**4**|
|Ed25519-signed, hash-chained receipts of every tool call — GigaClaw's ADR/comment receipts are not cryptographic|wshobson `plugins/protect-mcp/hooks/hooks.json`|**3**|
|HOT/WARM/COLD tiered working memory across sessions|aaron `memory/`|**3**|
|Shared-memory blackboard with namespaced keys for cross-agent state|ruflo `.claude/agents/swarm/hierarchical-coordinator.md`|**3**|
|In-tree self-audit grading marketing claims CONFIRMED/PARTIAL/FABRICATED|ruflo `docs/reviews/intelligence-system-audit-2026-05-29.md`|**3**|
|Typed system catalog + generated docs + CI-enforced topology counts — stronger than `check-automation-drift.sh`|aaron `references/system-catalog.json`, `scripts/generate-system-docs.py`|**3**|
|Progressive-disclosure hard caps (150-line context files, 8KB skills, `references/` overflow)|wshobson `docs/authoring.md`|**3**|
|State-file checkpointed orchestrators with resume — the board already gives GigaClaw durable, resumable state|wshobson orchestrator commands|**2**|
|Shared skill contract with self-declared handoffs — `contracts.json` is comparable|aaron skill contract vs. GigaClaw `contracts.json`|**2**|
|"Ledger vs. executor" framing — GigaClaw's board already is the ledger|ruflo `AGENTS.md`|**2**|
|Duplicate-don't-depend plugin isolation — irrelevant to a non-marketplace|wshobson plugin copies|**2**|

### D. Automations

|Finding|Source|Rank|
|---|---|---|
|Board-event engine: 8 triggers × 10 conditions × 10 actions with retry backoff, debounce, firing caps, concurrency groups — richest event model of the five repos|GigaClaw `AutomationConfig.cs`, `automations.json`|**1**|
|GitHub PR/issue integration — agent responds to issues, reviews PRs, runs in CI (claude-code-action, claude-hub); GigaClaw's gitCommit trigger is local-only|awesome-claude-code §Tools (claude-code-action 8.0k★, claude-hub)|**5**|
|Tool-level PreToolUse policy hooks (Cedar) — GigaClaw governs at ticket level; nothing stops a rogue Bash call mid-run|wshobson `plugins/protect-mcp/hooks/hooks.json`|**4**|
|Hook defensive engineering: global force-exit timeout, sha256 dedup lockfiles, failure-signal capture feeding learning|ruflo `.claude/helpers/hook-handler.cjs`|**3**|
|Named background-worker catalog (testgaps, ultralearn, predict, preload…) with priorities — GigaClaw crons cover audit/consolidate/optimize but not test-gap or predictive workers|ruflo `v3/@claude-flow/swarm/src/workers/worker-dispatch.ts`|**3**|
|TDD-enforcement hook (blocks implementation before failing test)|tdd-guard via awesome-claude-code|**3**|
|Dry-run-by-default outbound mutations requiring `--live` — GigaClaw's CMS httpRequest fires directly once labels align|aaron `resend.py`, `indexpush.py`|**3**|
|Hard-block dangerous git flags (`--no-verify`) at hook level|wshobson `plugins/block-no-verify/`|**3**|
|Continuous autonomous loop with intelligent exit detection — GigaClaw's resume + dispatch automations cover this|ralph-claude-code via awesome-claude-code|**2**|
|CI quality gates on agent definitions (validate/garden/386 tests) — GigaClaw has 525 tests + drift check|wshobson `Makefile`, `.github/workflows/validate.yml`|**2**|

### E. Optimizations

|Finding|Source|Rank|
|---|---|---|
|Dynamic model routing from outcome feedback (Thompson-sampling bandit, Q-learning weights) — GigaClaw's `models.json` is static and evaluator scores are never fed back|ruflo audit-confirmed routing, `persistent-sona.ts`|**4**|
|Context compression before the model (headroom, claude-context-mode: 315 KB → 5.4 KB)|awesome-claude-code §Tools|**4**|
|Parallel execution via git worktrees + merge queue — GigaClaw's RunConcurrencyGate serializes; worktrees would let programmer + code-janitor + qa run simultaneously|ccpm / crystal / claude-code-merge-queue via awesome-claude-code|**4**|
|Graduated eval framework for agents: static checks → LLM judge → Monte Carlo reliability — GigaClaw has no way to benchmark an agent before trusting it|wshobson `plugins/plugin-eval/`|**4**|
|Documented model-tier policy with centralized alias map — `models.json` covers 13 of 33 agents with no stated criteria|wshobson `AGENTS.md`, `tools/adapters/capabilities.py`|**3**|
|Token/cost dashboards (ccusage, codeburn) — CostTracker exists but is less visual/predictive|awesome-claude-code §Usage & Observability|**3**|
|Semantic/vector memory search over agent memory — MEMORY.md is grep-only|ruflo agentdb concept; cipher via awesome-claude-code|**3**|
|File-based inter-step context passing — tickets/comments/repo files already serve this role|wshobson orchestrators|**2**|
|Int8/RaBitQ quantization, consensus protocols, federation — wrong scale for a local single-owner board|ruflo|**2**|

---

## 4. Blind Spots (all the 5s, summarized)

1. **Security has no seat on the board.** No security-auditor, threat-modeling, or dependency-audit agent, no security team, no security gate before Done. Cheapest fix: port wshobson's security-auditor + the 4-reviewer parallel audit pattern as a GigaClaw team + Review-column automation.
2. **No DevOps/deployment capability.** Committer ends the pipeline at `git commit`. No deploy, infra, rollback, or release agents. wshobson's deployment-engineer/cloud-architect map cleanly onto new agents + a `deploy`-labeled pipeline that already has approval gates waiting.
3. **Marketing stack stops at content + copy.** Paid ads, influencer, and launch orchestration are absent — aaron's ROAS/STAR/RAMP lines are direct blueprints, and its Launch layer is exactly what a GigaClaw "campaign" parent-ticket with sub-tickets could orchestrate.
4. **No remote/GitHub surface.** All triggers are local. A claude-code-action-style GitHub integration (issues → tickets, PR review comments → owner-feedback trigger) would extend the board beyond the desktop.

## 5. Covered but Improvable — Priority Recommendations

1. **Structured auditor verdicts (rank 4, low effort).** Blog-reviewer, ui-auditor, and local-media-reviewer emit prose. Adopt aaron's gate pattern: each reviewer emits a JSON verdict (scores per category, veto items, pass/fail) validated by a contract script — `scripts/content_contract.py` already exists as the enforcement point.
2. **Close the evaluator loop (rank 4).** Evaluator already writes `scores.json`; nothing reads it. Feed per-agent scores into model selection (ruflo's bandit idea): demote an agent's model tier when scores are high, escalate on repeated failures.
3. **`/auto`-style routing through decision-engine (rank 4).** Let unassigned Backlog tickets be routed by decision-engine from the ticket text to the right agent + team, instead of owner hand-assignment.
4. **Worktree parallelism (rank 4).** Replace serialization-by-concurrency-group with git-worktree isolation per agent run + a merge-queue step owned by committer.
5. **Tool-level guardrails (rank 4).** Add PreToolUse hooks to the ClaudeRunner invocation (block `--no-verify`, block outbound network calls unless the ticket carries `approved`) — defense in depth under the ticket-level gates.
6. **Agent eval harness (rank 4).** A `gigaclaw eval <agent>` that replays canned tickets against an agent and scores outputs (wshobson's static → judge → Monte Carlo ladder) before an agent is trusted with dispatches.
7. **Tiered memory + compression (rank 3).** HOT/WARM/COLD split for MEMORY.md during consolidation, and compress large tool outputs before they hit the prompt.
8. **Machine-readable topology (rank 3).** Replace drift-check-by-script with a typed catalog (agents × automations × contracts × teams) that generates docs and fails CI on mismatch, like aaron's `system-catalog.json`.

## 6. Cautions

- **ruflo:** adopt its patterns (hook engineering, learning loop, self-audit culture), not the platform — its own audit documents fabricated benchmarks, and its hooks ship monetization nudges.
- **wshobson model note:** they explicitly avoid the Fable tier for security-analysis agents (safety classifiers silently fall back to Opus — same output, higher price).
- **GigaClaw's moat is real:** across all five repos, it is the only system where the orchestrator is a stateful board with contracts, risk classes, approval label lifecycles, per-agent memory commits, and cost tracking in one runtime. Everything recommended above composes onto that foundation; nothing replaces it.

---

## Appendix — Verification

Numbers re-checked directly against source: GigaClaw 33 agent dirs, 29 automations (28 enabled), 8 trigger classes in `Triggers/`, 9 teams, model seeds, full contracts.json inventory. Clones spot-checked: wshobson 91 plugin dirs / 204 agent files (docs claim 94/203 incl. 4 external git-subdir plugins); ruflo 108 persona files; aaron 120 SKILL.md files. All cited file paths confirmed to exist.
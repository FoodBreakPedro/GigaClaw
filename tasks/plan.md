# Implementation Plan: GigaClaw ↔ n8n ↔ PayloadCMS content pipeline

Status: awaiting review
Author: planning session, 2026-07-28

## Overview

Build a venture-aware content pipeline where **GigaClaw is the system of record and the place work happens**, n8n is reduced to two thin edges (ingress and egress), and a single consolidated PayloadCMS receives finished drafts tagged with the correct venture.

This is a deliberate divergence from both upstream KittyClaw and ZabsAIOS. We are not porting the 47-node Prime Job Runner. We are keeping its *learnings* — dedup, quality gating, brand-config-per-venture, status writeback — and re-homing each into whichever system already has the better primitive for it.

### The shape

```
  Telegram ─┐
  Discord  ─┼─→ n8n ingress ──→ GigaClaw ticket (Backlog)
  Schedule ─┘   (normalize +      │
  Trend cron    dedup)            │  board = the quality gate
                                  │  Todo → InProgress → Review → Done
                                  │  (runAgent: writer, then reviewer)
                                  ▼
                            httpRequest action ──→ ZabalaZone Payload
                                  ▲                 /api/ai/draft
                                  └── response written back as comment
```

n8n does **not** carry content state. GigaClaw does. This is the core inversion versus ZabsAIOS, where GitHub frontmatter was the state machine and n8n orchestrated it.

## Deployment topology

This is load-bearing for several tasks and was previously implicit. Writing it down.

| Host | Runs | Availability |
|---|---|---|
| **Linux server** | GigaClaw (production), n8n, SearXNG | 24/7 |
| **Mac** | Development, OpenMontage / ComfyUI, local model generation | Intermittent |
| **Cloud** | ZabalaZone PayloadCMS, venture sites | 24/7 |

The server reaches the Mac over **Tailscale** when the Mac is online. n8n runs containerized and reaches GigaClaw on the host via the Docker bridge (`172.23.0.1:5230`).

Three consequences the plan has to respect:

1. **The 24/7 path never depends on the Mac.** Ingress, agent runs, review, and CMS dispatch all live server-side and must complete with the Mac powered off.
2. **Anything needing local GPU is opportunistic and must degrade silently.** That is exactly AD-8's image upgrade, and it's why that work is a separate sweep rather than a step in the pipeline.
3. **Tailnet is the trust boundary.** ComfyUI ships with no authentication. It must bind to the Tailscale interface only, never `0.0.0.0` on a public NIC. Tailscale's own auth is what protects it — which is also the reason "GigaClaw API auth out of scope" is a defensible call rather than a shrug, provided GigaClaw stays off public interfaces too.

## Architecture decisions

### AD-1: One CMS, not two

Migrate HyperlaneTravels content into the ZabalaZone Payload instance. Hyperlane becomes a venture like any other.

**Rationale.** Two CMSes force venture→instance routing into every egress path permanently — a Switch node, two credential sets, two contracts, two schemas to keep in sync. One CMS reduces the entire problem to a `ventureSlug` field that ZabalaZone already resolves, indexes, and exposes for anonymous filtering via `payload/hooks/resolveVentureSlug.mjs`. The existing code already anticipates this: `lib/ai-draft.mjs` defines `REJECTED_VENTURE_SLUGS = {hyperlane, hyperlanetravels}` with a message pointing at the other CMS.

**Cost.** A content migration and a DNS/frontend decision for hyperlanetravels.com. Deferred to Phase 5 — the pipeline is built venture-agnostic from day one, so Hyperlane joins by adding a row, not by changing code.

**Reversibility.** Moderate. Keep the Hyperlane instance read-only rather than deleting it until the migration is verified.

### AD-2: GigaClaw gains an `httpRequest` action type

Add a 10th `ActionSpec` to `GigaClaw.Core/Automation/`. It must capture the response body and expose it to subsequent actions in the chain, because the CMS returns `{id, slug, adminUrl}` that has to be written back onto the ticket.

**Rationale.** The current 9 action types (`runAgent`, `moveTicketStatus`, `setLabels`, `assignTicket`, `addComment`, `commitAgentMemory`, `consolidateAgentMemory`, `executePowerShell`, `createTicket`) cannot make an HTTP call. The alternatives were polling from n8n (adds latency and a second source of truth for "was this dispatched?") or shelling out via `executePowerShell` (works — `ShellResolver.ResolvePowerShell()` is cross-platform — but puts URLs and secrets in shell strings, has no typed response handling, and is invisible to the automations UI). Given the explicit decision to diverge from KittyClaw, a first-class action is the right call and unlocks far more than this one pipeline.

### AD-3: The board is the quality gate

Draft → critique → revise is expressed as columns and `TicketInColumnTrigger`, not as n8n nodes.

**Rationale.** `TicketInColumnTriggerSpec` already carries `MaxConsecutiveFirings`, `RetryBackoffSeconds`, `ExhaustedStatus`, and `ExhaustedComment`. That is precisely the bounded-retry-with-escape-hatch behaviour the Prime Job Runner hand-rolls across `Quality gate ok?` → `Gate retry?` → `Build gate retry` → `GH PUT job gate iteration` → `Carry gate sha`. Using the native primitive removes five nodes, a GitHub round-trip, and the sha-carrying bug surface.

### AD-4: One GigaClaw project per venture

Project slug **is** the venture slug: `gamelifteat`, `gamepowergym`, `hyperlanetravels`, `zabsconsulting`, `karalungaming`, `personal-brand`.

**Rationale.** `ProjectTemplate/Agents/` already models `BRAND.md`, `VOICE.md`, and a per-project agent fleet — brand voice is already the per-project unit, mirroring ZabsAIOS `ventures/{v}/brand.md`. Each project also binds a git workspace, and each venture already has its own repo.

**Risk.** Six divergent copies of `automations.json` — the same duplication that made Trend Jacking (36 nodes, 4 hardcoded lanes) worse than Basic (18 nodes, config-driven loop). Mitigated by Task 12, a template-sync check.

### AD-5: Ingress normalizes; sources stay thin

Every source (Telegram, Discord, schedule, trend cron) is a thin capture workflow that calls one shared normalizer subworkflow, which owns validation, dedup, venture resolution, and the GigaClaw POST.

**Rationale.** This is the one ZabsAIOS pattern worth copying wholesale — `Telegram Capture → Telegram Prime Create Job → POST create job` already works in production. Adding Discord then costs one trigger node, not a pipeline.

### AD-6: Dedup at ingress, not mid-pipeline

Port the Prime Job Runner's 60% token-overlap gate into the normalizer, checked against existing GigaClaw tickets.

**Rationale.** Rejecting before a ticket exists is cheaper than skipping after. `CreateTicketActionSpec.SkipIfExists` remains as a title-exact backstop but is too weak alone.

### AD-7: The ticket body is the draft

The agent writes its finished draft into the ticket **description**. That is the single canonical location the reviewer agent reads and the egress `BodyTemplate` sends.

**Rationale.** It keeps the draft where the work is visible — reviewable in the board UI, diffable through ticket history, and reachable by both the reviewer agent and the CMS dispatch without a second fetch. Comments stay reserved for critique, receipts, and audit, so the "latest draft" is never ambiguous.

**Implication.** The description carries markdown body plus a frontmatter-style header for `title`, `slug`, `excerpt`, `contentType`, and `seo` — mirroring the vault-note convention the ZabsAIOS agents already produce, so agent prompts port with minimal change. Egress parses that header rather than guessing.

**Archival is additive, not alternative.** Record-keeping links out to Obsidian and/or Google Drive are a planned follow-on: an extra action appended to the egress chain that writes the finished draft to the archive and drops the resulting link back as a ticket comment. Nothing in Phases 1–4 may assume the ticket body is the *only* copy, but nothing should block on the archive existing either. Tracked as Task 16.

### AD-8: Images are progressively enhanced, never blocking

Every draft ships with a Pexels image. The writer agent additionally emits a **recommended generation prompt** into the ticket frontmatter. A separate, opportunistic pass upgrades the Post with a locally generated image via OpenMontage when the local stack is actually available.

**Rationale.** Local generation needs a GPU and a running ComfyUI server; the pipeline must not stall when that isn't there. Splitting into "always works" (Pexels), "free and portable" (a text prompt), and "best effort" (local generation) means the critical path never depends on hardware.

**Availability is probed, not inferred.** OpenMontage's ComfyUI backend exposes `GET /system_stats`. Per the topology above, the generator lives on the **Mac** and the sweep runs on the **server**, so `COMFYUI_SERVER_URL` points at the Mac's Tailscale address — not `localhost`. The sweep probes with a short timeout; up means generate, anything else means skip and leave the ticket labelled for a later pass.

The probe has to survive three distinct "unavailable" states, all of which look different on the wire: the Mac is powered off or asleep (connection refused / no route), Tailscale is down or the ACL blocks the port (timeout), and the Mac is up but ComfyUI isn't running (connection refused on 8188 specifically). None of these are errors worth alerting on — they're the normal resting state — so all three collapse to the same clean no-op.

This is also why AD-8 exists as a separate sweep rather than a step inside the egress chain. Publication happens on the 24/7 server; image quality improves whenever the Mac happens to be reachable. Coupling them would make publishing depend on a laptop being awake.

**Implication for the CMS.** Because the image improves *after* the Post exists, `/api/ai/draft` can no longer be create-only — Task 2 must also expose an authenticated update path keyed on `sourceTicket`. Generation is async (`POST /prompt` → poll `GET /history/{id}` → fetch `GET /view`), so the upgrade pass owns polling; it cannot be a single fire-and-forget action.

### AD-9: Drop T1/T2; model choice is a property of the agent

Retire the tier abstraction. Each agent member gets a `DefaultModel` appropriate to its job, and GigaClaw's existing three-level resolution handles the rest.

**Rationale.** T1/T2 existed in ZabsAIOS because n8n has no concept of a model — routing between Gemini Flash and Claude required Switch nodes and duplicate HTTP branches, so the tier label was the only place the decision could live. GigaClaw already resolves models natively at three levels, most specific first:

1. `RunAgentActionSpec.Model` — per-action override for one automation
2. member `DefaultModel` (`UpdateMemberRequest`) — the agent's normal model
3. project `FallbackModel` (`UpdateProjectRequest`) — the venture-wide default

A `T1`/`T2` label on top of that is indirection with nothing underneath. Set the model on the agent, override only where an automation genuinely differs.

**Proposed assignment**, to be confirmed when Task 10 lands:

| Model | Agents | Why |
|---|---|---|
| Haiku | `committer`, `groomer`, `documentalist`, trend triage | Mechanical, high-volume, low-judgment |
| Sonnet | `blog-researcher`, `growth-writer`, `qa-tester`, most writing | The bulk of the work |
| Opus | `blog-reviewer`, `decision-engine`, `approval-gatekeeper`, `evaluator` | Judgment gates the pipeline |

The reviewer-on-Opus / writer-on-Sonnet split is deliberate and economically favourable: the reviewer runs at the same frequency as the writer but emits far shorter output, so buying better judgment there is cheap — while the reviewer's verdict is what decides whether a weak draft reaches the CMS. Spending on the short critique rather than the long draft is the better trade.

Local Ollama models remain available through the same field for anything that shouldn't leave the machine.

## Dependency graph

```
Task 1 (Payload schema)
   └── Task 2 (/api/ai/draft v2 contract)
          └── Task 3 (httpRequest ActionSpec)          ← highest risk, done early
                 └── Task 4 (egress automation, 1 venture)
                        └── Task 5 (receipt writeback)
                               └── CHECKPOINT A: one ticket reaches the CMS

Task 6 (project scaffold script)  ── independent, parallel with 1-5
   └── Task 7 (ingress normalizer)
          └── Task 8 (dedup gate)
                 └── Task 9 (Telegram + schedule capture)
                        └── CHECKPOINT B: an idea becomes a ticket

Tasks 4,9 ─┴─→ Task 10 (agent write/review automations)
                 └── Task 11 (trend cron, config-driven)
                        └── CHECKPOINT C: unattended end-to-end

Task 11b (Pexels)  ── deps Task 4, lands with Phase 3
   └── Task 18 (OpenMontage upgrade sweep)   ← needs Task 2's update path

Task 12 (template sync) ─┐
Task 13 (Discord)        ─┤
Task 14 (Telegram alerts)─┼─→ Task 15 (Hyperlane migration)
Task 16 (draft archival) ─┤        └── Task 17 (karalungaming, last)
Task 18 (image upgrade)  ─┘               └── CHECKPOINT D: single CMS
```

Implementation order is bottom-up on this graph, with the CMS contract first because everything downstream encodes it.

---

## Phase 1: Egress path — one venture, end to end

Highest-risk work first. If the `httpRequest` action can't cleanly capture and template a response, the whole design changes, so we find out in Task 3 rather than Task 11.

### Task 1: Extend the Payload Posts schema for ticket provenance

**Description.** Add the fields the pipeline needs to trace a Post back to its originating GigaClaw ticket, and to carry the content-type discriminator. Builds on the existing `ventureSlug` denormalization rather than replacing it.

**Acceptance criteria:**
- [ ] `Posts` gains indexed `sourceTicket` (text, nullable, format `{projectSlug}#{ticketId}`) and `sourceSystem` (select: `gigaclaw` | `manual` | `n8n-legacy`, default `manual`)
- [ ] `personal-brand` and `hyperlanetravels` are added to `VENTURE_SLUGS` in `lib/ai-draft.mjs`; `REJECTED_VENTURE_SLUGS` is emptied and the rejection branch removed
- [ ] Corresponding `Ventures` records exist for all six slugs
- [ ] Migration runs clean against a copy of production data

**Verification:**
- [ ] `pnpm payload migrate` succeeds; `pnpm build` clean
- [ ] Manual: create a Post in `/admin`, confirm `ventureSlug` still auto-resolves and new fields persist
- [ ] `GET /api/posts?where[ventureSlug][equals]=gamelifteat` returns anonymously

**Dependencies:** None
**Files:** `ZabalaZone/payload/collections/Posts.mjs`, `ZabalaZone/lib/ai-draft.mjs`, `ZabalaZone/migrations/*`
**Scope:** S

---

### Task 2: Version the `/api/ai/draft` contract

**Description.** Accept `sourceTicket`, `sourceSystem`, and `contentType` on intake while remaining backward-compatible with the current Prime Job Runner body, so the legacy workflow keeps working during transition.

**Acceptance criteria:**
- [ ] Accepts `contentType` directly; continues to accept Hyperlane-style `category` via the existing `CONTENT_TYPE_ALIASES` map
- [ ] `sourceTicket` / `sourceSystem` are validated and persisted
- [ ] Response includes `ventureSlug` and `contentType` alongside existing `{ok, id, slug, adminUrl}`
- [ ] A request with an unknown `venture` still returns 400 with the actionable message naming valid slugs
- [ ] **Update path** (`PATCH`, or `POST` with upsert semantics) keyed on `sourceTicket`, so a Post can be revised after creation — required by AD-8's image upgrade
- [ ] The update path accepts a partial body (image fields alone) without requiring the full draft, and refuses to update a Post already `published` unless explicitly forced

**Verification:**
- [ ] Unit tests cover: valid GigaClaw body, legacy Prime Job Runner body, unknown venture, missing venture
- [ ] Update test: create a Post, then PATCH only its hero image; body and slug unchanged
- [ ] Update against an unknown `sourceTicket` returns 404, creates nothing
- [ ] `curl` with a legacy body still 201s

**Dependencies:** Task 1
**Files:** `ZabalaZone/app/api/ai/draft/route.ts`, `ZabalaZone/lib/ai-draft.mjs`, `ZabalaZone/tests/*`
**Scope:** M

---

### Task 3: Add the `httpRequest` action type to GigaClaw core

**Description.** New `ActionSpec` with typed request config and response capture. This is the riskiest task and the one with the least precedent in the codebase — build it before anything depends on it.

**Acceptance criteria:**
- [ ] `HttpRequestActionSpec` registered via `[JsonDerivedType(..., "httpRequest")]` with: `Url`, `Method`, `Headers`, `BodyTemplate`, `TimeoutSeconds`, `AbortOnFailure`, `SecretRef`
- [ ] Templating resolves ticket context (`{ticketId}`, `{title}`, `{description}`, `{projectSlug}`, `{labels}`) consistent with how `CreateTicketActionSpec` handles `{date}`
- [ ] Response status + parsed JSON body are exposed to later actions in the same chain
- [ ] Secrets are referenced by name from config, never inlined in `automations.json`
- [ ] Non-2xx honours `AbortOnFailure`; timeout is enforced and logged
- [ ] Renders and round-trips in the automations editor UI

**Verification:**
- [ ] Unit tests: success, 4xx, 5xx, timeout, malformed JSON response, template substitution
- [ ] `dotnet test` green; `dotnet build` clean
- [ ] Manual: automation posting to a local echo server shows the response in the run log

**Dependencies:** None (contract from Task 2 informs it but doesn't block)
**Files:** `GigaClaw.Core/Automation/AutomationConfig.cs`, `ActionExecutor.cs`, `GigaClaw.Web/Components/` (editor), `GigaClaw.Core.Tests/Automation/`
**Scope:** M

> Risk note: if response capture proves invasive to `ActionExecutor`'s chain model, fall back to fire-and-forget plus an n8n-side confirmation poll, and revisit AD-2. Decide this within Task 3, not later.

---

### Task 4: Egress automation on one venture

**Description.** Wire `gamelifteat` so a ticket reaching `Done` with a committer receipt posts to the CMS. Port the Dispatcher's `Verify receipt` gate logic into automation conditions.

**Acceptance criteria:**
- [ ] Automation triggers on `statusChange` to `Done`, conditioned on label `ready-for-cms` present and `dispatched` absent
- [ ] Approval gate preserved: if any of `outbound`/`publish`/`external`/`deploy` are present, `approved` is required
- [ ] Body sends `venture: "gamelifteat"` derived from project slug — never hardcoded per automation
- [ ] `BodyTemplate` reads the draft from the **ticket description** per AD-7, parsing the frontmatter header for `title`, `slug`, `excerpt`, `contentType`, `seo` and treating the remainder as markdown body
- [ ] A description that fails to parse blocks the dispatch with a readable error rather than posting a malformed draft
- [ ] `dispatched` label applied only **after** a 2xx; a failed POST leaves the ticket undispatched and retryable

**Verification:**
- [ ] Manual: ticket → `Done` → Post appears in `/admin` with correct `ventureSlug`
- [ ] Negative: stop the CMS, move a ticket to `Done`, confirm no `dispatched` label and a failure comment
- [ ] Negative: ticket labelled `publish` without `approved` does not dispatch

**Dependencies:** Tasks 2, 3
**Files:** `.agents/automations.json` (gamelifteat project), `ProjectTemplate/Agents/automations.json`
**Scope:** S

> This fixes the ordering bug in the current Dispatcher, where the disabled Payload node sits upstream of `Get labels`, so tickets get marked `dispatched` without anything being sent.

---

### Task 5: Receipt writeback

**Description.** Write the CMS response back onto the ticket so the board is the audit trail.

**Acceptance criteria:**
- [ ] On success, an `addComment` action posts author `automation` with CMS id, slug, and `adminUrl`
- [ ] On failure, a comment records status code and error detail, and the ticket moves to a `Blocked` column
- [ ] Comment format is stable enough to parse (mirrors the `Committed…` receipt convention)

**Verification:**
- [ ] Manual: success and failure paths both produce the expected comment
- [ ] Blocked ticket is visible on the board without opening the run log

**Dependencies:** Task 4
**Files:** `.agents/automations.json`
**Scope:** XS

---

### CHECKPOINT A — egress proven

- [ ] One ticket, moved by hand to `Done`, creates a correctly-tagged Post in ZabalaZone
- [ ] `adminUrl` is on the ticket as a comment
- [ ] Failure path is visible on the board, not just in logs
- [ ] `dotnet test` and `pnpm test` green
- [ ] **Review with Pedro before proceeding** — confirm the action-type design before it hardens

---

## Phase 2: Ingress path

### Task 6: Venture project scaffolding

**Description.** A script that creates a GigaClaw project from `ProjectTemplate` with venture-specific `BRAND.md` / `VOICE.md`, so adding a venture is a command rather than a copy-paste session.

**Acceptance criteria:**
- [ ] `scripts/new-venture.{sh,ps1} <slug>` creates the project, copies the template, seeds required labels (`ready-for-cms`, `dispatched`, `approved`, `blocked`)
- [ ] Fails loudly if the slug is not in the canonical venture list
- [ ] Idempotent — re-running against an existing project reports and exits non-destructively

**Verification:**
- [ ] Run for `gamepowergym`; board appears with template agents and labels
- [ ] Re-run; no duplicates created

**Dependencies:** None
**Files:** `tools/` or `scripts/`, `ProjectTemplate/`
**Scope:** S

---

### Task 7: n8n ingress normalizer subworkflow

**Description.** One subworkflow that all sources call. Owns validation, venture resolution, label-id resolution, and the GigaClaw POST. Replaces the current Backlog Item Creator.

**Acceptance criteria:**
- [ ] Accepts `{source, venture, title, description, priority?, assignedTo?, labels?}`
- [ ] Rejects unknown ventures with a clear error rather than defaulting
- [ ] Resolves label **names → ids** via `GET /api/projects/{slug}/labels` before POSTing, because `CreateTicketRequest` takes `LabelIds` as ints
- [ ] Creates tickets in `Backlog` with `createdBy` reflecting the true source
- [ ] Returns `{ok, ticketId, url}` to the caller

**Verification:**
- [ ] Manual execution for each of two ventures produces tickets on the right boards
- [ ] Unknown venture returns a 4xx-shaped error, creates nothing

**Dependencies:** Task 6
**Files:** new n8n workflow `GigaClaw Ingress Normalizer`
**Scope:** M

---

### Task 8: Dedup gate at ingress

**Description.** Port the Prime Job Runner's `Dedup gate` — 60% token overlap on words longer than 3 chars — to run against existing GigaClaw tickets before creation.

**Acceptance criteria:**
- [ ] Queries open tickets for the target project and compares normalized title + description tokens
- [ ] ≥60% overlap returns `{ok:false, reason:'duplicate', duplicateOf:<ticketId>}` and creates nothing
- [ ] Threshold is a workflow-level constant, not buried in an expression
- [ ] Closed/archived tickets are excluded from comparison

**Verification:**
- [ ] Submitting the same idea twice creates exactly one ticket
- [ ] Two genuinely different ideas sharing a keyword both create tickets

**Dependencies:** Task 7
**Files:** `GigaClaw Ingress Normalizer`
**Scope:** S

---

### Task 9: Telegram and schedule capture

**Description.** Two thin trigger workflows calling the normalizer. Reuse the working ZabsAIOS Telegram whitelist gate and command router.

**Acceptance criteria:**
- [ ] Telegram: whitelist gate preserved; a `/idea <venture> <text>` command creates a ticket and acknowledges with the ticket URL
- [ ] Unknown or unauthorized sender is silently dropped, consistent with current behaviour
- [ ] Schedule: cron trigger enqueues from a configured source, calling the same normalizer
- [ ] Neither workflow contains venture-specific logic beyond parsing the argument

**Verification:**
- [ ] Live Telegram message creates a ticket and returns a link
- [ ] Non-whitelisted sender produces nothing
- [ ] Scheduled run creates the expected ticket

**Dependencies:** Task 7
**Files:** new n8n workflows (Telegram capture, schedule capture)
**Scope:** M

---

### CHECKPOINT B — ingress proven

- [ ] A Telegram message becomes a ticket on the correct venture board
- [ ] Duplicate submissions are rejected
- [ ] Adding a second source required no change to the normalizer
- [ ] **Review with Pedro** — confirm the source→normalizer split before adding Discord

---

## Phase 3: The board as quality gate

### Task 10: Writer and reviewer automations

**Description.** Express draft → critique → revise as board transitions using `runAgent`. Replaces the Prime Job Runner's model-tier Switch nodes and hand-rolled retry loop.

**Acceptance criteria:**
- [ ] `Todo → InProgress` dispatches the writer agent (`blog-researcher` or `growth-writer`); per AD-9 the model comes from the member's `DefaultModel`, with `RunAgentActionSpec.Model` left unset unless an automation genuinely differs
- [ ] Agent members are seeded with the AD-9 model assignment; the project `FallbackModel` is set so an unconfigured agent still runs
- [ ] The writer emits `imagePrompt` into the ticket frontmatter per AD-8 — a generation prompt for later local rendering, written regardless of whether any generator is available
- [ ] `InProgress → Review` dispatches `blog-reviewer`; a pass moves to `Done` and applies `ready-for-cms`, a fail returns to `InProgress` with critique as a comment
- [ ] `MaxConsecutiveFirings` caps revision loops; `ExhaustedStatus` parks the ticket in `Blocked` with `ExhaustedComment` explaining why
- [ ] Per AD-7, the writer agent writes the draft to the **ticket description** in the frontmatter-plus-markdown shape Task 4 parses; the reviewer reads from there and posts critique as a **comment**, never overwriting the body
- [ ] A revision replaces the description wholesale, so the body is always the current draft and ticket history carries the previous versions

**Verification:**
- [ ] A `Todo` ticket runs unattended to `Done` with `ready-for-cms`
- [ ] A deliberately weak draft loops and parks in `Blocked` rather than looping forever
- [ ] Round-trip contract test: a description written by the writer parses cleanly in Task 4's template
- [ ] Run costs appear in the existing cost tracker

**Dependencies:** Tasks 4, 9
**Files:** `.agents/automations.json`, `ProjectTemplate/Agents/automations.json`, agent `SKILL.md` files
**Scope:** M

---

### Task 11: Config-driven trend cron

**Description.** Port the *Basic* night-research shape — read the venture list, loop, per-venture profile with fallback — and target GigaClaw tickets instead of vault notes. Explicitly not the Trend Jacking shape.

**Acceptance criteria:**
- [ ] Venture list is read from one config source; adding a venture requires no new nodes
- [ ] Per-venture trend profile with a `fallbackProfiles`-style default so a missing profile degrades rather than fails
- [ ] Surfaced trends enter via the Task 7 normalizer, so dedup applies automatically
- [ ] Single-flight: a run that overlaps the previous one exits cleanly

**Verification:**
- [ ] Adding a seventh venture to config produces tickets with zero node edits
- [ ] Removing a venture's profile still yields a run using fallback
- [ ] Two overlapping manual triggers do not double-create

**Dependencies:** Tasks 8, 10
**Files:** new n8n workflow `GigaClaw Trend Intake`
**Scope:** M

---

### Task 11b: Pexels first-pass image

**Description.** Every draft gets a stock image before dispatch, so no Post ever ships imageless. Ported from the Prime Job Runner's `Pexels search` + `Inline images` nodes.

**Acceptance criteria:**
- [ ] Searches Pexels on the draft's primary SEO keyword and attaches the result as the Post's hero image
- [ ] A Pexels failure or empty result does **not** block dispatch — the Post is created without an image and the ticket is labelled `needs-image`
- [ ] Attribution is preserved wherever Pexels' licence requires it
- [ ] The ticket records which image was chosen, so the AD-8 upgrade pass knows what it is replacing

**Verification:**
- [ ] A dispatched Post has a hero image sourced from the draft's keyword
- [ ] With the Pexels key removed, dispatch still succeeds and the ticket is labelled `needs-image`

**Dependencies:** Task 4
**Files:** egress automation or n8n egress helper
**Scope:** S

---

### CHECKPOINT C — unattended end-to-end

- [ ] Trend cron → ticket → agent writes → agent reviews → CMS Post, with no human step
- [ ] Costs tracked; failures parked visibly on the board
- [ ] **Review with Pedro** — this is the first full loop; confirm before scaling to all ventures

---

## Phase 4: Scale and harden

### Task 12: Template drift check

**Description.** Guard AD-4's known risk. A check that reports where per-project `automations.json` diverges from `ProjectTemplate`.

**Acceptance criteria:**
- [ ] Reports automations present in template but missing from a project, and vice versa
- [ ] Distinguishes intentional per-venture overrides (allowlist) from drift
- [ ] Runs as a script and is documented; CI wiring optional

**Verification:**
- [ ] Deliberately delete an automation from one project; the check flags it
- [ ] An allowlisted override does not flag

**Dependencies:** Task 10
**Files:** `tools/`, `docs/`
**Scope:** S

---

### Task 13: Discord capture

**Description.** Third source, proving AD-5. Should be near-trivial if the normalizer is right.

**Acceptance criteria:**
- [ ] Discord message in a designated channel creates a ticket via the normalizer
- [ ] Author allowlist enforced
- [ ] **No changes required to the normalizer** — if changes are needed, that is a finding to record

**Verification:**
- [ ] Live message creates a ticket
- [ ] Normalizer diff is empty

**Dependencies:** Task 7
**Files:** new n8n workflow
**Scope:** S

---

### Task 14: Operational alerting

**Description.** Telegram notifications for pipeline health, ported from the Prime Job Runner's alert nodes.

**Acceptance criteria:**
- [ ] Alerts on: CMS dispatch failure, ticket parked in `Blocked`, revision cap exhausted
- [ ] Each alert links to the ticket
- [ ] Rate-limited so one bad run cannot flood the channel

**Verification:**
- [ ] Force each condition; confirm one alert each with a working link

**Dependencies:** Task 5
**Files:** n8n workflow or GigaClaw automation
**Scope:** S

---

### Task 15: HyperlaneTravels CMS migration

**Description.** Execute AD-1. Move Hyperlane content into ZabalaZone Payload; Hyperlane becomes venture number six.

**Acceptance criteria:**
- [ ] All Hyperlane Posts migrated with `ventureSlug: 'hyperlanetravels'`, slugs and media intact
- [ ] Slug collisions resolved via the existing per-venture suffix logic
- [ ] Hyperlane frontend reads from the consolidated CMS
- [ ] Old instance set read-only, not deleted
- [ ] Legacy Prime Job Runner's `CMS_DRAFT_URL` repointed or the workflow retired

**Verification:**
- [ ] Post counts match pre/post migration
- [ ] Spot-check 10 posts for body, media, SEO fidelity
- [ ] Hyperlane site renders correctly against the new source
- [ ] Rollback rehearsed on staging before production

**Dependencies:** Tasks 1, 2, and CHECKPOINT C
**Files:** `ZabalaZone/scripts/`, `HyperlaneTravels/`, migrations
**Scope:** L — **break down further before starting**

---

### Task 16: Draft archival to Obsidian / Google Drive

**Description.** Complete AD-7's archival half. Append an action to the egress chain that writes the finished draft to a durable record-keeping store and links it back onto the ticket.

**Acceptance criteria:**
- [ ] On successful CMS dispatch, the draft is written to the chosen archive under a venture-scoped path
- [ ] The resulting link is added to the ticket as a comment alongside the CMS `adminUrl`
- [ ] Archive failure does **not** fail the dispatch — the Post is already live; a failed archive warns and retries independently
- [ ] Backfill path exists for tickets dispatched before this task landed

**Verification:**
- [ ] Dispatch a ticket; confirm archive entry and link comment
- [ ] Break the archive credential; confirm the CMS dispatch still succeeds and a warning is raised
- [ ] Backfill a handful of earlier tickets

**Dependencies:** Task 5
**Files:** `.agents/automations.json`, or an n8n workflow if the archive needs OAuth
**Scope:** M

> Target choice (Obsidian vault vs Google Drive vs both) is deliberately deferred. Obsidian keeps continuity with the ZabsAIOS vault and is trivially git-backed; Drive is easier to share with non-technical collaborators. Decide when the task is picked up — nothing before it depends on the answer.

---

### Task 17: `karalungaming` rollout

**Description.** Bring the trailing venture online. Deliberately last — it exists in the schema from Task 1, but nothing else waits on it.

**Acceptance criteria:**
- [ ] Project scaffolded via Task 6 with venture-specific `BRAND.md` / `VOICE.md`
- [ ] `Ventures` record exists; ingress accepts the slug
- [ ] Full loop verified once end to end: ingress → write → review → CMS
- [ ] Template drift check (Task 12) passes for the new project

**Verification:**
- [ ] One ticket completes the full pipeline
- [ ] No changes were required to the normalizer or egress automation — if any were, record why

**Dependencies:** Task 12
**Files:** project config, `Ventures` record
**Scope:** S

> If this slips past Checkpoint D, sign off anyway. It is explicitly the venture allowed to trail.

---

### Task 18: OpenMontage image upgrade sweep

**Description.** Complete AD-8. An opportunistic pass that finds Posts still carrying a stock image, renders a better one locally from the writer's `imagePrompt`, and updates the Post — only when ComfyUI is actually up.

**Acceptance criteria:**
- [ ] `COMFYUI_SERVER_URL` points at the Mac's **Tailscale** address; no `localhost` assumption anywhere in the workflow
- [ ] Probes `GET {COMFYUI_SERVER_URL}/system_stats` with a short timeout (≤5s); connection refused, no route, and timeout all exit cleanly with no error state and no alert
- [ ] Selects tickets labelled `image-upgrade-pending` with a non-empty `imagePrompt`
- [ ] Submits via `POST /prompt`, polls `GET /history/{prompt_id}`, retrieves the artifact from `GET /view`
- [ ] Uploads the result and updates the Post through Task 2's update path, keyed on `sourceTicket`
- [ ] Clears the label and comments the new image URL onto the ticket on success
- [ ] Bounded: a per-run cap on renders, and a per-render timeout that leaves the label in place for the next sweep rather than hanging
- [ ] **Tolerates the Mac vanishing mid-render** — Tailscale dropping between submit and poll leaves the ticket labelled and the Post untouched, with no partial write
- [ ] Records the reproducibility set — prompt, seed, dimensions, workflow hash, model stack — on the ticket
- [ ] Runs on a schedule **and** on demand, so it can be kicked manually after waking the Mac

**Verification:**
- [ ] Mac powered off: sweep exits cleanly, changes nothing, raises no alert
- [ ] Mac up, ComfyUI not running: same clean no-op, distinguishable in logs from the above
- [ ] Mac up, ComfyUI up: a pending ticket's Post gains a generated image and the label clears
- [ ] Disconnect Tailscale mid-render: label stays set, Post unchanged, next sweep retries
- [ ] A ticket with no `imagePrompt` is skipped, not failed
- [ ] Backlog of `image-upgrade-pending` tickets is visible on the board and drains across sweeps

**Dependencies:** Tasks 2, 11b
**Files:** new n8n workflow `OpenMontage Image Upgrade`, `.agents/automations.json`
**Scope:** M

> n8n owns this rather than a GigaClaw automation because it is long-running, polls, crosses the tailnet, and needs bounded retries — all things n8n does natively and `ActionExecutor` does not. It also keeps the Mac dependency out of GigaClaw's automation config entirely, so the 24/7 board has no knowledge of a machine that isn't always there.

---

### CHECKPOINT D — complete

- [ ] All six ventures dispatch to one CMS (`karalungaming` last; may trail without blocking sign-off)
- [ ] Three ingress sources live
- [ ] Drafts archived and linked per AD-7
- [ ] Template drift check green
- [ ] Legacy Prime Job Runner retired or explicitly kept with a documented reason
- [ ] Docs updated: `doc/` in GigaClaw, README in n8n export

---

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Response capture doesn't fit `ActionExecutor`'s chain model | High — invalidates AD-2 | Task 3 is early and explicitly gated; fallback to fire-and-forget + confirmation poll |
| GigaClaw API is unauthenticated on a reachable interface | High if the host is ever exposed | Out of scope per decision, but **bind to the Docker bridge and tailnet only**, never a public NIC; record as a known gap in `KNOWN_ISSUES` |
| ComfyUI is unauthenticated and reachable over tailnet | Medium | Bind to the Tailscale interface only; rely on Tailscale ACLs to scope which nodes may reach port 8188 |
| Mac rarely online, so image upgrades never happen | Low — cosmetic only | Pexels already shipped; sweep is on-demand as well as scheduled; pending backlog visible on the board and drains whenever the Mac appears |
| `automations.json` drift across six projects | Medium — recreates the Trend Jacking problem | Task 12 |
| Agent output shape doesn't match the CMS body template | Medium — silent bad drafts | Contract test in Task 10; `sentinel`-style validation before dispatch |
| Hyperlane migration loses media or breaks slugs | High, hard to reverse | Task 15 gated behind CHECKPOINT C; staging rehearsal; old instance retained read-only |
| Divergence from KittyClaw makes upstream merges painful | Medium, accepted | Deliberate per project direction; keep new action types in clearly separated files |
| Dedup threshold too aggressive, drops good ideas | Low | Threshold as a named constant; log near-misses for tuning |
| Post updated after publication surprises a reader | Low | Task 2's update path refuses `published` Posts unless forced |
| Per-agent model assignment proves too expensive | Medium | AD-9 assignment is reviewed against the existing cost tracker at Checkpoint C; changing it is a config edit, not code |

## Open questions

None blocking. Remaining choices are deliberately deferred to the task that needs them:

- Obsidian vs Google Drive vs both as the archival target (Task 16)
- Confirming the AD-9 per-agent model assignment against real cost data once Phase 3 has run (Task 10)
- Which OpenMontage workflow and model stack the upgrade sweep should default to (Task 18)

### Resolved

- **Where does the draft live?** → Ticket description, per AD-7. Archival links to Obsidian / Google Drive follow as Task 16.
- **Does `personal-brand` get a CMS presence?** → Yes. It needs a `Ventures` record and a `VENTURE_SLUGS` entry in Task 1 like any other venture, and a project via Task 6.
- **Is `karalungaming` in scope?** → Yes, at lowest priority. Included in the Task 1 schema work so nothing has to change later, but it is the **last** venture to get a project scaffolded and automations enabled. Treat it as the trailing venture in Phase 4 — if it slips, nothing else does.
- **Image sourcing?** → Progressive, per AD-8. Pexels always (Task 11b), writer emits a generation prompt, OpenMontage upgrades opportunistically when ComfyUI is live (Task 18).
- **T1/T2 tiers?** → Retired, per AD-9. Model is a property of the agent member, resolved through GigaClaw's existing action → member → project chain.

### The canonical venture list

Six ventures, in rollout order: `gamelifteat` (pilot), `gamepowergym`, `zabsconsulting`, `personal-brand`, `hyperlanetravels` (blocked on the Task 15 migration), `karalungaming` (last).

## Explicitly out of scope

- GigaClaw API authentication (decided; recorded as a known gap)
- Rebuilding the Prime Job Runner's 47-node graph
- Trend Jacking's hardcoded per-venture lanes
- Migrating the ZabsAIOS Obsidian vault job queue — GigaClaw replaces it

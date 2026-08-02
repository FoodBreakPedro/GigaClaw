# GigaClaw client demo script

Rehearsable runbook for a live client demo of GigaClaw's four agentic pipelines
(code, content, media, ops) closing on Mission Control. Uses **real data** — the
demo project's actual board, actual `claude` CLI dispatches, actual cost/verdict
receipts. No canned output, no mock CLI. Never run this against `:5232`
(debug instance, mock CLI) — that produces fake NDJSON, not a real demo.

Grounded against the working tree as of 2026-08-02 (branch
`fix/backlog-template-tickets`). Where the demo depends on `.agents/automations.json`
counts, marker strings, or UI copy, those were read directly from the repo, not
inferred from the plan.

---

## 1. Prep checklist (30 min before)

**Project**: use the GigaClaw repo itself as dogfood (self-hosting is the strongest
story — "we ran this on the tool that built it") unless the client has a repo already
registered. Either way, use a project the client hasn't seen tickets on yet, or one
with an obviously-labeled demo lane, so the board doesn't show unrelated real work.

**Must already exist before you start the clock:**
- A handful of already-groomed tickets sitting in Todo/InProgress/Review across at
  least two pipelines, so the board doesn't look empty during setup talk.
- Budgets confirmed. There is **no Settings-page UI for this today** — both caps
  are top-level fields in the project's `.agents/automations.json` (sibling to
  the `"automations"` array). New projects ship `"maxTicketCostUsd": 25.00` by
  default; `dailyBudgetUsd` is opt-in (e.g. `"dailyBudgetUsd": 25`). For the
  demo, lower the per-ticket cap on the sacrificial ticket's project (e.g.
  `"maxTicketCostUsd": 3`) so the cost-cap showcase is reachable. No restart
  needed — it's read per dispatch.
- **At least one midnight UTC crossed with the project active**, so
  `ticket_stat_snapshots` has a prior-day row. Without it, Mission Control's KPI
  deltas read "no baseline yet" and the velocity card is stuck on "Collecting
  daily snapshots since `<date>`" instead of real numbers — see Act 5. This is a
  hard prerequisite, not a nice-to-have, if you want Act 5 to look alive.
- `graphify update .` run recently if you're going to show an agent using it
  (Phase 5.1 — conditional mention only, not required for the demo to work).

**Instance health checks (do these last, right before walking in):**
- `dotnet watch --non-interactive` is running under `GigaClaw.Web`, serving
  **`http://localhost:5230`** — the live instance. Do not touch `:5232`; another
  session may be using it for its own verification, and `:5230` is the only
  instance with a real `claude` CLI wired up.
- `GET http://localhost:5230/api/docs` returns 200.
- Board shows no automation mid-run on a ticket you plan to touch (an in-flight
  `InProgress` on your demo ticket races your script).
- `.agents/channel/cost-log*.jsonl` isn't stale, or the cost numbers in Act 5
  will look wrong.

**Tabs to have open:** the board (`/board/<slug>`); a terminal with `curl` ready
for Act 1 and the FIX-round staging in Act 2; `/api/docs` (or `openapi/v1.json`)
in case a client asks "what's the actual contract"; `/board/<slug>/automations`
as a backup to show the automation list itself (66 automations as of this
write-up — re-check before quoting, the template evolves); `/mission` for the
Act 5 closer.

---

## 2. Act 1 — Intake (2 min)

Talking point up front: **this is the Telegram/n8n path.** There is no Telegram
integration in this repo — it's external n8n hitting this exact endpoint. Anything
that can POST JSON can feed the board.

```bash
curl -s -X POST http://localhost:5230/api/projects/<slug>/tickets \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Add a dark-mode toggle to the settings page",
    "description": "Customer request from support — no design attached yet.",
    "createdBy": "demo-intake",
    "status": "Backlog"
  }'
```

Note what's **absent**: no `assignedTo`. That's the trigger. Watch the board —
within 30 seconds the `backlog-intake` automation (`id: backlog-intake` in
`automations.json`) fires:

- It's a `ticketInColumn` trigger scoped to `Backlog` with `"unassigned": true` —
  it only touches tickets nobody claimed.
- Action: `assignTicket groomer`, plus a system comment explaining why.
- That reassignment is itself the trigger for the existing `groomer` automation
  (`ticketInColumn Backlog` + `assigneeSlug: groomer`), which dispatches the
  groomer agent.

Watch the groomer's pass live: title gets rewritten, description gets structured,
priority/labels get set, and it re-routes the ticket to the right agent (e.g.
`programmer`, `Todo`) — the groomer **never leaves itself as assignee**. Point out
the machine marker it leaves on the ticket comment,
`[groomer:v1 input-sha256=... output-sha256=... result=complete]` — that's how it
knows not to re-groom an unchanged ticket on the next poll.

If intake stalls 3 times running, the trigger's own `exhaustedStatus: Blocked`
fires with a comment naming the likely cause (no `groomer` member configured) —
mention only if asked "what if it fails"; don't demo it here, that's Act 7.

---

## 3. Act 2 — Code pipeline (5 min)

Pick up the ticket from Act 1 once it's `Todo`, assigned `programmer` (or use a
pre-groomed one if intake ran slow). If the ticket needs decomposition, `producer`
handles that first — the `producer-on-subtick` automation re-dispatches producer
whenever a sub-ticket's status changes, so multi-file work fans out into child
tickets before any code is touched. For a single-file demo change, skip straight to
programmer.

**Dispatch.** `Todo → InProgress` fires `assignee-dispatch-code` — note in the
automation JSON this is the **worktree-isolated** twin of the general dispatch
automation (`"isolation": "worktree"` on the `runAgent` action, tagged **SP-3**:
"code-touching contracts run isolated"). Talking point: the programmer never
touches the main checkout directly.

**Live run drawer.** Click the running-agent indicator on the ticket card — this
opens `AgentRunDrawer`, streaming the actual `claude` CLI turns as they happen.
This is the same drawer reachable from run history (header **Runs** button,
🕑 icon) if you want to replay a past run instead of watching live.

**QA verdict receipt.** When the ticket reaches `Review`, `qa-tester` posts a
comment carrying:

```
GIGACLAW-VERDICT v1 qa-tester SHIP artifact-sha256:<64-hex>

```json
{ "schemaVersion": 1, "agent": "qa-tester", "ticketId": ..., "verdict": "SHIP", ... }
```
```

Anatomy to call out on screen:
- **Marker line first, fenced JSON second** — marker and body must agree on
  `agent`/`verdict`/`inputDigest`, or the comment is rejected as `INVALID`.
- **`inputDigest`** binds the verdict to the exact bytes reviewed — this is the
  "stale approval can't be replayed" guarantee. `requireFreshArtifact` re-hashes
  before honoring a SHIP.
- **Fail-closed validation**: `verdict_contract.py` exits `0` only on a valid
  verdict; any other exit code — malformed JSON, digest mismatch, missing file —
  is treated as `BLOCK` by every caller. Nothing defaults to open.

`verdict-gate-qa-ship-to-done` re-validates the verdict itself (not taken on the
reviewer's word), then runs `enqueueMerge` + `moveTicketStatus Done`. The merge
queue (`MergeQueueProcessor`) serializes one merge at a time per project — a
second candidate holds behind the first until `Merged`/`Bounced`, and a live file
lease *holds* a queued merge rather than bouncing it. `committer-on-done` fires
last.

**Scripted FIX round.** Before the demo, or live if you're confident: stage a
deliberate imperfection in the working branch (an obvious lint issue, a missing
test) so `qa-tester` returns `FIX` once. Watch:
- `qa-tester` verdict comment: `FIX`, with `categories[]` below max and/or
  `vetoItems[]` — not prose, a typed reason.
- The repair-round arm (`repairBudget: withinCap`) re-dispatches `programmer` in
  `InProgress` with the outstanding veto items and below-max categories injected
  into its context — literally quote this on screen: "the agent is told what was
  refused instead of re-deriving it from the ticket."
- Round count is visible in the ticket's own comment history — no counter to
  desync. Default cap is **2 rounds** (`contracts.json` `maxReviewCycles`, `2` for
  `qa-tester`/`programmer`); a ticket labeled `extended-repair` gets 4.
- On the second `SHIP`, the episode closes normally. To show exhaustion instead,
  stage two consecutive `FIX`-worthy imperfections — the third gate fires:
  `{verdictHistory}` receipt, `assignTicket groomer`, `moveTicketStatus Backlog`
  — **not Blocked**. Repair exhaustion is a re-scoping problem, routed to the
  groomer with the full verdict history attached and a fresh repair budget.

---

## 4. Act 3 — Content pipeline (3 min)

Use `blog-writer` → `blog-reviewer` → `blog-seo` for this demo. (`content-writer`'s
AD-7 pipeline is fully live now — every `content-*` automation in
`automations.json` ships `enabled: true`, including the AD-7 verdict gates, and
`blog-reviewer`'s AD-7 protocol emits the same typed `GIGACLAW-VERDICT v1`
receipts as everything else. The demo still runs the `blog-writer` path because
it's the more mature, showcase-ready flow, not because AD-7 is missing anything —
say that plainly if asked. **Caution:** a `content-writer`-assigned ticket sitting
in `Todo` will fire the AD-7 pipeline for real — don't create or leave one on the
board by accident mid-demo.)

- `blog-writer` drafts, moves to `Review`.
- `statusChange → Review` (assignee `blog-writer`) dispatches `blog-reviewer`:
  the **5-category, 100-point rubric**, minimum **90/100** to pass, P0/P1 issues
  flagged by severity. Show the verdict comment's `categories[]` breakdown —
  same `GIGACLAW-VERDICT v1` shape as the code pipeline, different rubric.
- `SHIP` → `verdict-gate-blog-ship-to-seo` hands the ticket to `blog-seo` in
  `Todo`. `blog-seo` validates JSON-LD schema and GEO citability
  (`ai_citation_score.py`, gate at 85/100), then moves to `Review` — **still
  assigned to itself**. Its own SKILL says the owner takes it to `Done` by hand
  from there.

**To route this through the Approvals inbox** (so the closer has content in it,
not just media): label the ticket `external` or `publish` during grooming, before
Review. That label — not the pipeline identity — triggers `approval-gate-on-review`,
which dispatches `approval-gatekeeper`. It runs a privacy-guard scan
(`privacy_guard.py`, secret-pattern only) and, on a clean pass, sets
**`pending-approval`** and leaves the ticket in `Review`. It now shows in the
header **Approvals** button (✅, count badge) alongside media. Click it, click
**Approve** → `Done`; `approval-mark-on-done` swaps the label to `approved`.

**Optional CMS dispatch**: only fires on `ready-for-cms` + `approved` tickets, and
the shipped `httpRequest` action still points at the placeholder
`https://zabalazone.example/api/ai/draft` — swap in the client's real CMS
endpoint first, or just narrate the contract (`bodyTemplate` posts
title/body/seo/slug, `failureStatus: Blocked` on a non-2xx).

---

## 5. Act 4 — Media pipeline (3 min)

Post (or use a pre-seeded) approved media spec:

```bash
curl -s -X POST http://localhost:5230/api/projects/<slug>/media/jobs \
  -H "Content-Type: application/json" \
  -d '{ "author": "demo-media", ... spec fields ... }'
```

Returns `202 Accepted` and queues the job (idempotency key replay returns the
existing job with `200`). Point at the **stage progress** fields — `Stage`
(string), `StageIndex`, `StageCount` — new alongside the flat status enum, so a
long compositor run reports where it is without a forbidden sub-ticket. The
artist/compositor skills call:

```
POST /api/projects/<slug>/media/jobs/{id}/stage
{ "stage": "compositing", "stageIndex": 2, "stageCount": 5, "author": "local-media-compositor" }
```

`stageIndex` is monotonic server-side — a replayed or out-of-order report can never
rewind progress already shown on the board. Watch the card tick through stages
(compositor's `in_progress` OpenMontage checkpoints are the live source today).

On `local-media-reviewer` SHIP, `verdict-gate-media-ship-to-owner` sets
`pending-approval` (removes `approved` if stale) and leaves the ticket in
`Review` — **media is never auto-shipped**, only the review is certified by the
machine. Open the **Approvals** inbox, **Approve** → `Done`.

---

## 6. Act 5 — Mission Control closer (3 min)

`/mission` is live: a cross-project, read-from-live-state operations view, per the
approved Option A ("Operations") mockup. Walk top to bottom, in the order the
page actually renders:

- **KPI row** — ticket-status counts (Backlog/Todo/InProgress/Review/Blocked/
  Resolved-today) with vs-yesterday deltas from the daily snapshot table. Time
  Acts 1–4 so at least one attention item is still open when you reach this
  section, then clear it live — the attention queue draining to zero is the
  closing moment.
- **Ticket velocity** — created/resolved/blocked, from `ticket_stat_snapshots`.
  **Deltas need one midnight of snapshot history to mean anything** — without it
  the card says so plainly ("Collecting daily snapshots since `<date>`") instead
  of faking a line. If you skipped the prep-checklist prerequisite, narrate this
  as "day one, deltas land tomorrow" — the honesty is itself a talking point.
- **Agent workload** — per-agent dispatch counts with staleness coloring
  (fresh/stale/very-stale), from the cost-log JSONL plus the live run registry,
  not a new table — real numbers even on a brand-new project, since Acts 1–4
  already generated the dispatches. Call out the staleness dot: it's what tells
  a client an agent has gone quiet, not just how many times it ran.
- **Needs attention** — Blocked-with-reason, pending-approval, budget alerts,
  each one-click-actionable (`Open`, `Approve`/`Reject`, `Budget settings`). Walk
  this from populated to zero live, using whatever's still open from Acts 1–4.
- **Pipeline throughput / status mix** — Done-in-7-days grouped by team
  (`teams.json`) plus approved media jobs, then the live non-Done status
  breakdown as a donut. Narrate briefly; supporting context, not the close.
- **Active runs + cost strip** — live runs from the run registry (`View log`
  opens the same `AgentRunDrawer` used on the board), and beneath it, today/week
  spend plus **cache savings %** — call this out explicitly, it answers "is this
  efficient" without anyone reading the JSONL by hand — and the costliest ticket.
- **Recent tickets / activity feed / evaluator scores** — recent tickets by
  `UpdatedAt`; the activity feed replays `GIGACLAW-*` receipts (verdicts, gates,
  cost-cap breaches) from the last 3 days, itself evidence the gates ran;
  evaluator scores read `.agents/evaluator/memory/scores.json` — the *first*
  thing that ever reads a file that's been written all along.

**Pre-rehearsal sanity check:** load `/mission` once before walking in and
confirm the KPI row shows real deltas (not "no baseline yet") and the velocity
card isn't stuck on the day-one message. If either is true, the prep checklist's
midnight-of-history prerequisite wasn't met in time — fix the timing, don't
narrate around it live.

Talking points regardless: **verdict-gated autonomy** (nothing advances on
prose, every gate re-validates a typed receipt) + **full cost visibility**
(per-ticket `AgentCostUsd` was already tracked before this project; Mission
Control is the first thing that surfaces it system-wide, and the first consumer
of `scores.json` — unblocking outcome-informed model routing later).

---

## 7. Failure-mode showcase (2 min, optional)

**Return-to-sender on a bad verdict.** Get a reviewer (`ui-auditor`,
`blog-reviewer`, `local-media-reviewer`, `qa-tester`) to post a verdict comment
that breaks the contract — edit a real verdict comment to corrupt its JSON, or
let a reviewer answer in prose once. The `*-reviewer-retry` automation reads this
as `INVALID`/`STALE`/`MISSING`, posts a `GIGACLAW-REREVIEW v1` receipt explaining
**this is the reviewer's own output failing, not the work**, and re-dispatches
the *same reviewer* once — ticket stays in `Review`, assignee untouched. A second
unusable verdict in the same episode exhausts the retry budget (`maxRetries: 1`
default): `GIGACLAW-GATE v1` receipt, `assignTicket owner`,
`moveTicketStatus Blocked`.

**What Blocked means now.** Every arm that lands a ticket in `Blocked` — a
deliberate `BLOCK` verdict or a spent reviewer-retry — assigns it to `owner`
first: *"Blocked means a human has to decide, not that the ticket is nominally
owned by an agent that stopped working on it."* Contrast with repair exhaustion
(Act 2), which is **not** Blocked — a re-scoping problem, routed to
Backlog + groomer instead.

---

## 8. Q&A crib sheet

**"How do you stop an agent from burning money?"**
Two independent caps: `dailyBudgetUsd` stops the whole project once the day's spend is gone (with a `ceo` group exemption), and `maxTicketCostUsd` — new — caps a single ticket's accumulated `AgentCostUsd` regardless of the daily total. The per-ticket cap has **no exemptions, not even the groomer** — an exempt triage agent would fund an infinite re-route loop on an already-overspent ticket. Breach posts a `GIGACLAW-COSTCAP v1` receipt and hands the ticket to the groomer; nothing dispatches again until a human raises the cap, splits the ticket, or closes it.

**"What if an agent runs away — infinite loop, crashes mid-run?"**
Repair loops are capped (`maxReviewCycles`, default 2, config-only override to 4 via a label) and counted from the ticket's own comments, not engine memory, so a restart can't reset or lose the count. If the host process dies mid-run, the run record would otherwise read "Running" forever and hold its concurrency lane shut; on restart, runs whose registering host process is no longer alive are marked `Stopped`, keyed on the registering process's PID, not the recyclable subprocess PID.

**"Is my code safe? Can an agent break my main branch?"**
Code-touching dispatches (`programmer`, `qa-tester`) run with `isolation: worktree` — never the main checkout. Merges are serialized one-at-a-time per project through a durable SQLite-backed queue; a live file lease *holds* a conflicting merge rather than letting it race or bouncing it to the back.

**"What if the LLM just writes garbage and says it's done?"**
Every gate reads a typed verdict, not prose — `SHIP`/`FIX`/`BLOCK` bound to a `sha256` digest of exactly what was reviewed. `requireFreshArtifact` re-hashes before honoring an approval, so a stale approval against changed bytes reads `STALE`, not `SHIP`. Validation is fail-closed: any exit code other than 0 from the contract validator — malformed verdict, digest mismatch, missing file — is treated as `BLOCK` everywhere it's checked.

**"Are we locked into one model or one vendor?"**
Dispatches specify a model per automation — `claude-sonnet-4-6` for most agent work, `claude-haiku-4-5` for lighter triage dispatches (groomer, evaluator, the hourly watchdog) today; the verdict contract and repair/retry loops are model-agnostic — they gate on the typed receipt shape, not on which model produced it. Swapping models is a config edit, not an architecture change.

**"Where does our data live?"**
Per-project SQLite under `%APPDATA%/GigaClaw/projects/<slug>.db`, plus a registry DB and run logs on disk — no managed cloud database in the loop. Agent memory and session state live in the workspace's own `.agents/` folder, versioned alongside the code.

**"How does this fail — what does a client actually see when something goes wrong?"**
Two shapes, deliberately different: a reviewer-side failure (bad verdict) return-to-senders automatically and is invisible unless it happens twice in a row; repair-budget exhaustion routes back to Backlog for re-scoping, not to a human. Only a **deliberate BLOCK verdict** or a **second consecutive unusable verdict** ever reaches a human, and ownership is then explicit (`assignTicket owner`) rather than an orphaned ticket nobody's watching.

**"What does it cost to run this day-to-day?"**
Per-ticket cost is tracked from the first dispatch (`AgentCostUsd`, written by `RunCostRecorder`), and the cost-log JSONL under `.agents/channel/` records every run's token usage including cache hits. Mission Control (Act 5) is the first place that aggregates this system-wide instead of per-project — the cost strip's today/week totals and cache-savings % come straight from that journal.

---

## 9. Reset procedure

1. **Delete demo tickets**: `DELETE /api/projects/<slug>/tickets/{id}` for each
   ticket created in Acts 1–4 (intake ticket, producer sub-tickets, the media
   job's ticket). Cascades cleanly, no soft delete to reconcile.
2. **Cancel or leave the media job**: `POST /api/projects/<slug>/media/jobs/{id}/cancel`
   `{ "author": "demo-reset" }` if still `running`/`queued`; a
   `completed`/`approved` job can be left once its ticket is deleted.
3. **Revert the staged imperfection**: if you staged a deliberate lint/test
   failure for the Act 2 FIX round, `git checkout` the file back or drop the
   commit — don't leave a broken commit in the demo project's history.
4. **Clean worktrees**: `git worktree list` in the project repo — confirm no
   stray worktree directories from an aborted `isolation: worktree` run.
5. **Clear labels**: remove any `external`/`publish`/`pending-approval`/`approved`/
   `extended-repair` labels added manually for staging.
6. **Revert budget edits**: if `dailyBudgetUsd`/`maxTicketCostUsd` were changed in
   `.agents/automations.json` for the demo, restore the project's real values.
7. Re-check the board is empty of demo artifacts — a stray `Blocked` ticket
   assigned to `owner` from the Act 7 showcase is the easiest thing to forget.

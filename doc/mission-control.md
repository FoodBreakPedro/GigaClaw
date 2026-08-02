# Mission Control

## Purpose
System-level operations view at `/mission`, across every non-paused project at once. Where the
[dashboard](./dashboard.md) is per-project and free-form, Mission Control is fixed, cross-project and
read-from-live-state: ticket-status KPIs with vs-yesterday deltas, ticket velocity, agent workload,
a cross-project attention queue with working actions, pipeline throughput, status mix, live runs and
spend, recent tickets, an agent-activity feed, and evaluator quality scores.

The layout, section order and visual language come from the approved "Option A — Operations" mockup
(Phase 0 of the automation/UX plan); its styles are ported into `wwwroot/app.css` under the
`/* Phase 4: mission control */` marker, mapped onto the app's existing `:root` tokens. Only the
status palette the mockup introduced (`--mc-blue`, `--mc-green`, `--mc-purple`, `--mc-amber`,
`--mc-teal`, `--mc-red`, …) is declared, scoped to `.mc-root` so it cannot leak into the board.

## Key components
- `GigaClaw.Web/Components/Pages/Mission.razor` — the page (`@rendermode InteractiveServer`). It
  renders; it does not aggregate. Linked from the app-shell nav in `Components/Layout/MainLayout.razor`
  beside **All boards** and **Projects**.
- `GigaClaw.Core/Services/MissionControlService.cs` — one `BuildAsync(nowUtc)` call assembles every
  section from live sources. Registered as a singleton in `GigaClaw.Web/Program.cs`.
- `GigaClaw.Core/Services/MissionControlModels.cs` — the section DTOs (`record` types, per repo
  convention). Every unknown is `null`, never `0`, so the page can hide a figure instead of inventing
  one.
- `GigaClaw.Core/Services/TicketStatSnapshotService.cs` — the daily snapshot writer and reader (below).
- `GigaClaw.Core/Services/CostLogReader.cs` — reads `.agents/channel/cost-log*.jsonl`.
- `GigaClaw.Core/Services/EvaluatorScores.cs` — reads `.agents/evaluator/memory/scores.json`.
- `GigaClaw.Core/Localization/Mission.{en,es,fr}.json` — every string on the page; all keys are
  `Mission`-prefixed because `LocalizationService` merges all resource files into one dictionary per
  language.

## Data sources

| Section | Source |
|---|---|
| Ticket-status KPIs | live `TicketService.ListTicketsAsync` per project; deltas from `ticket_stat_snapshots` |
| Ticket velocity | `ticket_stat_snapshots` where present, live `CreatedAt`/`UpdatedAt` derivation elsewhere |
| Agent workload | `.agents/channel/cost-log*.jsonl` (7-day window) + `AgentRunRegistry` for "running now" |
| Needs attention | Blocked tickets (`TicketSummary.BlockedReason`), `pending-approval` labels, `GIGACLAW-COSTCAP` receipts, `AutomationConfig.DailyBudgetUsd` vs today's journal spend |
| Pipeline throughput | Done tickets in the 7-day windows grouped by the assignee's team in `teams.json`, plus approved `MediaJobs` |
| Status mix | live ticket counts, excluding `Done` |
| Active runs | `AgentRunsState` → `AgentRunRegistry` (live; `View log` opens `AgentRunDrawer`) |
| Cost strip | cost journal for today/week and cache savings; `Tickets.AgentCostUsd` for the costliest ticket |
| Recent tickets | live tickets, newest `UpdatedAt` first |
| Agent activity | `GIGACLAW-*` receipt comments (last 3 days) + run start/stop from the registry |
| Evaluator scores | `<workspace>/.agents/evaluator/memory/scores.json` |

Paused projects are excluded from every section: the board hides them, and counting their tickets
would read as work in flight.

### Why the cost journal, not the run registry, for workload

`AutomationEngine` calls `AgentRunRegistry.PurgeOld(TimeSpan.FromHours(24))` on every tick, and the
purge deletes the run's JSON from `RunLogStore` too. The registry can therefore answer "what is
running now" and "what ran today", but never "how many times did `qa-tester` run this week". The cost
journal is the only durable per-dispatch record with a timestamp and an agent name, and one
sequential JSONL read per project is cheaper than rehydrating snapshots that no longer exist. Live
runs still come from the registry — the one thing the journal cannot know.

**Two windows, two questions.** The bar length is "dispatches this week" (7 days), but the journal is
read over 30 days because staleness is answered from the last run found in it. Reading only the
dispatch window made the `very-stale` (>7 d) bucket unreachable and dropped any agent idle for more
than a week off the chart entirely. The roster is seeded from the project's own members first, so a
configured agent that has never been dispatched appears with 0 dispatches and no last-run stamp —
`MissionAgentWorkload.Staleness` classes that as `very-stale`, because "never ran" is the reddest
thing the tile can report, not an absence of data.

## The snapshot table

GigaClaw stores no ticket history: a row knows its current column and nothing else, so "how many
tickets were in Review yesterday" is unanswerable from `Tickets` alone. One small table per project
database closes the gap:

```sql
CREATE TABLE IF NOT EXISTS ticket_stat_snapshots (
    Date TEXT NOT NULL,            -- yyyy-MM-dd, the day this row describes
    "Column" TEXT NOT NULL,        -- board column, or '*' for a day-totals-only row
    "Count" INTEGER NOT NULL,      -- tickets in that column at capture time
    CreatedToday INTEGER NOT NULL, -- day totals, denormalized onto every row of the date
    ResolvedToday INTEGER NOT NULL,
    CapturedAt TEXT NOT NULL,      -- when the observation actually happened
    PRIMARY KEY (Date, "Column")
);
```

Created with the repo's inline-migration convention (`CREATE TABLE IF NOT EXISTS`, raw SQLite, the
same shape `LocalMediaJobService` uses for `MediaJobs`). No EF entity, no EF Migrations.

**Rows are stamped with the day they describe.** The row-set for day *D* is written on the first tick
after *D* ended, so it records *D*'s closing state — which is exactly what makes
`GetColumnCountsAsync(yesterday)` the right baseline for a "vs yesterday" delta. `CapturedAt` records
when the observation actually happened and is returned to callers on `ColumnCountSnapshot`.

**A hole beats a lie.** The counts written are the counts read *now* — the tickets table has no
history — which is faithful only while "now" is close to *D*'s end. A laptop booted at 18:00 would
otherwise stamp yesterday's row with a full extra day of movement, and every delta against it would
read as a calm ~0 instead of a gap. So a day is written only within
`TicketStatSnapshotService.CaptureGrace` (3 h) of its end; outside it nothing is written and the day
stays absent. `CaptureProjectAsync` probes every day since the newest real row-set, not just
yesterday, so a multi-day gap is evaluated (and then correctly skipped) rather than being permanently
unreachable. `MissionControlService.BuildKpis` re-applies the same rule to `CapturedAt` before using
a lane as a baseline.

**Write-once-per-day guard.** A capture is skipped when a real (per-column) row-set already exists
for the target date. `CaptureProjectAsync` returns `false` when nothing was written.

**Backfill is deliberately partial.** On the first capture for a project, the last 30 days of
creations (from `Ticket.CreatedAt`) and resolutions (approximately, from `UpdatedAt` on `Done`
tickets) are seeded under the reserved `Column` value `'*'`. Per-column history is *not* derivable
from anything in the tickets table, so it is left absent — `DailyTicketStats.Blocked` is `null` for
those days and the velocity chart draws a gap rather than a fabricated line.

**Day one.** With no snapshot at all, KPI deltas render as "no baseline yet" and the velocity card
says "Collecting daily snapshots — the first one lands after midnight UTC". Created/resolved are
still drawn, because both are derivable from the tickets themselves.

### Where the writer lives, and why

`TicketStatSnapshotService` is its own `BackgroundService` (registered as a singleton *and* a hosted
service in `Program.cs`), ticking every 5 minutes — the same shape as
[`ScheduledPromotionService`](./ticket-scheduling.md). It is deliberately **not** a check on the
`AutomationEngine` tick: that tick is the dispatch hot path and skips paused projects, and a paused
project's history must keep accruing or it gets holes nothing can ever fill.

## Evaluator `scores.json` — the first consumer

`ProjectTemplate/Agents/evaluator/` has always written
`<workspace>/.agents/evaluator/memory/scores.json`, and until this page nothing read it. That
"something consumes `scores.json`" is the explicit gate on the **O3/O4 outcome-grounded model
routing** entry in [`roadmap/packs-and-later.md`](./roadmap/packs-and-later.md), and on revisit
trigger (c) of the A14 swarm-leader decision. The Evaluator Scores tile closes it.

The file legitimately carries two shapes and `EvaluatorScores.Parse` accepts both:

- the **score cache** (`references/procedure-steps.md`): `tickets` keyed by ticket id, each entry
  carrying `worker`, `firstPass`, `deliveryQuality`, `feedbackCompliance`, `blocked`;
- the **typed verdict-contract v1 record** (`evaluator/SKILL.md`): a `categories` array of
  outcome / process / efficiency / communication scores, each normalized onto a 0–5 axis.

Metrics absent from whichever shape a workspace uses render as `—`, never as `0`. A missing,
unreadable or malformed file yields an empty tile — the evaluator's own contract says a malformed
cache must not be overwritten, so a reader that crashed on one would be the only thing that ever
broke.

## Entry points
- `/mission` (nav: **Mission Control**).
- `Open` on an attention row or a recent-ticket row navigates to `/board/{slug}/ticket/{id}` with
  `returnTo=/mission`.
- `Approve` / `Reject` on a pending-approval row move the ticket to `Done` / `Todo`, and only out of
  `Review`: both go through `TicketService.TransitionTicketAsync(..., expectedStatus: "Review")`, so
  a ticket an agent already advanced since the queue was rendered is not dragged back out of the
  column it now belongs in. A lost race throws `TicketTransitionConflictException`, which the page
  reports inline and then re-reads the queue. The board's approvals drawer does the same.
  The `approval-mark-on-done` / `approval-clear-on-revision` automations still own the label swap.
- `Budget settings` on a budget alert opens that project's automations page.
- `View log` on a live run opens the shared `AgentRunDrawer`.

## External dependencies
- [Storage](./storage.md) — the registry and per-project SQLite databases.
- [Automation engine](./automation-engine.md) — `AutomationConfig.DailyBudgetUsd` /
  `maxTicketCostUsd`, and the `AgentRunRegistry` behind live runs.
- [Verdict contract](./verdict-contract.md) — the `GIGACLAW-*` receipt markers the activity feed and
  the blocked-reason chip are derived from.
- [Local media creation](./local-media.md) — approved `MediaJobs` feed pipeline throughput.
- [Executable teams](./executable-teams.md) — `teams.json` supplies the pipeline grouping.

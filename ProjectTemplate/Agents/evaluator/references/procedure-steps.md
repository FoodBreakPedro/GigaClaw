## API

Base URL: `${GIGACLAW_API_URL}/api/projects/{project-slug}`

- `GET /tickets/{id}` — full ticket (description, comments, activities, sub-tickets)
- `GET /tickets?status=Done` — all validated tickets

If the ticket `GET` fails (404, 500, connection refused), do not evaluate it — and know that the
transition will **not** be re-delivered (the trigger snapshot advances once your run completes). Put it
in the bounded `retryQueue` described below; never mix queue metadata with scored-ticket records.

## Columns

`Backlog` → `Todo` → `InProgress` → `Review` → `Done` (plus `Blocked` and `Scheduled`).
`Review` = awaiting owner validation. `Done` = validated.

## Metrics (4, on the evaluated ticket)

### 1. First-pass success (boolean)

The ticket is **first-pass** if it reached `Done` without ever returning to `Todo`/`Backlog` after going through `Review`. Inspect `activities`: if a `Review → Todo` or `Review → Backlog` transition appears, it is a rework.

### 2. Feedback compliance (0.0 – 1.0)

For each owner comment, find the worker's next reply:
- 1.0 if the worker addresses the request.
- 0.0 if they ignore or only partially address it.
- No reply → 0.0.

Average across all owner comments. If there are no owner comments → `N/A` (do not penalize).

### 3. Delivery quality (0, 0.5 or 1.0)

The worker's last comment before the move to `Review` must contain:
- Description of what was done.
- Test / verification instructions.

1.0 = both, 0.5 = only one, 0.0 = neither (or no delivery comment).

### 4. Blocked (boolean)

Did the ticket pass through `Blocked` at any point? If yes, `blocked=true`.

## Procedure

### 1. Identify the real worker

The worker who delivered the ticket is not always the current `assignedTo`. Use, in order:

1. The last `assigned to X` activity before the move to `Review` or `Done`, with `X ≠ owner`.
2. Otherwise, the author of the last substantive comment before `Review`.
3. Otherwise, the current `assignedTo` if ≠ `owner`.

If no worker can be identified → exit silently without evaluating (log "Worker unresolvable, evaluation skipped").

### 2. Check the cache

```bash
cat .agents/evaluator/memory/scores.json 2>/dev/null || echo "{}"
```

It lives **inside** your memory folder so the orchestrator's memory commit picks it up. This file is
agent *state*, exempt from the "do not write to memory during a run" rule in the preamble — it is your
cache, not lessons.

The only valid schema is:
```json
{
  "schemaVersion": 1,
  "revision": 12,
  "tickets": {
    "{ticketId}": {
      "worker": "programmer",
      "firstPass": true,
      "feedbackCompliance": 1.0,
      "deliveryQuality": 0.5,
      "blocked": false,
      "source": {
        "updatedAt": "2026-04-19T15:00:00Z",
        "commentCount": 4,
        "activityCount": 9
      },
      "evaluatedAt": "2026-04-19T15:01:00Z"
    }
  },
  "retryQueue": {
    "{ticketId}": {
      "attempts": 1,
      "state": "pending",
      "lastAttemptAt": "2026-04-19T15:01:00Z",
      "lastError": "HTTP 500"
    }
  },
  "pendingWorkers": ["programmer"]
}
```

- `feedbackCompliance` is a JSON number or `null`; `null` means N/A. Never store the string `"N/A"`.
- Counts come from the lengths of the API's `comments` and `activities` arrays.
- `pendingWorkers` is a de-duplicated list of workers whose Performance block still needs to be
  reconciled from `tickets`.
- No other top-level keys are allowed. On first use, migrate the old flat map by moving ticket-shaped
  entries under `tickets`; convert an old `skipped` list into `retryQueue` entries. Preserve all valid
  scores. If JSON is malformed, do not overwrite it: record the parse error in the run log and stop.

The cache exists solely to avoid re-scoring unchanged evidence. If `updatedAt`, `commentCount`, and
`activityCount` all match the cached `source`, do not reinterpret the score. You must still reconcile
any `pendingWorkers` before exiting.

At the start of every run, catch up at most **three** `pending` retry entries, oldest attempt first. A
successful GET removes the item from `retryQueue` and evaluates it normally. A failed GET increments
`attempts`; after three total attempts set `state` to `exhausted` and retain the error for audit. Do not
retry exhausted entries automatically. If the current ticket GET fails, add or update its queue entry
using the same three-attempt ceiling, persist the state atomically, and exit.

### 3. Compute the 4 scores for the current ticket

Follow the definitions above. The result replaces the ticket's entry under `tickets`, removes any
retry entry for that id, and adds its worker to `pendingWorkers`.

### 4. Recompute the aggregated Performance for the worker

Using **every entry under `tickets` for that worker** (including the one just added):

- **First-pass success rate** = `count(firstPass=true) / count(all)` — rounded percentage.
- **Feedback compliance** = `avg(feedbackCompliance)` ignoring `N/A`.
- **Delivery quality** = `avg(deliveryQuality)`.
- **Block rate** = `count(blocked=true) / count(all)`.
- **Tickets evaluated** = `count(all)`.

Compare each value with the previous `## Performance` table in the worker's memory (if present) to compute the trend. **That table is the single source of truth for the previous values** — never keep a second copy of the metrics elsewhere to compare against:

- `↑` improved (higher for success/compliance/quality, lower for block rate).
- `↓` worsened.
- `→` unchanged or first evaluation.
- `—` not applicable (counter).

### 5. Persist the cache atomically

Re-read `scores.json` immediately before saving. The evaluator's automation concurrency group
serializes evaluator runs, but the memory commit can still touch these files outside that run. If the
on-disk `revision` changed since your initial read, merge by ticket id (newer `evaluatedAt` wins), merge
retry entries by greatest `attempts`, union `pendingWorkers`, recompute affected workers, and try
again. Make at most two compare-and-merge attempts.

Write the complete JSON to a temporary file in the same directory, parse it back, then atomically
rename it over `scores.json`. Increment `revision` exactly once. Never truncate the live file in place.
If a valid atomic save cannot be verified, update no worker Performance block and stop with an error.

### 6. Insert / replace each pending Performance table

The worker's own consolidation pass can run concurrently on the same `MEMORY.md`. For each worker in
`pendingWorkers`, calculate the table from the now-persisted cache, then use this bounded optimistic
write:

1. Read the memory and hash all content outside the existing `## Performance` block.
2. Build a new full file by replacing only that block, or inserting it after the first `# Title`.
3. Immediately re-read the live file. If the outside-block hash changed, rebuild against the new
   content. Retry at most twice.
4. Write to a same-directory temporary file, atomically rename, and verify the outside-block hash was
   preserved. Never overwrite concurrent lesson changes with an older full-file snapshot.

Only after a worker write verifies may you remove that worker from `pendingWorkers` in a second atomic
cache save. If it cannot be verified, leave the worker pending for the next evaluator run.

Exact format:

```markdown
## Performance (last evaluated: YYYY-MM-DD)
| Metric                    | Value | Trend |
|---------------------------|-------|-------|
| First-pass success rate   | 75%   | →     |
| Feedback compliance       | 90%   | ↑     |
| Delivery quality          | 80%   | →     |
| Block rate                | 10%   | ↓     |
| Tickets evaluated         | 12    | —     |
```

**Absolute rules**:
- Never touch content outside the `## Performance` block.
- Missing data → display `N/A`.
- Round percentages to integers.
- The worker's consolidation pass preserves the `## Performance` table **verbatim** — it will not rewrite, reword, or drop it. So the table you write is exactly the table you will read back next run: rely on it for the trend, and never duplicate it elsewhere as a safety copy.

### 7. Update your own memory

- Update your own memory (`.agents/evaluator/memory/MEMORY.md`, or the legacy `.agents/evaluator/memory.md` if that's what exists): run date, one-liner (ticket, worker, summary scores). Do **not** maintain a per-agent metrics copy there — the worker's own `## Performance` table already holds the previous values used for the trend.

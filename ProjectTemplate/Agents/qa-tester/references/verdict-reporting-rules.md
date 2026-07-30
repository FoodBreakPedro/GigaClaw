### 4. Post the verdict report

Score strictly across these 4 categories: Build (max 5), Acceptance criteria (max 10), Adversarial tests (max 10), and Regression risk (max 5).

Post your report as a ticket comment containing the typed verdict header and fenced JSON object:

```text
GIGACLAW-VERDICT v1 qa-tester SHIP artifact-sha256:0c92a48eb15d7f36c0d8241be5739af06c1e8d4b27950fa3e6c81d70b4925fe8

```json
{
  "schemaVersion": 1,
  "agent": "qa-tester",
  "ticketId": 447,
  "verdict": "SHIP",
  "summary": "All acceptance criteria verified at runtime; adversarial scenarios handled gracefully.",
  "categories": [
    { "name": "Build", "score": 5, "max": 5, "notes": "dotnet build clean, 0 compilation errors." },
    { "name": "Acceptance criteria", "score": 10, "max": 10, "notes": "All criteria observed passing via API/test execution." },
    { "name": "Adversarial tests", "score": 10, "max": 10, "notes": "Tested null payloads and rapid double submission; handled gracefully." },
    { "name": "Regression risk", "score": 5, "max": 5, "notes": "Adjacent test suite green." }
  ],
  "vetoItems": [],
  "evidence": [
    { "kind": "hash", "ref": "sha256:0c92a48eb15d7f36c0d8241be5739af06c1e8d4b27950fa3e6c81d70b4925fe8", "note": "artifact receipt" }
  ],
  "reviewedAtUtc": "2026-07-30T11:41:05Z",
  "inputDigest": "sha256:0c92a48eb15d7f36c0d8241be5739af06c1e8d4b27950fa3e6c81d70b4925fe8"
}
```
```

#### Machine-Checkable Veto Items
If issuing `FIX` or `BLOCK`, include machine-checkable veto items (slug code + factual statement):
- `cannot-exercise-change`: Application fails to start, missing environment setup, or test execution impossible (`BLOCK`).
- `failing-acceptance-criterion`: One or more acceptance criteria failed during runtime verification (`FIX`).
- `failing-adversarial-test`: One or more sneaky/adversarial scenarios produced an unhandled exception or corrupt state (`FIX`).

> **POST/PATCH discipline**: Write the comment JSON payload to a workspace temp file (e.g. `./qa-report.json`) with `Write`, then POST with `-d @file`. Always validate your verdict JSON locally before posting:
> `python3 .agents/scripts/content_contract.py --verdict ./verdict.json`

```bash
http=$(curl -s -o ./qa-resp.json -w "%{http_code}" \
  -X POST ${GIGACLAW_API_URL}/api/projects/{project-slug}/tickets/{id}/comments \
  -H "Content-Type: application/json" \
  -d @./qa-report.json)
[[ "$http" =~ ^2 ]] || { echo "POST failed http=$http"; cat ./qa-resp.json; exit 1; }
```

### 5. Act on the verdict

**SHIP** (verdict: `SHIP`) → leave the ticket in `Review` untouched. The `verdictIs` automation gate will advance the ticket once verified. Only issue `SHIP` if `vetoItems` is empty (`[]`) and all categories pass.

**FIX** (verdict: `FIX`) → move ticket status to `Todo` (the repair loop will re-dispatch `programmer` with the failed categories and machine-readable veto items).

**BLOCK** (verdict: `BLOCK`) → move ticket status to `Blocked` (environment/tooling broken or repeated failure cycle).

```bash
http=$(curl -s -o ./qa-resp.json -w "%{http_code}" \
  -X PATCH ${GIGACLAW_API_URL}/api/projects/{project-slug}/tickets/{id}/status \
  -H "Content-Type: application/json" \
  -d @./qa-status.json)   # {"status":"Todo","author":"qa-tester"}
[[ "$http" =~ ^2 ]] || { echo "PATCH status failed http=$http"; cat ./qa-resp.json; exit 1; }
```

**If you were dispatched as the ticket's assignee** (ticket in `Todo`/`InProgress` rather than `Review`): post your report, then move the ticket out of `InProgress` — `Review` on SHIP, `Todo` on FIX, `Blocked` if untestable — and set `assignedTo` back to `programmer` (or `owner` if unclear). Never end a turn with a ticket assigned to you in `InProgress`.

# Agent Handoff Reference (v1)

When you finish a run on a ticket or hand off work to another agent, you write a structured **handoff**. The handoff preserves what you changed, what files you own, your assumptions, open loops, and acceptance criteria so the next agent (or human owner) picks up work without guessing or re-deriving context.

## When to Write a Handoff

Write a handoff as a ticket comment at the end of your run whenever you:
- Complete your assigned phase on a ticket and transition `nextRole` / `assignedTo` to another agent.
- Complete work and return the ticket to the human owner (`nextRole: null`).
- Become blocked and assign the ticket to the owner with a blocking open loop (`blocking: true`).

## Transport & Marker Format

Post the handoff as a ticket comment formatted with a header marker line followed by a fenced `json` block:

```text
GIGACLAW-HANDOFF v1 <agent> ticket-<id> run-<runId>

```json
{
  "schemaVersion": 1,
  "agent": "your-agent-slug",
  "ticketId": 42,
  "runId": "run-20260730-01",
  "summary": "Completed initial draft of feature implementation.",
  "inputs": [],
  "outputs": [
    { "kind": "path", "ref": "src/feature.cs", "note": "Implemented core logic" }
  ],
  "ownedFiles": ["src/feature.cs"],
  "assumptions": ["Assumed default config timeout of 30s is acceptable."],
  "openLoops": [],
  "acceptanceCriteria": [
    { "statement": "Core feature logic implemented", "met": true, "evidenceRef": "src/feature.cs" }
  ],
  "nextRole": "qa-tester",
  "producedAtUtc": "2026-07-30T20:00:00Z"
}
```
```

## Schema Fields & Rules

- **`schemaVersion`**: Integer `1`.
- **`agent`**: Your exact agent slug (e.g. `blog-writer`, `qa-tester`). Must match header marker.
- **`ticketId`**: Integer or string matching ticket ID. Must match header marker.
- **`runId`**: Unique run identifier string. Must match header marker.
- **`summary`**: Concise one-line overview of what was accomplished and what the next agent must do.
- **`inputs`**: Array of artifacts consumed (`kind`: `path`|`hash`|`link`, `ref`, optional `note`).
- **`outputs`**: Array of artifacts produced (`kind`: `path`|`hash`|`link`, `ref`, optional `note`).
- **`ownedFiles`**: Array of workspace-relative paths or globs written or claimed by your run.
  - **Path Rules**: Must be workspace-relative (no leading `/` or `C:\`), must **not** contain `..` parent escapes, and entries must be unique.
  - **Lease Layer Impact**: Used by host file-ownership leases to serialize overlapping runs. Overstating blocks other agents; understating corrupts files.
- **`assumptions`**: List of unconfirmed decisions made during your run.
- **`openLoops`**: Unfinished items (`statement`, optional `blocking`: `true`|`false`).
  - **Rule**: If any open loop has `blocking: true`, `nextRole` **must** be `null` (returned to owner).
- **`acceptanceCriteria`**: Ticket criteria (`statement`, `met`: `true`|`false`, optional `evidenceRef`).
  - **Rule**: If `met` is `true`, `evidenceRef` is required and must match an `outputs` artifact `ref`.
- **`nextRole`**: Agent slug handed work next, or `null` if handed back to human owner.
- **`producedAtUtc`**: ISO-8601 UTC timestamp ending in `Z` (e.g. `2026-07-30T20:00:00Z`).

# Verdict contract (v1)

**Purpose.** Every reviewer, quality gate and eval judge in GigaClaw emits the *same* machine-readable judgement. Before this contract, a gate had to parse prose ("APPROVE — score 93/100"), which meant a reviewer could pass a ticket by accident of wording and a stale approval could be replayed against a changed artifact. A verdict is a typed object, bound to the exact bytes it judged, that a machine can gate on.

Frozen at **v1**: additive changes only (new optional fields), never renames or removals. A breaking change bumps `schemaVersion` and ships a migration.

## Shape

Schema: [`ProjectTemplate/Agents/scripts/verdict.schema.json`](../ProjectTemplate/Agents/scripts/verdict.schema.json) (JSON Schema 2020-12 subset). Unknown properties are rejected.

| Field | Meaning |
|---|---|
| `schemaVersion` | Always `1`. |
| `agent` | Slug of the reviewing agent (its `ProjectTemplate/Agents/<slug>` directory name). |
| `ticketId` | Board ticket judged. Integer, or the same integer as a string. |
| `verdict` | `SHIP` (advance) · `FIX` (return to the producing agent) · `BLOCK` (stop, escalate to owner). |
| `categories[]` | The agent's own rubric: `{name, score, max, notes?}`. Totals are **derived**, never stored. |
| `vetoItems[]` | Hard failures: `{code, statement, evidenceRefs?}`. Any veto item forbids `SHIP`. |
| `evidence[]` | `{kind: path\|hash\|link, ref, note?}` — what the judgement was derived from. |
| `reviewedAtUtc` | ISO-8601 UTC instant, must end with `Z`. |
| `inputDigest` | `sha256:<64 hex>` of the artifact reviewed. |
| `summary?` | One human-readable line. Never the machine-readable reason. |
| `reviewCycle?` | `{current, max}` for the repair loop. Informational — the durable counter lives on the ticket. |

Rules the schema cannot express, enforced by the validator:

- a category's `score` never exceeds its `max`; category names are unique;
- `SHIP` with a non-empty `vetoItems` is a contradiction and is rejected;
- `FIX`/`BLOCK` must carry a machine-readable reason — at least one veto item, or at least one category below `max`;
- `evidenceRefs` must resolve to an entry in `evidence`;
- `path` evidence is workspace-relative (no absolute paths, no `..` traversal), `hash` evidence is `sha256:<64 hex>`, `link` evidence is an absolute non-placeholder `https` URL.

## Transport

A verdict travels as a ticket comment. The comment carries a marker line — same family as the existing `BLOG-REVIEW`/`UI-AUDIT` receipts — followed by a fenced `json` block:

````text
GIGACLAW-VERDICT v1 <agent> <SHIP|FIX|BLOCK> artifact-sha256:<digest>

```json
{ …verdict… }
```
````

The **last** marker in a comment body wins, so an edited comment cannot resurrect an earlier judgement. Marker and body must agree on `agent`, `verdict` and `inputDigest`; a comment that disagrees with itself is not a verdict.

Agents may additionally write the verdict to a workspace file, but the comment is authoritative — it is what the board and the automation engine read. A verdict that exists only as a file is invisible: nothing scans the workspace for judgements.

## Validating

The single implementation is [`verdict_contract.py`](../ProjectTemplate/Agents/scripts/verdict_contract.py), shipped to every workspace at `.agents/scripts/`. `content_contract.py --verdict` forwards to it so agents already calling the content contract have one enforcement point, not two.

```bash
python3 .agents/scripts/verdict_contract.py verdict.json \
  --expect-digest sha256:<digest of what was actually reviewed> \
  --expect-agent qa-tester --expect-ticket 447

python3 .agents/scripts/verdict_contract.py --extract comment.md --out verdict.json
python3 .agents/scripts/verdict_contract.py --self-test
```

Exit codes: `0` valid · `1` contract violation · `2` unreadable input or bad usage. **Fail closed:** a caller treats anything other than `0` as `BLOCK`. Structural rules are read from the schema file at runtime — the validator holds no second copy of the field list, so schema and enforcement cannot drift apart.

`--expect-digest` is how stale approvals die: a verdict whose `inputDigest` no longer matches the current artifact is rejected as stale rather than honored.

## Gating with `verdictIs`

The automation condition `verdictIs` reads the newest verdict on the firing ticket and resolves it to one outcome: `SHIP`, `FIX`, `BLOCK`, or one of three failure modes — `MISSING` (no verdict comment), `INVALID` (breaks the contract) and `STALE` (valid, but the artifact changed after review). The failure modes are separate outcomes on purpose: a reviewer that answers in prose reads as `MISSING`, not as "not reviewed yet", so the pipeline stalls visibly instead of advancing.

```json
{ "type": "verdictIs", "verdicts": ["SHIP"], "agent": "blog-reviewer" }
{ "type": "verdictIs", "verdicts": ["FIX"] }
{ "type": "verdictIs", "verdicts": ["BLOCK", "INVALID", "STALE", "MISSING"] }
```

Three automations on the same Review column therefore express advance, repair and escalate. `agent` (which accepts `{assignee}`) restricts the scan to one reviewer, so a second reviewer's older verdict never shadows the one being gated on. Entries the condition doesn't recognize match nothing — a typo blocks rather than opens the gate.

`requireFreshArtifact` (default on) re-hashes the files listed as `path` evidence; unless one still matches `inputDigest`, the outcome is `STALE`. Freshness that cannot be verified — no path evidence, missing file, unreadable workspace — is stale too. Turn it off only for reviewers whose input is not a workspace file.

That case is real: the evaluator judges board state, not a file, so its `inputDigest` is a ticket-snapshot digest no file will ever hash to. Such a verdict must **not** list `path` evidence pointing at something unrelated — a cache file, a report it happened to write — because that reads as a stale artifact rather than as "not file-based". Cite the snapshot as `hash` evidence, and set `requireFreshArtifact: false` on any condition that gates on it.

## The bounded repair loop

A `FIX` is not a rejection, it is a work order: the reviewer is asking the producing agent for another pass. Two automations on the same Review column express the whole loop, distinguished only by the `repairBudget` condition:

```json
{ "type": "verdictIs", "verdicts": ["FIX"] }
{ "type": "repairBudget", "mode": "withinCap" }     // → moveTicketStatus InProgress + runAgent {assignee}

{ "type": "verdictIs", "verdicts": ["FIX"] }
{ "type": "repairBudget", "mode": "exhausted" }     // → addComment "{verdictHistory}" + moveTicketStatus Blocked
```

`repairBudget` says nothing about whether a repair is outstanding — a fresh ticket is trivially "within cap" — so it is only meaningful next to `verdictIs`. An unknown `mode` matches neither arm, and a contract manifest that exists but cannot be parsed resolves to `exhausted`: an unknowable budget escalates to a human rather than looping.

**The re-dispatch carries the findings.** `ActionExecutor` prepends the outstanding verdict's veto items and below-maximum categories to the run's context, ahead of the [handoff](./handoff-contract.md) and the action's own `context` — the agent is told what was refused instead of re-deriving it from the ticket. When the newest FIX judged the same `inputDigest` as the one before it, the brief says so: the previous attempt changed nothing, and that is a different failure from iterating and still falling short.

**Where the round count comes from.** Not a counter column and not engine memory — the ticket's own comments. One round per `FIX` verdict since the episode opened; a `SHIP`, a `BLOCK` or this loop's own escalation receipt closes the episode and the next `FIX` is round one again. That makes the number auditable (an owner recounts it by reading the ticket), restart-proof (there is no state to lose) and immune to a resumed or re-triggered run restarting the count. Deduplicating identical verdicts was considered and rejected: a reviewer re-reviewing unchanged bytes is exactly the runaway case the cap exists for, so a repeat spends a round like any other.

**The cap** is `maxReviewCycles` from [`contracts.json`](../ProjectTemplate/Agents/contracts.json) — the ticket's assignee first, then the reviewer named in the condition, then the manifest `defaults` block, then 2. `maxCycles` on the condition overrides all of it.

**The escalation comment** is what the owner actually reads, so it is self-contained. The `{verdictHistory}` placeholder — recognized in any `addComment` `content` — expands to the receipt marker plus every round: which reviewer, when, which artifact digest, every veto item and every category below its maximum. No run log required. The block deliberately does not reproduce verdict marker lines, so quoting past judgements cannot be misread as issuing a new one.

````text
GIGACLAW-REPAIR v1 ticket-42 escalated 2/2

The repair loop is exhausted: blog-reviewer returned FIX 2 times without a SHIP, …

### Round 1/2 — blog-reviewer, FIX, 2026-07-30 12:00Z
Artifact reviewed: `sha256:…`
…
````

Cost is not plumbed separately: the loop dispatches through the ordinary `runAgent` path, so every round accumulates on the ticket's existing token/cost badge via `CostTracker`.

## Consumers

- **Gate** — `verdictIs` gates ticket exit on a valid verdict instead of prose. Invalid or stale ⇒ Blocked with a receipt.
- **Repair loop** — a `FIX` verdict re-dispatches the producing agent with the failed categories and veto items injected, capped by `maxReviewCycles` from [`contracts.json`](../ProjectTemplate/Agents/contracts.json).
- **Eval judge** — the eval harness scores agents with the same shape, so an eval verdict and a review verdict are comparable objects. Its deterministic rubric judge is a pure function of the replayed stream, so it stamps `reviewedAtUtc` with the Unix epoch and cites `hash` evidence only (see the non-file case above); its opt-in real-LLM judge records the model, CLI version and settings beside the verdict and is explicitly informational. See [`GigaClaw.Eval/README.md`](../GigaClaw.Eval/README.md).

Worked examples (one per gating reviewer) and the rejection corpus live in `GigaClaw.Core.Tests/Fixtures/verdicts/`; `TemplateVerdictContractTests` runs the validator against all of them, so a schema edit that breaks a reviewer fails the build. `RepairLoopTests` covers the loop end to end over a real project, including a rebuilt executor that must resume the count rather than restart it.

## Wiring

[`automations.json`](../ProjectTemplate/Agents/automations.json) carries one gate per reviewed pipeline, all of them triggered by `ticketCommentAdded` filtered to the reviewer's slug and scoped to `ticketInColumn: ["Review"]` — the gate governs *exit from Review*, so a ticket the reviewer already routed is not re-litigated.

| Pipeline | SHIP advances to | `requireFreshArtifact` |
|---|---|---|
| `qa-tester` → `programmer` | Done | **off** — the judgement covers a multi-file change plus an executed test run, not one hashable file |
| `ui-auditor` → `ui-designer` | Done | on |
| `blog-reviewer` → `blog-writer` | `blog-seo` in Todo | on |
| `local-media-reviewer` → the three local-media agents | `pending-approval`, stays in Review for the owner | on |
| `blog-reviewer` → `content-writer` (AD-7) | Done + `ready-for-cms` | **off** — the draft is the ticket description, and the writer deletes its scratch file before exiting |

Each pipeline also has a `FIX`/`withinCap` repair arm, its `FIX`/`exhausted` twin, and an escalation arm reading `["BLOCK", "INVALID", "STALE", "MISSING"]`. Both blocking arms write a receipt before the move. Anything that closes a ticket outright also carries a negated `labels` guard on `external`/`publish`/`deploy`/`outbound`, so a SHIP verdict cannot bypass `approval-gate-on-review`: a quality judgement is not a publishing approval.

The `evaluator` is deliberately absent. `evaluator-on-done` runs *after* a ticket leaves the board, so gating on its verdict would mean re-opening finished work rather than governing an exit.

The four AD-7 arms ship with `enabled: false`. `blog-reviewer`'s AD-7 protocol still answers with `CONTENT-REVIEW` markers and no typed verdict, so switching them on would read as `MISSING` and Block every content-pipeline draft — the same reason the whole gate waited on G2. They turn on with one flag once that protocol emits `GIGACLAW-VERDICT`.

`TemplateAutomationContractTests` pins the shape of the wiring; `TemplateVerdictGateTests` runs the shipped automations against a real project through a real `ActionExecutor`.

Related: [automation engine](./automation-engine.md) · [project template](./project-template.md) · [roadmap lane CL](./roadmap/lane-claude-orchestration.md).

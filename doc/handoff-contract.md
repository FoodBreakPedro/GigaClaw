# Handoff contract (v1)

**Purpose.** When one agent finishes and the next picks the ticket up, everything the first one learned — what it assumed, what it deliberately left open, which files it touched — used to live in prose comments, or nowhere. The next agent re-derived it, or guessed. A handoff is the typed record of that transfer: outputs, owned files, assumptions, open loops, acceptance criteria, and where the work goes next.

Frozen at **v1** alongside the [verdict contract](./verdict-contract.md): additive changes only. An unreadable handoff is treated as *no* handoff — the next agent starts from the ticket rather than from something half-parsed.

## Shape

Schema: [`ProjectTemplate/Agents/scripts/handoff.schema.json`](../ProjectTemplate/Agents/scripts/handoff.schema.json). Unknown properties are rejected.

| Field | Meaning |
|---|---|
| `schemaVersion` | Always `1`. |
| `agent` · `ticketId` · `runId` | Who produced it, for which ticket, in which run. `runId` keeps the chain auditable across restarts. |
| `summary` | The one line the next agent reads first. |
| `inputs[]` · `outputs[]` | `{kind: path\|hash\|link, ref, note?}` — same artifact discipline as verdict evidence. |
| `ownedFiles[]` | Workspace-relative paths or globs this run wrote or claims. **Consumed by the file-ownership lease layer.** |
| `assumptions[]` | Decisions taken without confirmation. The next agent inherits or challenges them. |
| `openLoops[]` | `{statement, blocking?}` — known-unfinished work. |
| `acceptanceCriteria[]` | `{statement, met, evidenceRef?}` — the ticket's criteria as this run understands them. |
| `nextRole` | Agent slug the work is handed to, or `null` when it goes back to the owner. |
| `producedAtUtc` | ISO-8601 UTC, must end with `Z`. |

Rules the schema cannot express, enforced by the validator:

- `ownedFiles` are workspace-relative, contain no `..`, and are unique — a lease on an absolute path or a traversal is not enforceable, and a lease scope is a set;
- a criterion marked `met` must cite an `evidenceRef` that appears in `outputs`; a claim without evidence is not a handoff;
- a run declaring no outputs, no owned files *and* no open loops must say what it did — silence is not a handoff;
- a **blocking** open loop forces `nextRole` to the owner. Handing blocked work to another agent is how a pipeline spins.

## Transport

Same shape as verdicts: a marker line, then a fenced `json` block, posted as a ticket comment.

````text
GIGACLAW-HANDOFF v1 <agent> ticket-<id> run-<runId>

```json
{ …handoff… }
```
````

The last marker in a comment wins, and marker and body must agree on `agent`, `ticketId` and `runId`. The comment is authoritative — a handoff that exists only as a file is invisible, because nothing scans the workspace for them.

## Validating

```bash
python3 .agents/scripts/handoff_contract.py handoff.json --expect-agent programmer --expect-ticket 42
python3 .agents/scripts/handoff_contract.py --extract comment.md --out handoff.json
python3 .agents/scripts/handoff_contract.py --self-test
```

Exit codes: `0` valid · `1` contract violation · `2` unreadable input. Structural rules are read from the schema file at runtime through [`schema_check.py`](../ProjectTemplate/Agents/scripts/schema_check.py) — the same subset evaluator the verdict contract uses, so neither validator holds a second copy of its field list.

## Consumers

- **Dispatch** — before running an agent against a ticket, the engine finds the newest readable handoff and prepends a rendering of it to the run's context: summary, produced artifacts, assumptions, *unmet* criteria, open loops, and the files the previous run claimed. Criteria already met are left out; they are not worth the next hop's prompt budget.
- **File-ownership leases** — `ownedFiles` is the declared scope a lease is taken on, so two runs whose scopes intersect serialize instead of racing. Overstating scope blocks other agents; understating it corrupts their work.
- **Team runs** — when a team run joins, the synthesizer's brief is a rendering of each reporting lane's handoff, alongside a named list of the lanes that produced none. See [executable teams](./executable-teams.md).

Worked examples and the rejection corpus live in `GigaClaw.Core.Tests/Fixtures/handoffs/`; `TemplateHandoffContractTests` runs the real validator over all of them and cross-checks the host-side reader, so the two implementations cannot drift.

Related: [verdict contract](./verdict-contract.md) · [automation engine](./automation-engine.md) · [roadmap lane CL](./roadmap/lane-claude-orchestration.md).

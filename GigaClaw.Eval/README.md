# GigaClaw agent eval

Three layers share one CLI:

- `static` (default verb) — reads the committed catalog and `ProjectTemplate/` and never
  runs an agent.
- `replay` — dispatches an agent against a canned ticket through the mock `claude` CLI and
  captures the stream it produced.
- `judge` — replays, then **scores** the captured stream against the agent's rubric and emits
  a [v1 verdict](../doc/verdict-contract.md).

## Static eval

Run the complete committed catalog:

```bash
dotnet run --project GigaClaw.Eval -- all
```

Run one catalog agent by slug:

```bash
dotnet run --project GigaClaw.Eval -- programmer
```

The default **baseline** mode fails unreadable or malformed inputs, missing/drifted
baselines, and other integrity errors. Matching policy findings remain visible but
do not fail. `--strict` also fails every policy warning or error.

Reviewed per-agent snapshots live in `GigaClaw.Eval/baselines/`. Regenerate them
for review with `all --update-baselines`, then regenerate the system catalog so
its `EvalBaselinePresent` fields stay current. Normal run reports overwrite
`artifacts/eval/<agent|all>.json`; that configured artifact root is gitignored.

Prompt budget source, units, and thresholds are versioned in `evalconfig.json`.
The console prints actual elapsed time; reports and baselines omit timestamps and
timings so identical inputs produce identical committed/output JSON.

## Canned-ticket replay

Build the mock CLI once, then replay:

```bash
dotnet build GigaClaw.ClaudeMock -c Release
dotnet run --project GigaClaw.Eval -- replay all
```

The target selects fixtures by id, by pipeline family, by agent slug, or `all`:

```bash
dotnet run --project GigaClaw.Eval -- replay dev                     # a whole family
dotnet run --project GigaClaw.Eval -- replay blog-writer             # one agent
dotnet run --project GigaClaw.Eval -- replay dev-fix-login-timeout   # one fixture
```

Exit codes match the static layer: `0` all fixtures pass, `1` at least one fixture
failed, `2` a usage or configuration error. One JSON report per agent is written to
`artifacts/eval/replay/<agent>.json`.

### What a fixture is

A fixture is two committed files:

- `fixtures/<id>.json` — the canned ticket (id, title, status, assignee, description,
  comments), the agent it is dispatched to, its pipeline family, the scenario it replays,
  and an `Expect` block: exit code, run status, the stream event kinds that must appear
  **in order** (as a subsequence, so a new runtime event does not break a fixture), and a
  marker string the final assistant message must contain.
- `fixtures/scenarios/<scenario>.ndjson` — the canned model transcript the mock replays,
  in the same NDJSON dialect as `GigaClaw.ClaudeMock/scenarios/` (`_meta.exit` sets the
  process exit code, `{{session_id}}` is rewritten at replay time).

The fixture id must equal its filename, the agent must exist in `catalog.json`, and the
scenario file must exist — a replay refuses to start otherwise.

Committed fixtures, one per pipeline family:

| Fixture | Family | Agent |
| --- | --- | --- |
| `blog-draft-release-notes` | blog | `blog-writer` |
| `dev-fix-login-timeout` | dev | `programmer` |
| `dev-suite-fails-hard` | dev | `qa-tester` |
| `governance-reject-unscoped-release` | governance | `approval-gatekeeper` |
| `growth-nurture-sequence` | growth | `growth-writer` |
| `media-storyboard-teaser` | media | `local-media-director` |

`dev-suite-fails-hard` deliberately exits non-zero: it proves the harness records a failed
dispatch as data rather than crashing on it.

### How a replay stays hermetic and deterministic

Each fixture gets a throwaway workspace under the system temp directory containing the
agent's committed `SKILL.md`, `memory/MEMORY.md`, the shared `preamble.md` and
`contracts.json`, the rendered ticket (the offline stand-in for the REST fetch the agent
would otherwise make), and the scenario. The dispatch goes through the product's own
`ClaudeRunner`, with `GIGACLAW_CLAUDE_BIN` pointed at the mock built from
`GigaClaw.ClaudeMock` and `GIGACLAW_MOCK_SCENARIO` naming the scenario explicitly, so
fixture selection never depends on the wording of a committed skill. No network, no real
model, and nothing written to `%APPDATA%/GigaClaw`.

Captured events are normalized before they are reported: the workspace path and the
generated session id are replaced with placeholders, and timings, costs and timestamps are
never recorded — same rule as the static layer. `StreamDigest` is a SHA-256 over the
normalized stream, so two runs of a fixture can be compared at a glance.

### Real CLI (costed, opt in)

`--real-cli` replaces the mock with the actual `claude` binary. It spends tokens, so it is
gated twice: the flag itself, plus `GIGACLAW_EVAL_ALLOW_REAL_CLI=1` in the environment.
`GIGACLAW_EVAL_CLAUDE_BIN` overrides which binary is used; otherwise `claude` is resolved
from `PATH`. Real-CLI runs are not deterministic and are not run in CI.

```bash
GIGACLAW_EVAL_ALLOW_REAL_CLI=1 \
  dotnet run --project GigaClaw.Eval -- replay dev-fix-login-timeout --real-cli
```

### Tests

Replay tests live in `GigaClaw.Eval.Tests` (`ReplayRunnerTests`) and run against the real
repository, so the mock must be built first:

```bash
dotnet build GigaClaw.ClaudeMock -c Release
dotnet test GigaClaw.Eval.Tests -c Release
```

`Replay_IsDeterministic_AcrossRepeatedRunsOfTheSameFixture` replays one fixture twice and
asserts the two written reports are byte-identical.

### Not in this layer

Replay only captures the stream and checks it mechanically. Scoring what the reply *says* is
the `judge` layer below; sampling variance across repeated non-deterministic runs (Monte
Carlo) is still a separate slice.

## Judge

```bash
dotnet build GigaClaw.ClaudeMock -c Release
dotnet run --project GigaClaw.Eval -- judge all
```

The judge replays the target (same targets as `replay`: fixture id, family, agent slug, or
`all`), scores each captured stream against that agent's rubric, and emits **the same verdict
object a reviewer emits** — `ProjectTemplate/Agents/scripts/verdict.schema.json`, documented in
[doc/verdict-contract.md](../doc/verdict-contract.md). Exit codes match the other layers: `0`
everything matched its baseline, `1` a regression or a rejected verdict, `2` a usage or
configuration error. One JSON report per agent is written to `artifacts/eval/judge/<agent>.json`.

### Rubrics

`rubrics/<agent>.json` is the agent's own rubric, falling back to `rubrics/default.json` for
agents that have not been given one. Each criterion names one deterministic check over the
captured stream:

| Check | Scores |
| --- | --- |
| `replay-expectations` | full when every fixture expectation held, otherwise zero |
| `no-error-events` | full when the stream carries no `error` event |
| `event-kinds-present` | proportional to how many of `Values` appear as event kinds |
| `final-text-contains-all` | proportional to how many of `Values` appear in the final message |
| `final-text-contains-any` | full when any of `Values` appears |
| `final-text-omits-all` | proportional to how many of `Values` are absent |
| `final-text-min-length` | proportional to `Threshold` characters |
| `tool-use-at-most` | full when `tool_use` events are within `Threshold` |

Text checks compare case-insensitively: a rubric asserts that a property was reported, not how
it was capitalized. A criterion with a `Veto` code emits a veto item when it scores zero, and any
veto item forbids `SHIP`. Otherwise the decision is `SHIP` at or above `ShipThresholdPercent`
(90 in `evalconfig.json`) and `FIX` below it.

### Determinism

The deterministic judge is a pure function of `(rubric, captured stream)`. It reads no clock, no
filesystem, no environment and no model; scores are rounded to two decimals and all messages are
formatted invariantly. The one contract field a pure function cannot honestly supply is
`reviewedAtUtc` — there is no "when" for a function of its arguments — so it is stamped with the
Unix epoch and the verdict is bound to what it judged by `inputDigest` (the SHA-256 of the
normalized stream) instead. `Judge_IsByteIdentical_AcrossRepeatedRunsOfTheSameFixture` replays and
judges one fixture twice and asserts the two written reports are byte-identical.

Verdicts cite **`hash` evidence only**. The judged artifact is a captured stream, not a workspace
file; citing an unrelated file would make every eval verdict read as `STALE` to
`requireFreshArtifact`. `doc/verdict-contract.md` calls this case out.

### Validation

Every verdict is round-tripped through its own serializer and back through the host-side reader
(`VerdictReader.TryRead`) **before** it is written. A verdict that breaks the contract is
discarded with a `judge.contract` error and never reaches a report or a baseline — an unvalidated
judge verdict is worse than none. `EveryCommittedBaselineVerdictPassesTheShippedValidator` also
runs the committed baselines through `verdict_contract.py`, the validator that ships to
workspaces, so the two enforcement points cannot drift apart.

### Baselines

`baselines/judge/<agent>.json` records one verdict per fixture, committed for review:

```json
{ "Version": 1, "Agent": "qa-tester", "Judge": "deterministic",
  "Fixtures": [ { "Fixture": "dev-suite-fails-hard", "Verdict": { "schemaVersion": 1, … } } ] }
```

A judged verdict that differs from its recorded baseline is a `judge.baseline` error and fails the
run; re-record deliberately with `--update-baselines` and review the diff. Recording merges, so
judging one fixture never drops another fixture's entry.

Baselines record what the agents actually do, including where they fall short:
`dev-suite-fails-hard` reports its blocker honestly but proposes no way forward, so its baseline is
`FIX` at 80%, not a hidden pass.

### Real-LLM judge (costed, opt in, informational)

```bash
GIGACLAW_EVAL_ALLOW_REAL_CLI=1 GIGACLAW_EVAL_JUDGE_MODEL=… \
  dotnet run --project GigaClaw.Eval -- judge dev-fix-login-timeout --llm
```

`--llm` asks a real model to score the same stream against the same rubric and to answer in the
same verdict shape, through the same marker + fenced-json transport a reviewer uses. It is gated
twice — the flag plus `GIGACLAW_EVAL_ALLOW_REAL_CLI=1` — because it spends tokens.

An LLM verdict **is not reproducible and is not treated as if it were**:

- it is validated and rejected on the same terms as the deterministic one, and additionally must
  be bound to the replayed stream's `inputDigest`, name the fixture's agent, and judge the
  fixture's ticket — a reply that drifts off its input is discarded, not scored;
- it is recorded alongside the CLI binary, CLI version, requested and reported model, `MaxTurns`,
  and a digest of the exact prompt, because that is the metadata that explains why two runs differ;
- it is compared against the deterministic verdict within `LlmTolerancePercent` and the difference
  is **reported, never asserted** — its checks are `informational` and never set the exit code;
- it is never written to a baseline.

### Still out of scope

Monte Carlo mode — N repeated real-model runs with variance, a confidence interval and a hard cost
cap — remains its own slice. Nothing here samples a distribution: the deterministic judge has none,
and `--llm` runs exactly once per fixture.

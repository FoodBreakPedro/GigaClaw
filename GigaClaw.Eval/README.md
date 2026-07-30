# GigaClaw agent eval

Two layers share one CLI:

- `static` (default verb) — reads the committed catalog and `ProjectTemplate/` and never
  runs an agent.
- `replay` — dispatches an agent against a canned ticket through the mock `claude` CLI and
  captures the stream it produced.

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

Scoring the *quality* of a reply (the LLM judge) and sampling variance across repeated
non-deterministic runs (Monte Carlo) are separate slices. Replay only captures the stream
and checks it mechanically.

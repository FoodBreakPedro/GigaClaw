# R8 Evidence — Codex CLI harness

Date: 2026-08-05

R8 adds `CodexRunner : IAgentRunner` and keeps Claude as the default. A member selects the typed `Claude` or `Codex` harness in project settings; `GIGACLAW_AGENT_HARNESS=codex` is the instance-wide outage override.

## Parity result

| Capability | Result |
|---|---|
| Dispatch and JSONL streaming | `thread.*`, `turn.*`, and `item.*` events normalize into `AgentRun` events |
| Session continuation | Codex thread ids persist in a provider-namespaced `SessionRegistry` key; failed resumes restart cleanly |
| Queued steering | Messages received after stdin closes are replayed through `codex exec resume` |
| Usage and cost | Input, cached input, cache-write, and output tokens accumulate; priced USD cost is explicitly unavailable |
| Policy enforcement | `UserPromptSubmit` and `PreToolUse` Codex hooks use the existing authenticated loopback policy transport and fail closed if unacknowledged |
| Process containment | Shared concurrency gate, cancellation, wall-clock timeout, terminal-event exit grace, process-tree kill, and Windows job object |
| Model routing | Explicit GPT ids pass through; known Claude tiers map to Luna/Terra/Sol; unsupported values fail before process launch |
| Hermetic execution | `GigaClaw.CodexMock` and a committed real JSONL fixture cover success, failure, resume, usage, and parser behavior |

## Verification

- Real CLI fixture captured from `codex exec --json --ephemeral --sandbox read-only --model gpt-5.6-sol`.
- Live `CodexRunner` integration: authenticated `gpt-5.6-sol` run completed with hooks, streaming, thread id, and usage.
- Focused R8 runtime, lifecycle, policy, routing, and member tests: 69 passed, 0 failed.
- Full `GigaClaw.Core.Tests` suite: 1,484 passed, 0 failed.
- Full `GigaClaw.Eval.Tests` Release suite: 45 passed, 0 failed.

The live test is opt-in with `GIGACLAW_LIVE_CODEX=1`; ordinary CI remains hermetic and does not consume Codex usage.

# R2 hook-transport validation

## Reproduce

```sh
dotnet build GigaClaw.ClaudeMock/GigaClaw.ClaudeMock.csproj -c Release
dotnet test GigaClaw.Core.Tests/GigaClaw.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PolicyHookTransportBenchmarkTests" --logger "console;verbosity=detailed"
```

The benchmark times the complete warm in-process path: `HttpClient` POST, loopback
TCP HTTP parsing, Claude hook-input adaptation, `ContractPolicy.Evaluate`, and the
explicit `{}` HTTP response. It does not time a direct evaluator call.

## Recorded result

- Date: 2026-07-30
- Environment: Darwin 25.5.0 arm64, macOS 26.5.2, .NET SDK 10.0.302, Release build
- Transport: run-scoped HTTP server bound only to `127.0.0.1`, ephemeral port,
  random per-run path
- Warmup: 50 sequential requests
- Measured samples: 500 sequential requests
- Target: warm p95 at or below 50 ms
- Independent run: p50 0.128 ms, p95 0.181 ms
- Post-wiring verification run: p50 0.184 ms, p95 0.316 ms
- Result: target passed with more than 49 ms p95 headroom

The benchmark remains an asserted test so later parser, transport, or evaluator
changes fail visibly if warm p95 exceeds the rollout target.

## Settings acknowledgement and shadow semantics

Each runner attempt receives one schema-validated `--settings` file containing:

- `UserPromptSubmit`: acknowledgement to the run-scoped endpoint on every print
  turn;
- `PreToolUse`: observation for `Write`, `Edit`, `NotebookEdit`, `Bash`,
  `WebFetch`, and `WebSearch`.

The bridge always returns HTTP 200 with `{}`. A policy `Warn` or `Block` is
therefore an observation in R2, never a Claude tool denial. Because Claude HTTP
hook transport errors fail open, a completed subprocess with no valid
acknowledgement is converted to a visibly failed GigaClaw run.

Violations are emitted as structured `policy-violation/v1` run events before the
terminal run snapshot is persisted. Runs with no violations emit no policy event.

## Shared ticket-comment handoff

This lane does **not** claim ticket-comment receipt persistence. `ClaudeRunner`
does not own `TicketService`, and adding a runner-to-ticket mutation creates a
shared orchestration boundary with lane CL. The committed R2 result is queryable
run-log persistence only (`persistence: "run-log"` in every receipt).

At the CL/CX sync point, add a single post-run receipt writer outside the runner:

1. consume the run's `policy-violation/v1` events after terminal completion;
2. append one ticket comment only when at least one event exists;
3. preserve `runId`, agent, tool, target, decision, and reason;
4. use an idempotency marker keyed by `runId`;
5. do not trigger a comment for a clean run.

Until that shared writer lands, the R2 ticket-comment acceptance criterion remains
explicitly unmet.

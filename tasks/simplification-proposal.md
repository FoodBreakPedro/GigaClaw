# Simplification proposal — 2026-08-02

Propose-first pass. Nothing applied yet. Four areas, ranked by impact within each. Every item preserves behavior unless marked as a policy change.

## A. Token burn — evaluator / committer / system-watchdog

All three dispatch the real `claude` CLI, and each firing is actually **two** LLM runs: the main run plus a `consolidateAgentMemory` follow-up that is itself a full claude pass (`ActionExecutor.ExecuteConsolidateAgentMemoryActionAsync` → `_runner.RunAsync`).

| Automation | Trigger | Main run | Consolidation run |
|---|---|---|---|
| system-watchdog | **hourly cron, unconditional** | Haiku, ≤40 turns | **default model** (not pinned) |
| committer | every ticket → Done | **Sonnet 4.6**, ≤50 turns | default model |
| evaluator | every ticket → Done | Haiku, ≤50 turns | default model |

So an idle project still burns ~48 LLM runs/day (watchdog × 2), and every Done ticket costs 4 runs. Across projects and weeks, that is the "thousands of runs."

Proposed changes (policy changes, template + one spec field):

1. **`ConsolidateAgentMemoryActionSpec` has no `Model` property** (`AutomationConfig.cs:516`) — consolidation always runs on the CLI default model, likely your most expensive one. Add `Model` to the spec, thread it through `ClaudeRunContext` (currently hardcoded `Model = null` at `ActionExecutor.cs:1211`), pin `claude-haiku-4-5` in the template. Cheapest, highest-leverage fix.
2. **Watchdog cadence**: hourly → daily, or replace the probe with a `runCommand` health check that only dispatches the LLM agent when the check fails. Drop `consolidateAgentMemory` from the watchdog chain entirely — a health probe generates no lessons worth a 30-minute-budget consolidation pass.
3. **Gate consolidation on signal**: skip when `BuildEventsSummary(parentRun)` is empty or trivially short, instead of only skipping on negative exit codes.
4. **Committer on Sonnet ≤50 turns** for a commit-message job — Haiku with ~10 turns is plausible. Verify against a few real committer transcripts first.
5. **Cost visibility**: `AutomationEngine` calls `PurgeOld(24h)`, which also deletes the run JSON that `CostLogReader` reads — you can never see more than 24h of spend. Persist a small append-only cost ledger before purge, so questions like this one are answerable from data.

## B. CI time

1. **`GigaClaw.Core.Tests/xunit.runner.json` sets `parallelizeTestCollections: false`** — all 1,214 tests run serially. This predates the rebrand; the apparent reason is `MockClaudeBinFixture` setting a process-global env var. But the 15 mock-dependent classes already share the `"MockClaude"` collection (serialized within itself regardless). Proposal: delete the setting, run once locally per OS, watch for TempDir/env-var races. Likely the single biggest CI-time win — probably minutes per leg, ×3 legs.
2. **One solution build instead of five.** The Build step runs 5 sequential `dotnet build` invocations with overlapping dependency graphs. `dotnet build GigaClaw.slnx -c Release` builds everything once.
3. **Catalog steps build Debug three times.** `dotnet run --project GigaClaw.Catalog` (generate, `check --strict`, `check --strict-packs`) each trigger a Debug-config build even after the Release build. Use `-c Release --no-build` (after moving the build step earlier), and consider merging the two check invocations into one.
4. **No NuGet cache.** Add `cache: true` to `setup-dotnet` — saves restore time on all 3 legs.
5. **Matrix scope (optional policy change).** The 3-OS matrix is documented as load-bearing for build+test — keep it. But the catalog drift check and static eval are deterministic file/JSON checks; the CRLF class of bug is already caught by the dedicated normalization step. Running those on ubuntu only removes 2 legs × 2 steps.

Not proposed: cutting tests. The count isn't the problem; serialization and redundant builds are.

## C. Agent navigability (graphify-assisted debugging)

1. **`ActionExecutor.cs` — 2,402 lines, 58 methods, god node (95 edges).** It already has 10 clean region seams. Split into partial classes along them: `ActionExecutor.MergeQueue.cs` (R6), `.HttpRequest.cs`, `.Leases.cs` (R4), `.Worktree.cs` (R5), `.WorkflowWalk.cs` (C5). Pure file moves, no behavior change; graphify communities will map 1:1 to files afterward.
2. **Next tier, same treatment when touched** (don't do speculatively): `TicketService` (1,313), `LocalMediaJobService` (1,170), `PolicyHookTransport` (1,109), `ClaudeRunner` (1,056), `ContractPolicy` (1,025).
3. **Root clutter confuses agent navigation**: three "Ecosystem Comparison" MDs (~100 KB), `.obsidian/` (3.4 MB), `wiki/`, `tasks/`, plus both `doc/` and `docs/`. Move research notes into `docs/dev-notes/` (or out of the repo) and fold `docs/` into `doc/`. Every root file is context an agent reads before finding the code.
4. **Doc drift**: CLAUDE.md says run logs live at `runs/<run-id>/` (directory per run); `RunLogStore` writes flat `runs/<runId>.json`. One-line fix.

## D. Junk-file generators

1. **`graphify-out/` is 110 MB tracked in git and grows daily.** Dated snapshot dirs (`2026-07-30/` … `2026-08-02/`, 14–17 MB each) and `cache/` (36 MB, 967 files) are committed; the post-commit hook regenerates after every commit. The repo gains ~15 MB per active day forever. Proposal: track only `graph.json`, `GRAPH_REPORT.md`, `manifest.json`; gitignore `cache/` and the dated snapshots (or cap retention to N days via the hook). Optionally rewrite history later to reclaim the 110 MB — separate decision.
2. **`.gate-*.out` / `.judge-*.err` scratch** — written into each worktree by the TDD gate, gitignored but never deleted. Add cleanup when the gate completes, or redirect into the run's data dir (which already has 24h purge).
3. **`artifacts/eval/**`** — per-agent judge outputs accumulate locally on every eval run (gitignored, unbounded). Overwrite per agent or prune at eval start.
4. `runs/` is fine — 24h purge exists (see A5 for the cost-data side effect).

## Suggested order

| Step | Items | Risk |
|---|---|---|
| 1 | A1, A2 (token bleed) | low — template + one spec field |
| 2 | D1 (graphify git bloat) | low — .gitignore + hook flag |
| 3 | B2, B3, B4 (CI builds/cache) | low — workflow only |
| 4 | B1 (test parallelization) | medium — needs 3-OS verification |
| 5 | C1 (ActionExecutor partials) | low-medium — mechanical, big diff |
| 6 | A3–A5, B5, C3, D2, D3 | low, as touched |

Each step lands as its own commit(s), tests green between steps.

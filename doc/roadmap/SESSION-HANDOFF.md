# Session handoff — 2026-07-31 (addendum 2026-08-01)

Written for whoever picks this up next. Everything below was read from the tree, not from a report.

## Addendum — 2026-08-01: C5, C7, C8 landed on a PR branch

This session ran as a background job that cannot push `main`; unlike 2026-07-31's per-task merges, the work sits on branch **`worktree-c5-c7-c8`** as a draft PR for the owner to merge. Eight feature commits plus one merge commit:

- **C5** — `2ade596` (`parallelRunAgents`: inline branches translated to an ad-hoc `TeamDefinition` and driven through the C4 machinery — fan-out, joins, cancellation, restart-resume inherited, branches dispatched as ordinary sub-tickets so `RunConcurrencyGate` and the R4 leases apply; plus `MaxConcurrency`/`PartialFailure` on the team model) and `ab268a1` (typed `WorkflowGraph` at `.agents/workflow.json`, validated inside `AutomationStore.LoadAsync` — unreachable states and gate-less cycles rejected exactly like a malformed `automations.json`. **Declared and validated, not executed** — the walker is future work, stated in `doc/workflow-graph.md`).
- **C7** — `394cdb3` / `19209c2` / `59e40fe` (merge `17aad77`): labeled issue import with an idempotent mapping table, owner PR comments re-dispatching the assignee through C3's dispatch-injection rail, CI check status as a gitCommit-family trigger with restart-surviving dedupe in `SessionRegistry`. PAT settings-only (containment proven both directions), every call behind the P3 gate. No Blazor settings UI (REST-only, deliberate).
- **C8** — `9cd8732` / `9fd04e0`: `parallel-review` and `hypothesis-debug` — the first built-in teams with real task graphs. Finding dedup (`FindingDeduplicator`) feeds the synthesizer a per-lane-attributed deduped view and posts a `verdictIs`-consumable receipt; arbitration is host-enforced (`ArbitrationReader` closes losing lanes itself). Specialist lanes bind to real core agents (`ui-auditor`, `qa-tester`, `producer`) with reserved role IDs until the Security / Architecture & Data / Incident & Debug packs land — `packs-and-later.md` already plans `performance-engineer` "joins parallel-review as an optional lane". Prompts are minimal placeholders pending GM G5.
- Found and fixed in passing: `Automations.razor`'s `DescribeAction` had no arm for `startTeamRun` or `enqueueMerge` — the Automations page crashed on open for any project using them; now covered by an attribute-driven coverage test.

**Verification on the branch head** (each suite re-run by the orchestrator, not taken from subagent reports): Core **1246/0**, Eval **39/0**, catalog `check --strict` **0** and `--strict-packs` **0**, `replay all` **38/38**, `judge all` **38/38 zero drift** (macOS; the Windows exemption below is untouched by this session).

Everything below this line is the 2026-07-31 state, kept intact.

## Where the work is

| | |
|---|---|
| **Branch** | `main`, pushed through `4082184`. No open lane branches — every task this session merged to `main` immediately after its own verification (owner's directive, after the multi-branch debugging of 2026-07-30). |
| **Prior state** | PR #9 (SP-2 close-out) and PR #10 (Windows install "bug", Spanish locale) both merged before this session started. |
| **Roadmap artifact** | https://claude.ai/code/artifact/b0c190ed-cd5d-4fd4-8114-fe20c256a78c — republish to that same URL or it mints a new one. |

### Verification on `4082184`

`GigaClaw.Core.Tests` **1131 / 0** · `GigaClaw.Eval.Tests` **39 / 0** · `catalog check --strict` **0, zero gap lines** · `catalog check --strict-packs` **0** · `Eval -- all` **0** (37 agents) · `Eval -- replay all` **38 fixtures / 38 pass** · `Eval -- judge all` **38 / 38, zero drift** (macOS/Linux; on Windows the judge-baseline test is exempted again — see below).

Re-run with the same commands as always (test both projects in `-c Release`; catalog `check --strict` and `--strict-packs`; eval `all`, `replay all`, `judge all`). In this sandbox, clean `bin`/`obj` before trusting incremental builds — clock skew makes MSBuild's up-to-date check lie, and it produced two false "green" runs this session.

## What landed this session (2026-07-31, owner away, per-task merges)

1. **Q3 confirmed + `models.json` header narrowed** (`d7bc965`) — auditor → Sonnet, threat-modeler → Opus; the blanket no-sub-Opus rule replaced by the real one (no security agent on Fable).
2. **`Eval -- all` summary honesty** (`90c7dfb`) — baseline errors now appear in the printed summary that drives exit 1.
3. **`{assignee}` single implementation** (`309b5ae`) — the contract test consumes `CatalogGenerator.ReadAutomations`; structural assertions preserved and drift between typed model and reader pinned.
4. **G6 baseline review recorded** (`d024ce8`) — three pass, threat-modeler was NEEDS-WORK; review at `GigaClaw.Eval/baselines/REVIEW-security-assurance-2026-07-31.md`.
5. **threat-modeler worked example fixed to BLOCK** (`fd39acb`) — SKILL, scenario, and fixture now pin the verdict token.
6. **R3 completed** (`2dc9c4b`) — `OutboundApprovalGate` wired into `ActionExecutor`; trust anchor `ApprovedOutboundHosts` in the owner's `settings.json`, read per-execution; dry-run + `outbound-denial/v1` receipts without it; CMS regression pair runs the real shipped automation.
7. **Pack replay fixtures execute** (`7fe6a45`) — `Replay.FixtureRoots` plural; pack agents resolve their Agents dir via `catalog.json`; first-ever run of the four security fixtures (all passed); first SHIP-path fixture (secrets-reviewer) so always-BLOCK can no longer pass.
8. **R4 landed** (`eea24db`) — durable `file_leases` table, transactional acquire-with-inline-reap, `FileLeaseReaper`, conservative glob-intersection, block/warn semantics, handoff `ownedFiles` as the leased scope.
9. **Core eval-fixture backlog closed** (`f7c0295`) — 27 new fixtures + scenarios, `design` family added, 27 judge baselines, catalog gap list now empty and the empty state pinned by test.
10. **R5 landed** (`d902ec5`) — `isolation: "worktree"` per runAgent action; `<workspace>.worktrees/ticket-<id>` on branch `ticket/<id>`; durable worktree state on the ticket; fail-closed on non-git workspaces; dirty/unmerged worktrees flagged, never deleted; leases provably not bypassed.
11. **Windows judge drift diagnosed AND fixed** (`e32f872`, `aed9f74`, `118496e`+`a12e113`) — see below.
12. **R6 landed** (`cd4317e`) — `enqueueMerge` action, durable `merge_queue`, `MergeQueueProcessor` (claim transaction doubles as restart recovery), rebase → integration command → ff-merge pipeline, `merge-bounced/held/completed/v1` receipts, `ApprovedMergeProjects` trust anchor. Opt-in vocabulary only until SP-3.
13. **R7 landed** (`607e821`) — `IAgentRunner` (one member: `RunAsync`), `ClaudeRunner` first implementation, all consumers on the interface, zero test edits.
14. **PolicyHookTransport shutdown race fixed** (`32ccd24`) — the Windows CI flake was an `ObjectDisposedException` escaping the accept loop during dispose; classifier tests pin the exact CI exception shapes.
15. **MergeQueueTests CRLF fix** (`4082184`) — test temp repos pin `core.autocrlf=false`; Git for Windows' default was rewriting merged checkouts.

## The Windows exemption: narrowed, disproven once, and back on

The judge-baseline drift got its first real evidence this session, and the honest state is: **still open, but much smaller.** The CI interrogation step showed 29/38 fixtures drifting with **only** `evidence[].ref`/`inputDigest` moving — scored text identical to the character. That implicated `Normalize`'s workspace scrub, a structural scrub landed (`aed9f74`) with failing-then-passing tests for the Windows path forms, and the exemption was removed (`118496e`) on an "observed green" — **which was false.** The interrogation step runs with `continue-on-error`, and such a step's *conclusion* always reads success; only its `outcome` or log tells the truth. The next runs proved the produced Windows digests are byte-identical before and after the scrub fix: **the leaked difference is not any representation of the workspace path.** The exemption is restored with that history in its reason string; the scrub fix stays (its tests are real; it just wasn't the mechanism).

What the next session inherits: the differing bytes live somewhere in the normalized stream that path scrubbing never touches, on 29 of 38 fixtures, with per-fixture stable digests. The cheapest next move is a normalized-stream dump printed by the test on drift, diffed against a committed macOS reference — line-level, not hash-level, evidence. And the meta-lesson is now paid for twice in this repo: **a diagnostic (or CI field) that reports something other than what it appears to report is worse than none.**

## Gate state

- **SP-1** — enforced; 31 agents block, `programmer`/`code-janitor` warn (both declare `**`).
- **SP-2** — closed.
- **SP-3** — R4 leases, R5 worktrees, and P4/T2 prerequisites all exist now. What remains before the gate: integration-test the combined semantics (cycle detection, lease expiry, join semantics, ownership conflicts failing closed *together*) and the owner's call to enable `enqueueMerge`/worktree isolation by default. This is the natural next session.
- **SP-4** — needs the U6 end-to-end demo (worktree→PR→CI→owner merge) and R8.

## Outstanding

- **R8 Codex harness** — now unblocked by R7, still gated on Codex CLI usage cap and real `codex exec --json` fixtures.
- ~~**C5 / C7 / C8** — unparked by R4/R5 landing; not started tonight (owner unparked R4–R7 explicitly; the C-lane items were not named).~~ Landed 2026-08-01 on the `worktree-c5-c7-c8` PR branch — see the addendum at the top.
- **Judge baselines for the 31 new fixtures were recorded, not reviewed** — same §9 posture the security four were in before G6. A G6-style review pass over the core judge baselines is cheap and closes the loop.
- **Manual Board drags to Done skip worktree cleanup** — R5 cleanup triggers only through `ActionExecutor`'s `moveTicketStatus`; a UI-path drag bypasses it. Small, known, documented in the R5 commit.
- **Judge-drift stream dump** — the one remaining Windows exemption needs line-level evidence: make the test print the normalized stream of one drifting fixture on failure and commit a macOS reference dump to diff against. The interrogation CI step is back in `ci.yml`; read its **log**, never its conclusion.
- **MergeQueueTests CRLF fix (`4082184`)** — its Windows leg passed in run 30673464742's Core.Tests (1131/0 there; the run's only red was the re-blocking judge test). Confirmed.

## Hard-won lessons, appended

Previous sessions' lessons stand (read the tree; conservation checks; check what a subagent branched from; a clean textual merge is not a clean merge; stage explicitly; a diagnostic that misattributes is worse than none). New this session:

- **Instrument before you fix, then fix without ceremony.** The judge drift sat exempted for months; one non-blocking CI step with honest diagnostics turned it into a 3-commit fix within the same evening. The interrogation-step pattern (run the skipped thing, `continue-on-error`, print the diff) is reusable for any long-lived exemption.
- **Every new green-path test that touches git checkouts on Windows will meet `core.autocrlf`.** Twice now (PR #10's install "bug", tonight's MergeQueueTests). Any test that bare-inits a repo must pin `core.autocrlf=false` or ship `.gitattributes`.
- **Piped verification is unverified.** A `dotnet test | tail` chain let a broken build reach `main` (`118496e`) because tail's exit code masked the failure; fixed one commit later, but the pattern to keep is: assert on the test output, not the pipe's exit.
- **This sandbox's clock skew defeats incremental builds.** Two subagents independently hit stale-binary false greens. Clean `bin`/`obj` before any verification that matters.

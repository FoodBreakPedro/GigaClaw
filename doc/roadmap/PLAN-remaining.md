# Plan — remaining roadmap work (2026-08-01)

Written after PR #11 (C5 + C7 + C8) merged to `main` as `d1cce5d`. Derived from the lane docs and the tree, not from reports. Item IDs refer to the reconciliation line items; per the decision log, this directory is the plan home and tickets are created manually as work starts.

R8 is the only item *blocked on something external* (the Codex usage cap). Everything else below is open work with a clear owner and no external blocker.

## 1. SP-3 gate — the next session (CX-R + CL, owner decision at the end)

The prerequisites (R4 leases, R5 worktrees, R6 queue, P4 edges, C4/C5 joins) all exist; what SP-3 requires is proving their **combined** semantics, then the owner's default-on call.

1. ~~**Integration suite**~~ **Done 2026-08-01** (`225ef26`): `Sp3GateTests` — 10 scenarios green in Release and Debug, every refusal receipted. Evidence: [`SP3-EVIDENCE.md`](SP3-EVIDENCE.md).
2. ~~**Owner decisions — the only part of SP-3 still open**~~ **Decided 2026-08-01**: enable `enqueueMerge` by default? worktree isolation by default? Both **yes, derived from the contract, not the ticket type**: dispatch automations whose agent's `allowedWriteGlobs` (`.agents/contracts.json`) touch code/build paths run isolated and land through the merge queue; content/media/SEO/research agents stay in-place under R4 leases (non-git workspaces and ticketless firings fail closed under isolation; content pipelines need write→review→revise visibility without a merge hop; leases already serialize genuinely conflicting content writes). Precondition: the F1 merge/lease hold interlock and F2 write-once denial receipts, fixed on this branch. Wired into `ProjectTemplate/Agents/automations.json` (`programmer`/`qa-tester` dispatches run isolated; `verdict-gate-qa-ship-to-done` enqueues the merge at ticket acceptance), pinned by `TemplateAutomationContractTests`. The suite's two findings that bore on the call: **F1** — the merge queue did not consult file leases (a merge could land under a live lease; pinned by a characterization test), and **F2** — a lease denial re-wrote its receipt on every poll (R6 writes `merge-held/v1` once; R4 had no such guard — noise, not correctness). Both write-ups in the evidence doc.
3. ~~**Board-drag bypass**~~ **Fixed 2026-08-01** (`3b6b108`): one cleanup implementation at the `TicketService` status-transition choke point; UI-path regressions added; `ActionExecutor`'s private copy deleted.

## 2. Verdict close-out — A11's GM half (GM + CL)

4. ~~**G2**~~ **Was already done** — this plan inherited a stale lane status (owner caught it 2026-08-01). `a9a2e2e` + merge `2e6405f` landed the rewrites on `main`; four reviewers (`blog-reviewer`, `ui-auditor`, `qa-tester`, `local-media-reviewer`) emit `GIGACLAW-VERDICT` in the shipped template.
5. ~~**C2 wiring**~~ **Was already done** — the shipped template carries `verdict-gate-{qa,ui,blog,media}-*` plus the `content-verdict-gate-*` family and repair rounds. **Open sliver:** `evaluator` (the fifth reviewer) emits no `GIGACLAW-VERDICT` marker — check whether that is intentional (it may report through the eval layer rather than tickets) or unfinished; folded into item 12b's slice.

## 3. Cheap standing debts (any session, mostly CX-T)

6. ~~**Windows judge drift** stream dump~~ **Done 2026-08-01** (`6f5060c`): drift now prints a unified diff of the normalized stream against 38 committed macOS references (byte-reproducible, proven by double generation) on both the test path the CI interrogation step runs and the `judge` CLI path. Digest/normalization/exemption untouched; three dead ends ruled out in the commit. **Next Windows CI run's interrogation LOG localizes the drift to lines — read the log, never the conclusion.**
7. ~~**G6-style review of the 31 unreviewed core judge baselines**~~ **Done 2026-08-01** (`f5bbd7a`): the real population was **37 files / 38 entries** (the "31" undercounted; the six T5-era baselines were also unreviewed and five produced findings). Tally 28 PASS / 10 NEEDS-WORK / 0 FAIL, all recorded-only in [`REVIEW-core-2026-08-01.md`](../../GigaClaw.Eval/baselines/REVIEW-core-2026-08-01.md) — every remedy needs an authoring decision. **New follow-up item 7b**: act on the review — the biggest are the default rubric's `"TODO"` forbidden-token matching the `Todo` board column (dings 32 baselines), four 100/100 SHIPs that break their own SKILL's receipt rules, wrong-instrument rubrics (blog-writer, qa-tester), zero BLOCK verdicts / veto codes exercised by any baseline. Each fix requires re-recording baselines deliberately. **The ten decisions are costed and batched in [`BASELINE-DECISIONS.md`](BASELINE-DECISIONS.md) — answer that sheet before any re-record.**
8. ~~**T2 drift-script retirement**~~ **Done 2026-08-01** (`a488e16` + `07c40fb`): `GigaClaw.Catalog -- check --project <workspace>` / `--projects <root>` replaces the shell script with the same parity contract (automation-id granularity, `automation-overrides.json` allowlist, non-zero on drift) extended to every file `AgentsTemplateService` writes; script deleted, every live reference updated (historical snapshots deliberately left).

## 4. C5 follow-up — workflow-graph walker (CL, after SP-3)

9. ~~The graph is declared and validated but nothing executes it~~ **Done 2026-08-01** (`94bb269`, `d30ed90`, `03a5f73`): `WorkflowWalker` executes the validated graph — opt-in via a `startWorkflow` action (all editor/palette/API surfaces), polled beside team-run reconcile, task states dispatched through the ordinary path (leases/gate/isolation apply), fanOut/join over the C4/C5 machinery, gates through `ConditionSpec` with `verdictIs` outcomes as arm labels. **No stored walker state**: every traversal is a `GIGACLAW-WALK v1` receipt comment replayed on each pass; double-dispatch prevented by deterministic sub-ticket/run keys; `maxCycles` exhaustion escalates with the walk history; undecidable transitions park to Blocked, receipt first. A terminal state writes its `finished` receipt and deliberately owns no board policy (an automation watches the receipt). Runtime semantics in `doc/workflow-graph.md`.

## 5. GM volume (parallel to everything above)

10. ~~**G3** handoff templates~~ **Was already done** (`f48360f` — stale lane status, caught by the owner 2026-08-01).
11. ~~**G4** progressive disclosure~~ **Was already done** (`fdc1a50`, five largest skills).
12. ~~**G5** team-preset prose~~ **Was already done as staged drafts** (`4fce091`): six agents (`accessibility-reviewer`, `architecture-reviewer`, `coverage-reviewer`, `performance-reviewer`, `hypothesis-investigator`, `debug-lead`) with SKILL.md + memory stubs and a `BINDINGS.md` manifest sit in `doc/drafts/g5-team-presets/` — authored but NOT installed as template agents, which is why C8's presets still run on stand-ins.
12b. **NEW — promote the G5 drafts** (CL, next slice): install the six agents into `ProjectTemplate/Agents/` per `BINDINGS.md`, give each the full five bindings (contract + model + team + automation + eval fixture — the T6 gate now enforces this), swap the C8 preset roles off the stand-ins (one-line `teams.json` role-ID edits by design), un-defer the performance/architecture/accessibility lanes in `parallel-review`, and settle the `evaluator` verdict question from item 5.

## 6. Phase 3 completion → SP-4 (CX-R)

13. ~~**U6 end-to-end demo**~~ **Proven hermetically 2026-08-01** (`73c261b` + `2d962b0`, [`U6-EVIDENCE.md`](U6-EVIDENCE.md)): one ticket worktree → PR (new `GitHubPullRequestService`, C7 patterns: PAT settings-only, P3-gated, `github-pull-request/v1` receipt, idempotent by asking GitHub) → CI (`gitHubCheckStatus` fires `enqueueMerge` — first proof of C7×R6 composing) → owner-gated ff merge, plus failure and restart legs. Real git/queue/triggers, faked GitHub HTTP only. ~~New small follow-ups it surfaced~~ **All three closed 2026-08-01** (`11bd29a`, `39dbd3c`, `bcc313a`): (a) the template ships `github-ci-success-enqueues-merge`, `github-ci-failure-records-check`, and `verdict-gate-qa-ship-open-pull-request` — **wired but `enabled: false`** (owner-decided: off unless a project opts in, consistent with local-first); (b) `openPullRequest` is a full automation action on every surface, failing closed with a note when the project has no GitHub config; (c) `{checkName}`/`{checkConclusion}` render in comments, and the previously weakened U6 failure-leg assertion is restored to prove it.
14. **R8 Codex harness** — *when the usage cap lifts*: second `IAgentRunner` implementation against real `codex exec --json` fixtures, parity checklist from P13 (streaming/resume/policy for one agent, usage/cost or explicit unsupported).

## 7. Phase 4 — packs (CX-T infra, GM volume, CL wiring)

15. ~~**T6 completion**~~ **Done 2026-08-01** (`d25474d`, `d2b0384`, `546daf6`, `75a4529`): the survey found most of the C9 spec already implemented (staged install/rollback, lock hashes, hash-guarded uninstall, quarantine, five-binding gate in CI); the four real gaps are closed — packs now embedded in `GigaClaw.Core.dll` (the production install path), the per-project drift checker no longer misreads an installed pack as drift (lockfile-vouched, falsified both ways), a 12-test real-Security-pack install→verify→uninstall round trip, and teams-as-data proven end-to-end for a pack team — the 2026-07-30 structural finding is closed in `doc/roadmap/index.md`.
16. **Packs in order** (`packs-and-later.md`): Security Assurance proves the infra (its fixtures already execute), then Incident & Debug (real `hypothesis-investigator`/`debug-lead` into the C8 preset), Architecture & Data (`performance-engineer` as the planned optional parallel-review lane), Language specialists, P7/P12 registries, then Marketing.

## 8. Post-SP-4 pilots

17. P16 semantic memory, O3/O4 model routing, O5 — shadow mode, individually gated on O6 evidence, per the decision log.

## Suggested session slicing

| Session | Contents | Exit proof |
|---|---|---|
| Next | §1 SP-3 suite + Board-drag fix + owner call | Combined integration tests green in Release; decision recorded in the log |
| +1 | §2 G2/C2 close-out, §3 items 6–7 | Five reviewers verdict-gated on the debug instance; drift dump committed |
| +2 | §4 walker (+ §5 lands in parallel via GM) | A ticket walks a declared graph end-to-end hermetically |
| +3 | §6 U6 demo (R8 if unblocked) → SP-4 | The one-ticket end-to-end flow recorded |
| Then | §7 packs, §8 pilots | Pack 1 installed/uninstalled cleanly under the five-binding gate |

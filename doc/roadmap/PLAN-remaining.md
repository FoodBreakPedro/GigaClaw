# Plan — remaining roadmap work (2026-08-01)

Written after PR #11 (C5 + C7 + C8) merged to `main` as `d1cce5d`. Derived from the lane docs and the tree, not from reports. Item IDs refer to the reconciliation line items; per the decision log, this directory is the plan home and tickets are created manually as work starts.

R8 is the only item *blocked on something external* (the Codex usage cap). Everything else below is open work with a clear owner and no external blocker.

## 1. SP-3 gate — the next session (CX-R + CL, owner decision at the end)

The prerequisites (R4 leases, R5 worktrees, R6 queue, P4 edges, C4/C5 joins) all exist; what SP-3 requires is proving their **combined** semantics, then the owner's default-on call.

1. ~~**Integration suite**~~ **Done 2026-08-01** (`225ef26`): `Sp3GateTests` — 10 scenarios green in Release and Debug, every refusal receipted. Evidence: [`SP3-EVIDENCE.md`](SP3-EVIDENCE.md).
2. **Owner decisions — the only part of SP-3 still open**: enable `enqueueMerge` by default? worktree isolation by default? The suite added two findings that bear on it: **F1** — the merge queue does not consult file leases (a merge lands under a live lease; pinned by a characterization test; the fix — wait vs bounce — is a design choice coupled to this decision), and **F2** — a lease denial re-writes its receipt on every poll (R6 writes `merge-held/v1` once; R4 has no such guard — noise, not correctness). Both neutral write-ups in the evidence doc.
3. ~~**Board-drag bypass**~~ **Fixed 2026-08-01** (`3b6b108`): one cleanup implementation at the `TicketService` status-transition choke point; UI-path regressions added; `ActionExecutor`'s private copy deleted.

## 2. Verdict close-out — A11's GM half (GM + CL)

4. **G2**: resolve the two outstanding rubric findings, merge `lane/gemini-vol`. (CL review already cleared receipt-chain, cycle-counter, evaluator-transport.)
5. **C2 wiring**: as each rewritten reviewer lands, wire its `verdictIs` gate into `ProjectTemplate/Agents/automations.json` (the edit deliberately deferred since C2 — wiring earlier would Block every ticket).

## 3. Cheap standing debts (any session, mostly CX-T)

6. ~~**Windows judge drift** stream dump~~ **Done 2026-08-01** (`6f5060c`): drift now prints a unified diff of the normalized stream against 38 committed macOS references (byte-reproducible, proven by double generation) on both the test path the CI interrogation step runs and the `judge` CLI path. Digest/normalization/exemption untouched; three dead ends ruled out in the commit. **Next Windows CI run's interrogation LOG localizes the drift to lines — read the log, never the conclusion.**
7. ~~**G6-style review of the 31 unreviewed core judge baselines**~~ **Done 2026-08-01** (`f5bbd7a`): the real population was **37 files / 38 entries** (the "31" undercounted; the six T5-era baselines were also unreviewed and five produced findings). Tally 28 PASS / 10 NEEDS-WORK / 0 FAIL, all recorded-only in [`REVIEW-core-2026-08-01.md`](../../GigaClaw.Eval/baselines/REVIEW-core-2026-08-01.md) — every remedy needs an authoring decision. **New follow-up item 7b**: act on the review — the biggest are the default rubric's `"TODO"` forbidden-token matching the `Todo` board column (dings 32 baselines), four 100/100 SHIPs that break their own SKILL's receipt rules, wrong-instrument rubrics (blog-writer, qa-tester), zero BLOCK verdicts / veto codes exercised by any baseline. Each fix requires re-recording baselines deliberately.
8. **T2 drift-script retirement**: needs a per-project equivalent first (the script checks initialized projects against the template; catalog checks don't).

## 4. C5 follow-up — workflow-graph walker (CL, after SP-3)

9. The graph is declared and validated but nothing executes it (stated in `doc/workflow-graph.md`). Build the walker over the C4/C5 runtime: state transitions driven by ticket movement, gates evaluated through the ordinary `ConditionSpec` path, `maxCycles`/visited-roles enforced at run time. Sequenced after SP-3 so the join semantics it composes are certified first.

## 5. GM volume (parallel to everything above)

10. **G3** handoff templates (unblocked by C6 since 2026-07-31).
11. **G4** progressive disclosure on the seven largest skills.
12. **G5** team-preset prose: the C8 presets run today on stand-in agents (`ui-auditor`, `qa-tester`, `producer`) with placeholder prompts; G5 authors the real investigator/lead/reviewer prose. Specialist swap-in stays a one-line `teams.json` edit per reserved role ID.

## 6. Phase 3 completion → SP-4 (CX-R)

13. **U6 end-to-end demo**: one ticket worktree → PR → CI → owner merge, using R5/R6 plus C7's `gitHubCheckStatus` trigger (the CI-status leg now exists).
14. **R8 Codex harness** — *when the usage cap lifts*: second `IAgentRunner` implementation against real `codex exec --json` fixtures, parity checklist from P13 (streaming/resume/policy for one agent, usage/cost or explicit unsupported).

## 7. Phase 4 — packs (CX-T infra, GM volume, CL wiring)

15. **T6 completion** per the approved C9 spec: staged install/uninstall + `packs.lock.json`, quarantine-on-incompatibility, and the five-binding CI gate. Note: the 2026-07-30 structural blocker (teams as compiled constants) is materially changed — C4/C8 moved teams to data (`teams.json` + `TeamSeed`), so the team-binding rule should now be enforceable; verify and close that finding.
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

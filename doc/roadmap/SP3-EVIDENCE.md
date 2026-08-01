# SP-3 gate — evidence

**Date:** 2026-08-01 · **Suite:** `GigaClaw.Core.Tests/Integration/Sp3GateTests.cs` (10 tests) · **Branch:** `sp3/integration-gate`

SP-3 asks for one thing the per-feature suites cannot give: proof that P4 cycle validation, R4 file
leases, R5 worktrees, R6 the merge queue and C4/C5 joins **fail closed together, each with its
receipt**. Every scenario below runs the real services over a temp data directory — a real
`ActionExecutor` wired to a real `FileLeaseStore`, `MergeQueueStore`, `MergeApprovalGate` and
`TeamRunService`, real git repositories, and either the hermetic mock claude CLI or `ClaudeRunner`'s
`OllamaValidationError` fast-fail. No real `claude` CLI is spawned; nothing reaches the network.

Two claims are asserted for every refusal: **what did not happen** (no run registered, no edge
inserted, no file overwritten) and **the durable receipt that says why**.

Run these with:

```
dotnet test GigaClaw.Core.Tests -c Release --filter "FullyQualifiedName~Sp3GateTests"
```

---

## 1. Ownership conflict inside a parallel team run

### 1a — exactly one branch dispatches, the loser is serialized not lost

**Test:** `Contending_branches_serialize_exactly_one_dispatches_and_the_loser_gets_the_denial_receipt`

**What it proves.** A `parallelRunAgents` fan-out whose branches all declare `allowedWriteGlobs:
["src/**"]` dispatches exactly one lane. The winner's lease is taken on the ordinary dispatch path
(the runner's only concurrency slot is pinned from outside, so the winner parks on the gate *while
holding* the lease — the lease row is not hand-planted). The loser is refused before
`ClaudeRunner.RunAsync` is reached: no `AgentRun` is registered for its ticket, and it claims no
lease of its own. Once the winner's run completes and releases the lease, the identical dispatch
goes through — the refusal was a serialization, not a dropped lane — and the all-done join then
completes the run normally.

**Receipts observed.** `file-lease-denial/v1` on the loser's sub-ticket, with
`enforcementMode: "block"`, `scope: ["src/**"]`, and — the cross-lane part — `conflictingAgent` /
`conflictingTicketId` naming **the sibling branch's** agent and ticket. That is the assertion that
proves the lease is project-wide rather than per-ticket, which is the only reason declaring two
same-scope branches in one action is safe at all. Exactly one denial receipt exists after the
successful retry (the retry writes none).

### 1b — a refused lane is named in the synthesis, and the run fails with gaps

**Test:** `A_lane_refused_for_ownership_is_named_in_the_synthesis_and_the_run_fails_with_gaps`

**What it proves.** When the refused lane is *reported* as failed rather than retried, the join does
not paper over the ownership conflict. The all-done join fires, the synthesis brief names the
refused lane and carries its denial reason verbatim, and the run ends `Failed` — a synthesis
covering two of three lanes is not a green run.

**Receipts observed.** `file-lease-denial/v1` on the refused lane; the join's synthesis brief
containing `Lanes that reported (2 of 3)`, `Lanes missing (1 of 3)`, the refused agent slug, the
literal string `file-lease-denial/v1` as the lane's failure reason, and the standing instruction
`Do not present their subject matter as covered`; `TeamRunStatus.Failed` with the denial in
`FailureReason`.

---

## 2. Lease expiry mid-run — and conservative reaping

**Test:** `An_expired_lease_is_reaped_and_the_waiting_branch_proceeds_while_a_live_lease_is_never_stolen`

**What it proves.** Two leases are taken: 30 minutes on `src/**`, 90 minutes on `docs/**`. Both
contenders are refused while both are live. A single sweep with an injected clock 45 minutes later
(`FileLeaseReaper.ReapAllAsync` documents `now` as injected for exactly this determinism) reaps the
30-minute lease and **only** that one. The branch that had been refused then dispatches and takes a
lease of its own; the contender for the 90-minute lease is refused again. Nothing was stolen to make
room, and "expired" is a fact about the clock the sweep was handed rather than a sleep the test has
to race.

**Receipts observed.** `file-lease-denial/v1` naming `run-src-holder` before the sweep and none after
it; `file-lease-denial/v1` naming `run-docs-holder` both before and after; the reaper's own return
value containing exactly one lease (`run-src-holder`).

---

## 3. Dependency cycle across team-run sub-tickets

**Test:** `A_cycle_between_team_sub_tickets_is_refused_at_edge_creation_and_the_run_still_decides`

**What it proves.** A team run's ordering *is* the board's edges, so P4's recursive cycle check is
what protects it. Two refusal paths are exercised on a live run:

1. A hand-drawn back-edge (`security-lane` blocked by `dedup`, which already waits on
   `security-lane`) is refused at insert time.
2. A whole team task whose declared `DependsOn` would close a loop is refused *and rolled back* —
   `TeamStore.AddTaskAsync` removes the half-built row rather than leaving a node nothing waits on.

In both cases the run's real edges survive untouched, and the run is not deadlocked: the `dedup`
task the cycle attacked is released once both lanes report and the run reaches a decided outcome.

**Receipts observed.** `TicketDependencyException` with `Code == "dependency_cycle"` on both paths;
`ListBlockingTicketsAsync` empty for the ticket the refused edge targeted; `dedup.BlockedByTicketIds`
unchanged; no `loop-lane` task in `ListTasksAsync` after the rollback.

---

## 4. Merge-queue composition behind two worktree-isolated runs

### 4a — two candidates land one at a time behind the owner gate

**Test:** `Two_worktree_isolated_runs_enqueue_and_land_one_at_a_time_behind_the_owner_gate`

**What it proves.** Two tickets are dispatched with `isolation: "worktree"` through the production
path (worktree created and durably recorded on the ticket by `ActionExecutor`), commit on their own
branches, and reach the queue through the real `enqueueMerge` action. **Unapproved, nothing merges**:
both entries sit `Held`, `ProcessProjectAsync` returns null, and the workspace file is byte-for-byte
unchanged. After the owner edits `settings.json` — no engine restart — they land sequentially, the
second rebased onto the first, history linear (3 commits, no merge commit). The temp repos pin
`core.autocrlf=false`, the lesson `MergeQueueTests` paid for in `4082184`.

**Receipts observed.** `merge-held/v1` on the first hold; `merge-completed/v1` on both tickets after
approval; `MergeQueueState.Held → Merged` transitions; `git log --oneline` at 3 lines.

### 4b — disjoint leases do not prevent a real conflict; the queue bounces it

**Test:** `Disjoint_leases_do_not_prevent_a_real_conflict_and_the_queue_bounces_it_rather_than_overwriting`

**What it proves.** This is the composition claim that matters most for the owner decisions below.
File leases are **declarative** — they lease the scope an agent's contract *claims*, not the bytes it
actually writes. Two runs with provably disjoint lease scopes (`src/**` vs `docs/**`) are both
correctly permitted by R4, and both then commit to the same line of the same file. R4 is not wrong
here and is not the backstop; the merge queue is. The second candidate bounces to `Blocked` rather
than overwriting the first, and its worktree is left rebase-free rather than mid-conflict.

**Receipts observed.** No `file-lease-denial/v1` on either ticket (both dispatches were legitimately
permitted); `merge-bounced/v1` on the second with `cause: "conflict"` and `conflictingFiles`
containing `shared.txt`; ticket status `Blocked`; the workspace still holding ticket one's line;
`git status` in the loser's worktree reporting no rebase in progress.

### 4c — `enqueueMerge` is close to useless without worktree isolation

**Test:** `Enqueueing_a_merge_for_a_ticket_that_never_ran_isolated_bounces_with_a_no_worktree_receipt`

**What it proves.** The ordering between the two opt-ins the owner is deciding on. A ticket
dispatched *without* `isolation: "worktree"` has no recorded branch, so enqueueing a merge for it
bounces immediately rather than queuing a candidate that could never rebase — nothing is added to
the queue at all.

**Receipts observed.** `merge-bounced/v1` with `cause: "no-worktree"`; ticket status `Blocked`; an
empty merge queue.

---

## 5. Restart at the worst moment

**Test:** `A_restart_with_leases_held_a_join_undecided_and_the_queue_claimed_resumes_without_double_dispatch`

**What it proves.** Every service is thrown away and rebuilt over the same data directory with three
things in flight at once: a file lease held by a run the first process will never finish, a team run
whose join has not fired (one lane done, two open), and a merge-queue entry claimed `Merging` but
never completed. The second process resumes all three from disk:

- the lease is still active, still scoped `docs/**`, and still refuses the same branch;
- the pre-restart `file-lease-denial/v1` receipt is still on the ticket **byte for byte** (asserted
  by string equality against the receipt captured before the restart) — no lost receipts;
- re-firing the fan-out re-attaches instead of fanning out again (one run, three lanes, three
  child tickets — no double dispatch);
- the claimed merge is recovered and completed **exactly once** (one `merge-completed/v1`, and the
  next poll claims nothing);
- the join then fires in the new process with exactly one synthesis ticket ever created.

**Receipts observed.** Two `file-lease-denial/v1` comments where the first is identical to the
pre-restart capture; one `merge-completed/v1`; `MergeQueueState.Merging → Merged`; run counts and
child-ticket counts unchanged across the boundary; `TeamRunStatus.Joining` with 4 children total.

---

## 6. Everything at once

**Test:** `Ownership_conflict_a_refused_cycle_and_the_join_all_land_in_one_run_each_with_its_receipt`

**What it proves.** The case the gate is named for, in one run. Two contending lanes serialize (one
dispatches, one is refused with its receipt naming the sibling's ticket). *While that conflict is
live*, a back-edge that would deadlock the same graph is refused. A reaper sweep at the current clock
collects nothing — the live lease is not harvested just because something is waiting on it. The
refused lane then dispatches once the lease is released. The `dedup` task the cycle attacked is
released on schedule and the run joins and completes.

**Receipts observed.** `file-lease-denial/v1` with `enforcementMode: "block"` and
`conflictingTicketId` = the winning lane's ticket; `dependency_cycle` refusal with the target's
blocker list still empty; an empty reaper result with the live lease still listed; a single denial
receipt after the successful retry; synthesis brief `Lanes that reported (3 of 3)`;
`TeamRunStatus.Completed`.

---

## Open findings (not fixed — recorded, not papered over)

### F1 — the merge queue does not consult file leases

**Test:** `Characterization_the_merge_queue_does_not_consult_file_leases_and_lands_under_a_held_one`
(a characterization test: it asserts what the system does **today**, not what it ought to do).

`MergeQueueProcessor` takes no `FileLeaseStore` — see its constructor. The suite demonstrates a merge
landing into the main workspace checkout while an in-flight run holds an active `src/**` lease over
the very paths the merge rewrites, with no receipt on either side and no lease consulted or reaped.

This is harmless when every agent runs in its own R5 worktree, and materially not harmless when one
does not: **a dispatch without `isolation: "worktree"` executes *in* the workspace the merge is
rewriting.** It is recorded rather than fixed because interlocking the two is an owner decision, not
a bug fix — should a merge wait behind a lease, or bounce with a receipt? — and the answer is coupled
to both decisions below. If that test ever fails because a merge is refused or deferred, the
interlock has landed and the test should be replaced by an assertion of whichever semantics were
chosen.

### F2 — a lease denial is written on every refused attempt

Asserted inline in scenario 2 (`Assert.Equal(2, …)` denial comments after two refusals).

A blocked dispatch returns from `ExecuteRunAgentActionAsync` before `FinalizeAsync`, so the trigger
firing is never committed and a repeating `ticketInColumn` trigger retries it every poll — correct
(the lane is not permanently parked) but it accumulates one `file-lease-denial/v1` comment per poll
for as long as the conflicting lease lives. R6's `enqueueMerge` deliberately writes `merge-held/v1`
only on the **first** hold for exactly this reason; R4 has no equivalent guard. Noise on the ticket
the synthesizer will later read, not a correctness break. Left unfixed because suppressing repeat
receipts changes what the audit trail records, which is the owner's call.

### F3 — the Board-drag bypass is still open

`PLAN-remaining.md` §1 item 3 (UI drag to Done skips R5 worktree cleanup because it bypasses
`ActionExecutor.moveTicketStatus`) is **not** addressed by this suite and remains open. It is a UI
entry-point hole rather than a combined-semantics one, and nothing here should be read as evidence
that it is closed.

---

## Owner decisions (stated neutrally — not decided here)

### Decision 1 — enable `enqueueMerge` by default?

Today `enqueueMerge` is opt-in vocabulary: an automation has to name the action, and
`ActionExecutor` treats an unwired `MergeQueueStore` as a no-op.

*Evidence that bears on it:*

- **4b** is the strongest argument in favour: file leases are declarative and cannot see bytes an
  agent actually wrote, so two legitimately-permitted runs can still collide. Without the queue,
  that collision has no backstop at all. With it, the second candidate bounces to `Blocked` with a
  `merge-bounced/v1` receipt naming the file, and the first ticket's content survives.
- **4a** shows the blast radius is already gated independently: even with the action enabled, nothing
  lands until the project's slug is in the owner's `settings.json`. Default-on `enqueueMerge` would
  therefore enqueue by default but still **merge** only where the owner has opted in — the two
  switches are separable.
- **5** shows the queue survives a restart with exactly-once completion, so enabling it by default
  does not introduce a durability question.
- **F1** cuts the other way: more candidates flowing through the queue means more merges landing in
  the workspace, and the queue currently does not check whether a run holds a lease over what it is
  about to rewrite. The exposure scales with how many projects enqueue by default.
- **F3** is adjacent: a ticket dragged to Done in the UI does not go through the action path at all,
  so default-on would not cover that entry point.

### Decision 2 — enable worktree isolation by default?

Today `isolation: "worktree"` is per-`runAgent` opt-in; the default is in-place execution in the
project workspace.

*Evidence that bears on it:*

- **F1** is the strongest argument in favour: the concrete harm demonstrated (a merge rewriting files
  under a running agent) only exists for in-place dispatches. Worktree-by-default removes that class
  without needing the lease/queue interlock at all.
- **4c** shows the worktree path is already the prerequisite for `enqueueMerge`: a ticket with no
  recorded worktree bounces immediately with a `no-worktree` `merge-bounced/v1`. Default-on worktrees
  and default-on `enqueueMerge` are therefore ordered — the second is much less useful without the
  first.
- **1a/6** show isolation is *not* a substitute for the lease: the lease is keyed on logical scope,
  and two branches in separate checkouts still serialize correctly. Turning isolation on by default
  does not weaken R4 and does not remove the denial receipts.
- Against: every worktree is a real checkout on disk, `ActionExecutorWorktreeTests` already proves
  isolation **fails the dispatch closed** when the workspace is not a git repo — so default-on turns
  "project is not a git repo" from an irrelevance into a dispatch failure for every agent, and
  **F3** means the cleanup path can still be bypassed from the UI, so worktrees created by default
  could accumulate.

No recommendation is recorded here. Both decisions belong in the decision log in
[`index.md`](index.md) once the owner has made them.

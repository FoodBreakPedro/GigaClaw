# U6 end-to-end — evidence

**Date:** 2026-08-01 · **Suites:** `GigaClaw.Core.Tests/Integration/U6EndToEndTests.cs` (3 tests) +
`GigaClaw.Core.Tests/Github/GitHubPullRequestTests.cs` (18 tests) · **Branch:** `u6/end-to-end`

U6 is the **first half of the SP-4 gate** (`doc/roadmap/index.md`, `PLAN-remaining.md` §6 item 13):
*one ticket flows worktree → PR → CI → owner merge*. Every leg already had a unit suite — R5
worktrees (`WorktreeManagerTests`, `ActionExecutorWorktreeTests`), R6 the merge queue
(`MergeQueueTests`), C7 the GitHub surface (`GitHubCheckStatusTests`, `GitHubIssueImportTests`). What
none of them proved is the **join**: that a check-run conclusion arriving from GitHub actually
reaches R6's `enqueueMerge`, that the owner gate still stands between "CI is green" and "a commit
landed", and that a restart in the middle does not open a second pull request or enqueue the same
branch twice.

Run these with:

```
dotnet test GigaClaw.Core.Tests -c Release --filter "FullyQualifiedName~U6EndToEndTests"
dotnet test GigaClaw.Core.Tests -c Release --filter "FullyQualifiedName~GitHubPullRequestTests"
```

---

## What is real and what is faked

Being precise about this is the point of the exercise — an end-to-end proof made of mocks proves
nothing.

**Real.** Real git throughout: a real workspace repository, a real R5 worktree on a real
`ticket/<id>` branch created by the production `WorktreeManager`, a real `git push` into a real
local **bare repository** standing in for the remote, a real `git rebase` + `git merge --ff-only`
performed by `MergeEngine`, and real `git worktree remove` cleanup. Real services over a temp data
directory: a real `ActionExecutor` wired to a real `FileLeaseStore`, `MergeQueueStore`,
`MergeQueueProcessor` and `MergeApprovalGate`; the real `GitHubCheckStatusTrigger` polled exactly
the way `TriggerHandler` polls it (evaluate → `ConditionsMatchAsync` → `ExecuteAutomationAsync`);
the real `GitHubApiClient` with the real P3 `OutboundApprovalGate` built from the owner's
`settings.json`. Every gate is re-read from disk per call, so "the owner approved between two polls"
is literally a file edit between two calls.

**Faked — one thing only.** The GitHub **HTTP transport**. C7's `FakeHttpMessageHandler` is the
primary handler of the named `GitHubApiClient` client, so no test here can reach the network, and
every claim about "what GigaClaw sent GitHub" is asserted against the `HttpRequestMessage` the
client actually built (method, absolute path, and the JSON body it serialized).

**Not an agent.** The dispatched run is failed inside `ClaudeRunner` before any subprocess is
spawned (the `OllamaValidationError` fast-fail that `ActionExecutorWorktreeTests` uses), so the
commit on the ticket branch is authored by the test — exactly as `Sp3GateTests` authors its own. The
mock CLI writes no files; what is under test is the pipeline, not what an agent would have typed.

---

## The new capability: `GitHubPullRequestService`

C7 shipped **no push and no PR-create capability** — its client could read issues and check runs and
write issue comments, and that was all. U6 could not be built without one, so
`GigaClaw.Core/Github/GitHubPullRequestService.cs` was added, following C7's existing rules rather
than inventing new ones:

- **Settings-only PAT.** The token is read from `AppSettingsService` and reaches the outgoing
  `Authorization` header and nowhere else. `GitHubProjectConfig` gained two non-secret fields
  (`GitRemote`, `PullRequestBase`) and still carries no token member.
- **Every HTTP call through the P3 gate.** The service holds a `GitHubApiClient`, never an
  `HttpClient`, so the `OutboundApprovalGate` preflight cannot be bypassed.
- **The push is gated too.** A `git push` is outbound traffic even though it is not an HTTP call
  this process makes, so the remote's **host** is put through the same gate before git is invoked
  (evaluated as `https://<host>` so there is one host-approval rule, not a second weaker copy).
  `RemoteHost` understands URL remotes and git's scp-like `user@host:path` form. A remote with **no
  host** — a filesystem path, including the bare repository this suite pushes to — is not outbound
  traffic and is not gated; that is stated rather than hidden, and is directly under test.
- **Receipts.** A gate refusal writes the standard `outbound-denial/v1` receipt
  (`action: "gitPush"` or `action: "githubRequest"`); a created pull request writes a
  `github-pull-request/v1` ticket comment.
- **Idempotence without a new table.** Before creating anything the service asks GitHub whether a PR
  already exists for the branch (`GET /pulls?head=owner:branch&state=all`). "Do not open a second PR
  after a restart" is therefore a property of what GitHub says now, not of any state this process
  carries — the same reasoning `GitHubIssueSyncService` uses for its import pass.

Registered in `GigaClaw.Web/Program.cs` alongside the rest of the C7 surface. **No automation action
wires it yet** — see "What remains" below.

---

## Leg-by-leg

### Legs 1–4 — worktree → PR → CI → owner merge

**Test:** `A_ticket_flows_worktree_to_pull_request_to_green_ci_to_owner_merge`

One ticket, one pass, in order.

**1 — Worktree.** The ticket is dispatched through the production isolation path
(`runAgent` with `isolation: "worktree"`, the shape `assignee-dispatch-code` carries in the
template). Asserted: the ticket durably records `WorktreeBranch == "ticket/<id>"` and a
`WorktreePath` that exists on disk and is **not** the workspace, and the workspace's own copy of the
changed file is byte-for-byte untouched after the branch commit — isolation means the change is not
there yet.

**2 — PR.** `GitHubPullRequestService.OpenForTicketAsync` pushes the branch and opens the pull
request. Asserted: the **bare repository** resolves `refs/heads/ticket/<id>` to exactly the commit
SHA the worktree is at (real git, not a mock's opinion); the single POST the client sent went to
`/repos/acme/widgets/pulls` with `head = "ticket/<id>"` and `base = "main"` in the body it
serialized; a `github-pull-request/v1` receipt is on the ticket.

**3 — CI.** The polled checks API is C7's webhook-equivalent (GigaClaw is a local app behind
whatever NAT the owner is on, so C7 polls rather than listens). GitHub is re-scripted to report
`build / completed / success`, and `GitHubCheckStatusTrigger` is polled with `ref` pinned to the
ticket branch. Asserted: exactly one firing, **bound to the ticket** — resolved from the commit
message's `ticket-<id>` reference, with no second API call — and the request URL carries the
percent-escaped branch and ends in `/check-runs`. The firing then drives the automation's action
chain through `ActionExecutor`, which is the composition U6 exists to prove: **C7's trigger reaching
R6's `enqueueMerge`.**

**4 — Owner merge.** Asserted **held-before / landed-after**:

- *Before approval:* one queue entry, state `Held`, branch `ticket/<id>`; a `merge-held/v1` receipt
  on the ticket; `MergeQueueProcessor.ProcessProjectAsync` returns `null`; the workspace file is
  still `base\n`; no `merge-completed/v1` anywhere. **CI going green is not authorization to land.**
- *After the owner edits `settings.json`* (no restart, no re-enqueue): the next processor pass
  returns `Merged` for that ticket, and the pass after that returns `null` — the queue drained.

**The final truth**, asserted on all three surfaces:

| Surface | Assertion |
| --- | --- |
| Workspace | the changed file now reads `base\nheader-guard\n` |
| History | `git log --oneline` is 2 commits, `git log --merges` is empty, `HEAD` == the pushed SHA — fast-forward, linear |
| Bare remote | still resolves `ticket/<id>` to the same SHA (GigaClaw landed the merge **locally** and never pushed `main`; the remote is the CI/review surface, not the merge target) |
| Receipts | exactly one `merge-completed/v1`, parsed and checked field-by-field (`schema`, `ticketId`, `branch`); no `merge-bounced/v1` |
| Ticket | `Done` |
| Worktree | `WorktreeStatus == "cleaned"`, the directory is gone, no `worktree-cleanup-blocked/v1` — R5's semantics exactly: removed only because the branch is now an ancestor of HEAD **and** the checkout is clean |

### Leg 5 — a red check must not enqueue

**Test:** `A_red_check_records_why_and_nothing_reaches_the_merge_queue`

Asserted in the SP-3 gate's two-sided form: once on what did not happen, once on the record that
says why. The owner's merge approval is turned **on** first, deliberately — the strong form of the
claim is that the *red result* is what stops the merge, not a missing approval.

- The success automation (`conclusions: ["success"]`) polls the same commit and fires **nothing**.
- The failure automation (`conclusions: ["failure", "timed_out", "cancelled"]`) fires once, bound to
  the ticket, and its `addComment` action records the outcome.
- `Queue.ListAsync` is **empty** — not held, not queued, not bounced, *nothing* — the processor
  returns `null`, and the workspace file is unchanged. No `merge-held/v1`, no `merge-completed/v1`.

**Vocabulary gap found, then closed.** Once a `githubCheckStatus` firing was bound to a ticket, the
check-run's own name used to be lost from the `TriggerFiring`, so `addComment`'s `{ticketTitle}`
placeholder could render the *ticket* but nothing named *which* check failed. U6 follow-up (c) closed
it: `TriggerFiring.CheckName`/`CheckConclusion` are now populated on every `githubCheckStatus` firing,
ticket-bound or not, and `{checkName}`/`{checkConclusion}` are new `ActionTemplate` placeholders. The
assertion below, previously limited to what the vocabulary could prove, now asserts the failure
comment names the check that failed and how — the previously impossible assertion is the proof.

### Leg 6 — a restart between the PR and CI

**Test:** `A_restart_between_the_pull_request_and_ci_completes_without_a_duplicate_pr_or_a_double_enqueue`

Every service is thrown away and rebuilt over the same data directory at the point the flow is most
exposed: the pull request exists, no check result has arrived yet.

- **No duplicate PR.** After the restart GitHub reports the PR the dead process opened. The second
  `OpenForTicketAsync` returns `AlreadyOpen` with the same number, **sends no POST at all**, and the
  ticket still carries exactly one `github-pull-request/v1` receipt. Nothing in memory survived the
  restart; the only thing preventing the duplicate is what GitHub says now.
- **No double enqueue.** The green result is delivered twice — a second poll, then the same firing
  re-executed. The trigger's durable seen-state (workspace `dispatch-state.json`) refuses the second
  firing, and the queue's partial unique index on active entries per ticket would refuse a second
  row even if it did not. Result: exactly one queue entry, exactly one `merge-held/v1`.
- **Claim recovery.** The owner approves, the entry is claimed to `Merging` and then abandoned —
  a process killed mid-merge. A **third** set of services over the same directory recovers it,
  completes it exactly once (`Merged`, then `null`), lands the same SHA, writes exactly one
  `merge-completed/v1`, and the ticket's subsequent move to `Done` cleans the worktree.

---

## The new capability's own suite

`GitHubPullRequestTests` (18 tests) covers the capability the way C7 covers the rest of its surface:

- **Happy path** — the branch really reaches the bare repository; the POST body carries the branch,
  the base, and the ticket reference in both title and body.
- **Idempotence across a restart** — every service rebuilt, GitHub now reports the PR: no second
  POST, one receipt.
- **Local-first** — an unconfigured project never pushes and never reaches the network; a ticket
  that never ran isolated is skipped before anything leaves the process.
- **Policy** — an unapproved API host refuses the PR with an `outbound-denial/v1` receipt
  (`action: "githubRequest"`); an unapproved **push** host refuses the push with its own receipt
  (`action: "gitPush"`) *before git runs*, even though the API host is approved; and the complement
  — an approved host is genuinely let through to git.
- **Token containment** — the PAT appears in no ticket content, no receipt, no URL, and does reach
  the `Authorization` header (containment must not be achieved by never sending it); a remote URL
  carrying `user:password@` userinfo is stripped before it can be receipted.
- **Which remotes count as outbound** — a table of URL / ssh / scp-like / filesystem-path remotes.

---

## Verification

| Run | Result |
| --- | --- |
| `dotnet test GigaClaw.Core.Tests` (Debug) | `Passed!  - Failed: 0, Passed: 1291, Skipped: 0, Total: 1291` |
| `dotnet test GigaClaw.Core.Tests -c Release` | `Passed!  - Failed: 0, Passed: 1291, Skipped: 0, Total: 1291` |
| `dotnet test GigaClaw.Eval.Tests` | `Passed!  - Failed: 0, Passed: 39, Skipped: 0, Total: 39` |
| `dotnet run --project GigaClaw.Catalog -c Release -- check --strict` | exit 0 |
| `dotnet run --project GigaClaw.Catalog -c Release -- check --strict-packs` | exit 0 |

Baseline on `4e8971c` was 1270; the 21 added are the 3 U6 tests and the 18 pull-request tests.
`core.autocrlf=false` is pinned in every repository these suites create — the paid-for lesson from
commit `4082184`, where Windows' default silently rewrote LF blobs on `git worktree add` and
`git merge --ff-only` checkouts and corrupted the exact bytes the assertions compare.

---

## What remains

### For U6 itself

1. **No `githubCheckStatus` automation ships in the template.** `ProjectTemplate/Agents/automations.json`
   contains no `githubCheckStatus` (or `githubPrComment`) automation at all — neither a success path
   nor a failure path. Both automations U6 exercises are declared **in the test**, in the same
   vocabulary a template automation would use. Shipping them means deciding what an initialized
   project should poll by default, which is an owner decision and is why it was not taken here.
2. **Closed.** `OpenPullRequestActionSpec` (`openPullRequest`) is now the vocabulary addition this
   note called for: an `ActionSpec`, its discriminator, and `ActionExecutor.ExecuteOpenPullRequestActionAsync`,
   mirrored across every surface `enqueueMerge`/`startWorkflow` have (editor, palette,
   `DescribeAction`, en/fr/es localization, the generated API action table). It calls
   `GitHubPullRequestService.OpenForTicketAsync` for the firing ticket and fails closed — a project
   with no GitHub configuration gets a ticket note rather than a thrown exception — proved by
   `ActionExecutorOpenPullRequestTests`.
3. **Closed.** `TriggerFiring` now carries `CheckName`/`CheckConclusion`, populated by
   `GitHubCheckStatusTrigger` on every firing — ticket-bound or not — and rendered by
   `ActionTemplate.Render` as `{checkName}`/`{checkConclusion}`. Leg 5's assertion, previously
   weakened to what the vocabulary could prove, now asserts the failure comment names the check by
   its own name and conclusion (`GitHubCheckStatusTests`, `U6EndToEndTests`).
4. **`main` is never pushed back.** The merge lands in the local workspace; the bare remote keeps the
   ticket branch only. A real deployment would want the landed `main` pushed, and the branch deleted
   on the remote — neither exists yet, in code or in test.

### For the other half of SP-4 — R8

`PLAN-remaining.md` §6 item 14: the **Codex harness**, gated on the usage cap lifting. A second
`IAgentRunner` implementation driven against real `codex exec --json` fixtures, reaching the P13
parity checklist for one agent — streaming, resume, and policy — and reporting usage/cost or an
explicit *unsupported capability*. Nothing in U6 touches the runner abstraction, so the two halves
of the gate are independent: this evidence closes the pipeline half and says nothing about the
second-harness half.

# GitHub surface

Optional, per-project GitHub integration (roadmap task C7 / U5). It is **additive**: GigaClaw stays local-first, and a project that never configures a repository never opens a socket. Everything below is inert until an owner writes a configuration and a token into app settings.

The surface has four parts, each usable on its own:

1. **Issue import** — issues carrying a configured label become tickets, synced idempotently, with an optional round trip that comments on and/or closes the issue when its ticket is done.
2. **PR review comments → owner feedback** — a pull-request comment from a configured GitHub login re-dispatches the ticket's assignee with the comment injected as steering input.
3. **CI status → trigger** — check-run conclusions for the workspace's commits surface as a trigger in the `gitCommit` family.
4. **Pull requests** — U6's `openPullRequest` automation action pushes a ticket's R5 worktree branch and opens (or re-finds) its pull request, the natural home beside `enqueueMerge` in a verdict-gate chain.

## Purpose

A repo with a remote already has a place where humans file work and review it. C7 lets that place feed the board without making it the board: GitHub is a source and a sink, never the system of record. Tickets remain authoritative; issues are imported into them and answered from them.

## Key components

| Component | Responsibility |
|---|---|
| `GigaClaw.Core/Github/GitHubProjectConfig.cs` | The per-project, non-secret configuration record. Deliberately has no token member. |
| `GigaClaw.Core/Github/GitHubApiClient.cs` | The single outbound door. Runs the [policy preflight](#the-policy-layer) on every call and attaches the PAT to the `Authorization` header only. |
| `GigaClaw.Core/Github/GitHubIssueLinkStore.cs` | The issue ↔ ticket mapping table in the per-project SQLite DB (inline `CREATE TABLE IF NOT EXISTS` migration). What makes a re-sync an update rather than a copy. |
| `GigaClaw.Core/Github/GitHubIssueSyncService.cs` | One poll pass: import labeled issues, then run the closure round trip. |
| `GigaClaw.Core/Github/OwnerFeedback.cs` | The `github-owner-feedback/v1` ticket-comment contract and the brief rendered into a re-dispatch. |
| `GigaClaw.Core/Automation/Triggers/GitHubPrCommentTrigger.cs` | The `githubPrComment` trigger. |
| `GigaClaw.Core/Automation/Triggers/GitHubCheckStatusTrigger.cs` | The `githubCheckStatus` trigger, in the `gitCommit` family. |
| `GigaClaw.Core/Automation/Triggers/GitHubTriggerServices.cs` | The optional dependency bundle the GitHub triggers need. |
| `GigaClaw.Core/Automation/Policy/OutboundReceipt.cs` | The shared `outbound-denial/v1` receipt shape and the sink that writes it. |
| `GigaClaw.Core/Github/GitHubPullRequestService.cs` | Pushes a ticket's R5 worktree branch and opens (or re-finds) its pull request. The `github-pull-request/v1` ticket-comment receipt. |
| `GigaClaw.Web/Api/Endpoints.GitHub.cs` | REST configuration and manual sync. |

## Where the token lives

The personal access token lives in **`%APPDATA%/GigaClaw/settings.json` and nowhere else** — the same trust anchor `OutboundApprovalGate` and the merge queue use (see [automation engine](./automation-engine.md)). That file sits outside every workspace, and therefore outside every agent's write globs: an agent cannot read it, cannot rewrite it, and cannot grant itself a token.

The token is never:

- returned by any REST route (`GitHubConfigDto` carries `tokenConfigured: bool` instead),
- placed in a URL (which would put it in receipts and log lines),
- written to a ticket title, description, comment, or run prompt,
- echoed in an error string — `GitHubApiClient.Redact` scrubs it from exception messages before they can become a `GitHubResponse.Error` and reach a ticket.

Environment overrides exist for headless deployments, mirroring the Hermes key: `GIGACLAW_GITHUB_TOKEN_<SLUG>` then `GIGACLAW_GITHUB_TOKEN`.

`GitHubTokenContainmentTests` asserts each of these, including that the token *does* reach the `Authorization` header — containment must not be achieved by never sending it.

## The policy layer

Every GitHub call passes the same P3 gate the `httpRequest` automation action passes: `OutboundApprovalGate`, anchored on the owner's approved-host list in `settings.json` and re-read per call. With no approval, nothing leaves the process; the refusal is recorded as an `outbound-denial/v1` receipt naming the agent, the action (`githubRequest`), the target, the host and the rule, so the owner can see exactly which host to approve.

This holds for writes as well as reads: the closing comment and the issue-close `PATCH` are gated identically to the issue-list `GET`. `GitHubApiClient` is the only type in `GigaClaw.Core/Github/` that holds an `HttpClient`, which is what keeps "add one more caller" from becoming "add one more ungoverned path".

Receipts go to `IOutboundReceiptSink`. Unlike the `httpRequest` action's inline writer, the sink is abstracted because a refused issue import has no ticket to comment on — the receipt would otherwise vanish in exactly the case where the surface has not yet produced anything.

## Issue import

Poll-based, not webhook-based: GigaClaw is a local app behind whatever NAT the owner is on, and a webhook listener would need an inbound route most installs cannot provide and none of them should have to open.

One pass:

1. `GET /repos/{owner}/{repo}/issues?state=all&per_page=100&labels={importLabel}`.
2. Pull requests in that feed (they carry a `pull_request` member) are dropped — a PR is not a work item.
3. For each issue, consult the link table:
   - **no row** → create a ticket in `importStatus`, then record the link;
   - **row, issue newer than last sync** → update the ticket in place;
   - **row, nothing newer** → touch the link's sync timestamp and move on.
4. Run the closure round trip.

**Idempotence** is a property of the link table, not of engine memory: an issue is imported only when it has no row. A second sync, an engine restart, or two owners hitting the endpoint at once therefore converge on exactly one ticket per issue. The primary key is `(Repository, IssueNumber)`, so a project pointed at a second repo cannot have its issue #12 collide with the first repo's.

A ticket the owner deleted is **not** resurrected: the link is refreshed and the issue left alone, because re-importing would undo a deliberate delete on every poll forever.

### Round trip

When a linked ticket reaches one of the configured `doneStatuses`, the sync optionally posts a comment on the issue and/or closes it. Both flags default off. The link row's `RoundTripDone` marker makes this happen exactly once — without it, every subsequent poll would post another comment. A refused (dry-run) write leaves the marker unset, so the round trip retries once the host is approved.

## PR review comments as owner feedback

The `githubPrComment` trigger polls `/repos/{owner}/{repo}/pulls/comments` and fires for each ticket whose pull request just received a comment from a configured **owner login**. Because it is a trigger in the ordinary [automation vocabulary](./automation-engine.md), it composes with every existing condition — `assignedTo`, `ticketInColumn`, `labels`, `repairBudget` — instead of being a bespoke hook with its own rules.

### Re-dispatch reuses the repair-loop mechanism

C3 already solved "re-dispatch an agent with something it must read first", and it solved it by putting the evidence on the ticket and re-deriving the state from the comment trail at dispatch time (see [verdict contract](./verdict-contract.md)). Owner feedback rides that rail rather than adding a second one:

1. The trigger writes the comment onto the ticket as a `github-owner-feedback/v1` comment — **before** it fires, so the steering is durable even if the action chain then fails.
2. The automation dispatches the assignee as usual.
3. `ActionExecutor.ComposeDispatchContextAsync` renders the outstanding feedback into the prompt, beside the repair brief and the previous run's handoff.

"Outstanding" means *every feedback comment since the agent last handed off*. A handoff closes the episode exactly as a `SHIP` closes a repair episode, so an answered comment is not re-litigated on every later dispatch.

Several comments on one PR produce **one** firing carrying all of them — three comments should be one re-dispatch that addresses all three, not three competing runs on the same files.

### Who counts as the owner

The owner logins live in the project's app-level settings, alongside the token. An automation may **narrow** that list (the trigger intersects), never widen it: `automations.json` lives in the workspace and is agent-writable, so a widening list would let an agent with repository write access nominate itself and steer its own next dispatch. An empty owner list is "no one", not "anyone" — the trigger fails closed.

### Resolving which ticket a comment steers

Cheapest source first, and no guessing:

1. an explicit `ticket-<id>` in the comment body;
2. the pull request's branch name, title, or body;
3. the issue the PR closes (`#n`), resolved through the part-1 link table.

If none of those resolve, nothing fires. Guessing would re-dispatch an agent onto work the comment was never about.

Dedupe is a per-automation cursor of the highest comment id seen, stored in the workspace's `dispatch-state.json` — the same mechanism `ticketCommentAdded` uses, so two automations watching one repository cannot swallow each other's comments.

## CI status as a gitCommit-family trigger

The `githubCheckStatus` trigger fires when a GitHub check run reaches one of its configured conclusions (`failure` by default; an empty list means every concluded run) for the commit under watch.

It belongs to the **`gitCommit` family**, and that is a design claim, not a label. Like `gitCommit`, it is about a commit rather than a ticket or a clock: it resolves the commit from the workspace's own `git rev-parse HEAD` (unless `ref` pins a branch or SHA), it polls on the same debounce shape, and it keeps its seen-state in the same durable place. Anything else would give the board two different notions of "a commit happened".

**Dedupe** is one key per `(sha, check-run id, conclusion)` in the workspace's `dispatch-state.json`, per automation — the same store `gitCommit` uses for its last-processed commit and `ticketCommentAdded` uses for its comment cursor. The conclusion is part of the key on purpose: a re-run that flips `failure` to `success` is a new event, not the same one seen twice. The key set is bounded so a long-lived workspace's state file cannot grow without limit.

A check run that has not concluded (`queued`, `in_progress`) fires nothing — it has not concluded anything.

**Ticket binding** is best-effort and free: the commit's own message is read for a `ticket-<id>` reference. When one is found the firing carries that ticket; otherwise it is ticketless, which is `gitCommit`'s normal shape rather than a failure.

## Pull requests

U6 (`doc/roadmap/U6-EVIDENCE.md`) added the missing leg between R5 (a ticket's work lives on its own `ticket/<id>` branch in a worktree) and CI status above: something has to put the branch where GitHub can see it. `GitHubPullRequestService.OpenForTicketAsync` follows C7's rules rather than inventing new ones — settings-only PAT, every HTTP call through the same P3 gate, a `git push` gated on the remote's host the same way, and idempotence by asking GitHub whether a pull request already exists rather than keeping its own table.

The `openPullRequest` automation action is the executor's arm around it — the natural home is beside `enqueueMerge` in a `verdict-gate-*` chain: *verdict SHIP → open PR → wait for CI → enqueue merge*. Like `enqueueMerge` it only records intent: it pushes the branch and opens (or re-finds) the pull request, once, and returns — CI and review are driven by their own triggers. An unconfigured project (no remote, no token, a ticket never dispatched with `isolation: "worktree"`) is not an error: the service returns rather than throws, and the action writes a note on the ticket explaining why instead of letting the outcome vanish silently. Any policy-gate refusal (push host or API host not approved) already carries its own `outbound-denial/v1` receipt from the service, so the action does not duplicate it.

## Entry points

| Route | Purpose |
|---|---|
| `GET /api/projects/{slug}/github` | Current configuration (never the token). |
| `PUT /api/projects/{slug}/github` | Save configuration; a blank `token` keeps the stored one. |
| `DELETE /api/projects/{slug}/github` | Forget the configuration and its token. |
| `POST /api/projects/{slug}/github/sync` | Run one sync pass now; returns the counts. |
| `GET /api/projects/{slug}/github/links` | The issue ↔ ticket mapping. |

Services are singletons registered in `GigaClaw.Web/Program.cs`. There is no Blazor page for this surface yet — repository, token and owner-login configuration is REST-only. The two triggers do appear in the automation editor's trigger palette, because leaving a registered trigger type out of that switch is a runtime failure, not a missing feature.

`ProjectRuntimeManager` takes the GitHub services as an *optional* bundle: a host that never registered them builds every other trigger unchanged, and a GitHub trigger declared without them degrades to a no-op with a warning rather than failing the project's whole config load.

## External dependencies

- The GitHub REST API (`https://api.github.com` by default; `apiBaseUrl` is overridable for GitHub Enterprise), version `2022-11-28`.
- A personal access token with `repo` scope for private repositories, or `public_repo` for public ones.
- The host must appear in the owner's `approvedOutboundHosts`, or every call is a dry run.

Tests never reach the network: `GigaClaw.Core.Tests/Github/` drives the whole surface through a fake `HttpMessageHandler`.

# GitHub surface

Optional, per-project GitHub integration (roadmap task C7 / U5). It is **additive**: GigaClaw stays local-first, and a project that never configures a repository never opens a socket. Everything below is inert until an owner writes a configuration and a token into app settings.

The surface has three parts, each usable on its own:

1. **Issue import** — issues carrying a configured label become tickets, synced idempotently, with an optional round trip that comments on and/or closes the issue when its ticket is done.
2. **PR review comments → owner feedback** — a pull-request comment from a configured GitHub login re-dispatches the ticket's assignee with the comment injected as steering input.
3. **CI status → trigger** — check-run conclusions for the workspace's commits surface as a trigger in the `gitCommit` family.

## Purpose

A repo with a remote already has a place where humans file work and review it. C7 lets that place feed the board without making it the board: GitHub is a source and a sink, never the system of record. Tickets remain authoritative; issues are imported into them and answered from them.

## Key components

| Component | Responsibility |
|---|---|
| `GigaClaw.Core/Github/GitHubProjectConfig.cs` | The per-project, non-secret configuration record. Deliberately has no token member. |
| `GigaClaw.Core/Github/GitHubApiClient.cs` | The single outbound door. Runs the [policy preflight](#the-policy-layer) on every call and attaches the PAT to the `Authorization` header only. |
| `GigaClaw.Core/Github/GitHubIssueLinkStore.cs` | The issue ↔ ticket mapping table in the per-project SQLite DB (inline `CREATE TABLE IF NOT EXISTS` migration). What makes a re-sync an update rather than a copy. |
| `GigaClaw.Core/Github/GitHubIssueSyncService.cs` | One poll pass: import labeled issues, then run the closure round trip. |
| `GigaClaw.Core/Automation/Policy/OutboundReceipt.cs` | The shared `outbound-denial/v1` receipt shape and the sink that writes it. |
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

## Entry points

| Route | Purpose |
|---|---|
| `GET /api/projects/{slug}/github` | Current configuration (never the token). |
| `PUT /api/projects/{slug}/github` | Save configuration; a blank `token` keeps the stored one. |
| `DELETE /api/projects/{slug}/github` | Forget the configuration and its token. |
| `POST /api/projects/{slug}/github/sync` | Run one sync pass now; returns the counts. |
| `GET /api/projects/{slug}/github/links` | The issue ↔ ticket mapping. |

Services are singletons registered in `GigaClaw.Web/Program.cs`. There is no Blazor UI for this surface yet — configuration is REST-only.

## External dependencies

- The GitHub REST API (`https://api.github.com` by default; `apiBaseUrl` is overridable for GitHub Enterprise), version `2022-11-28`.
- A personal access token with `repo` scope for private repositories, or `public_repo` for public ones.
- The host must appear in the owner's `approvedOutboundHosts`, or every call is a dry run.

Tests never reach the network: `GigaClaw.Core.Tests/Github/` drives the whole surface through a fake `HttpMessageHandler`.

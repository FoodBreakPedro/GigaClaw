# The `gigaclaw-system` self-maintenance pipeline

**Status:** designed, not built. Recorded 2026-08-07. Not urgent — content throughput is the standing
priority. This page exists so the next session does not have to rediscover the gaps.

## What it is meant to be

`gigaclaw-system` is not a content venture. It is the pipeline by which **GigaClaw diagnoses and
repairs itself**: a bug or enhancement becomes a ticket, a coding agent works it on a branch, and the
branch arrives on GitHub as a pull request. The owner merges. `gigaclaw-main-poll.timer` picks the
merge up within five minutes and the running instance updates itself.

The loop is deliberately **not** closed by an agent. A human merge is the gate, and that is the whole
safety story: agents propose, the owner disposes, and only then does the code that runs the agents
change.

## What already works

The tail of the loop is real and verified (2026-08-07):

- `gigaclaw-main-poll.timer` runs `update-from-main.sh --delivery systemd-poll` every 5 minutes.
- The script retries health checks for up to 24 seconds, which fixed a false-negative race where the
  probe ran before Kestrel bound port 5230.
- The script refuses to fetch or pull when its source checkout has tracked, staged, or untracked
  changes, so its failure rollback cannot erase local work (`2026-08-09` guard test: dirty exit 12,
  clean dry-run exit 0).
- Previous application sets are retained as `~/gigaclaw/app-prev-*`, so a bad deploy has something to
  fall back to.

The engine-side vocabulary also exists: `openPullRequest`, `enqueueMerge`, `gitCommit`-family
triggers, worktree isolation, and the `programmer` / `qa-tester` / `committer` / `code-janitor`
agents all ship in the template. See [`../github-surface.md`](../github-surface.md) and
[`../worktree-workflow.md`](../worktree-workflow.md).

## What is missing

Measured against the live `gigaclaw-system` workspace on zabs-server, 2026-08-07:

| # | Gap | Evidence |
|---|---|---|
| 1 | **The workspace contains no GigaClaw source** | It holds only `CLAUDE.md`, `.gitignore`, `.agents/`, `.dashboard/`. The source lives at `~/gigaclaw/src`, which the project does not point at. Agents have nothing to open. |
| 2 | **No git remote** | Standalone repo on `master`, 6 commits, `git remote -v` empty. Nothing to open a PR against. |
| 3 | **No PR machinery wired** | Its `automations.json` contains `openPullRequest: 0` and `enqueueMerge: 0`. The loop would stop after the commit. |
| 4 | **No worktree isolation** | `worktree: 0` in its automations. Per `doc/worktree-workflow.md` this is opt-in and template SKILLs do not use it by default, so concurrent agents would collide in one checkout. |
| 5 | **A content automation is enabled there** | `cms-dispatch-on-done` is present and enabled in a code project. It is the reason that project alone kept the placeholder CMS url — it was never a publishing target. |

Gap 1 is the one that explains the others being untested: tickets like
`[template] sync_draft_to_description.py: unguarded urlopen raises raw` were filed *about* GigaClaw,
but no agent in that workspace could ever have reached the file.

## Design constraints for whoever builds it

**Never let an agent write to the deploy source.** `~/gigaclaw/src` is what `update-from-main.sh`
pulls and builds. An agent editing it directly would mutate the deployment mid-flight. The workspace
needs its own clone, with per-ticket worktrees branching off it.

**Agents push branches, never `main`.** The human merge on GitHub is the only gate between an agent's
output and a running deployment that auto-updates every five minutes. Anything that lets an
automation merge its own PR removes it.

**The deploy gate fails closed, but it is not yet trustworthy enough for agent-volume changes.**
Observed 2026-08-07: the pipeline correctly refuses to publish when a test fails, so a merged PR that
does not compile will not reach production. But two bugs surfaced on the first real exercise — the
poller wedged permanently after a failed deploy, and a build/test race failed a green commit. Both
are fixed (see [EXECUTION-PLAN-2026-08-05.md](./EXECUTION-PLAN-2026-08-05.md)). A third is open: at
least one API test fails intermittently on the server and passes locally. **Fix that flake before
agents are opening PRs at volume** — with the rollback in place a flaky gate now retries every five
minutes instead of stopping, so a persistent failure becomes a loop rather than a silent stall.

**Disable `cms-dispatch-on-done` in this project** regardless of when the rest is built. It is a
content-publishing automation aimed at a fake domain in a project that publishes nothing. Low risk
today only because its conditions (`ready-for-cms` + `approved` + Done) never coincide there.

## Suggested first slice

Smallest thing that proves the loop end to end, in order:

1. Point the project at a clone of the GigaClaw repo with a real `origin` remote.
2. Disable `cms-dispatch-on-done` there.
3. One ticket → `programmer` on a per-ticket worktree branch → commit.
4. Add `openPullRequest` so that branch surfaces as a PR.
5. Merge by hand, and watch the poller deploy it.

Only after that is reproducible is it worth wiring `qa-tester`, `code-janitor-nightly`, or any
triage automation on top.

## Related

- [`../workflow-graph.md`](../workflow-graph.md) — the declarative graph. A code pipeline is a
  plausible second consumer, but note the graph currently ships **declared and not executed**; see
  `TemplateWorkflowGraphTests.Graph_is_declared_only_and_nothing_starts_a_walk`.
- [`EXECUTION-PLAN-2026-08-05.md`](./EXECUTION-PLAN-2026-08-05.md) — checkpoint ledger.

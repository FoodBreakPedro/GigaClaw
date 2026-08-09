# Incremental execution plan - 2026-08-05

This ledger keeps the remaining GigaClaw work recoverable across sessions and usage limits. Update it after every implementation checkpoint. Each checkpoint should produce a focused implementation commit followed by a status/evidence commit when documentation changes are needed.

## Working rules

- Work only in GigaClaw. Do not edit ZabalaZone. zabs-server access is limited to the owner-authorized post-push deployment check.
- Preserve `.obsidian/**`, `graphify-out/**`, and unrelated owner changes unless explicitly included in a named checkpoint.
- Give sub-agents disjoint file ownership and record their model, scope, and result below.
- Run focused tests before each implementation commit and the full affected suites before merging.
- Record the commit id, tests, propagation files, remaining risks, and exact next action before starting the next checkpoint.
- Merge completed checkpoints early. Do not merge a checkpoint that leaves its user-visible path knowingly broken.

## Checkpoints

| Checkpoint | Scope | Status | Durable result |
|---|---|---|---|
| 0 | Stabilize and preserve the existing R8 Codex harness | Complete | `ecba804`, `8de75ca`, `e4bd3ce` |
| 1 | CMS `categorySlug` and `tags` dispatch, authentication, and placeholder safety | Complete | `b68d020` through `d4c7bcc`; evidence commit follows this ledger update |
| 2 | Non-destructive `.agents` synchronization with dry-run drift reporting | Complete | `39f50ed` through `2dc4dad`; evidence commit follows this ledger update |
| 3 | Deliverable catalog, ticket persistence, and deterministic entry routing | Complete | `b9f3bf5` through `7236382` |
| 4 | Deliverable-first ticket creation and human-readable pipeline progress | Complete | `0d636a9`, `58d61eb`, `898aa0d`, `a93d6f2` |
| 5 | Canonical content routes and bounded translator/content recovery | Complete | `0d22cac`, merged by `3737153` |
| 6 | Truthful content journeys, optional media, propagation, and deployment handoff | In progress | [`CHECKPOINT-6-CONTENT-JOURNEYS.md`](CHECKPOINT-6-CONTENT-JOURNEYS.md) |
| 7 | Security join, Codex budgets, model overrides, and specialist promotion | Pending | - |

## Checkpoint 0 log

Branch: `codex/r8-codex-harness`

Existing R8 work was moved off `main` without changing the working tree. Obsidian and Graphify files remain unstaged and excluded.

Verification completed:

- Initial focused R8 suite: 19 passed.
- Initial full solution gate: 1,473 Core tests and 45 Eval tests passed.
- Initial opt-in live test exposed that it was invoking `GigaClaw.CodexMock` instead of the real CLI.
- Corrected opt-in real Codex test: 1 passed against the authenticated CLI.
- Final focused runtime, lifecycle, policy, routing, and member gate: 69 passed.
- Final full solution gate: 1,484 Core tests and 45 Eval tests passed.
- Implementation commits: `ecba804` and `8de75ca`.

Review and implementation assignments:

| Model | Scope | Result |
|---|---|---|
| GPT-5.4 | R8 file/test inventory | Found model-picker inconsistency, mock fail-open risk, and router coverage gap |
| GPT-5.5 | Runtime architecture review | Found missing terminal-event exit grace and silent invalid-model fallback |
| Luna | Documentation/evidence audit | Found stale roadmap statements and required durable handoff files |
| Terra | Commit-boundary review | Recommended separate vertical implementation and evidence commits |
| GPT-5.5 | Terminal-event grace and per-instance binary resolution patch | Implemented, reviewed, and integrated |
| Terra | Fail-closed model patch | Identified required runner integration; no files changed due ownership boundary |

Deferred test improvement: direct `AgentRunnerRouter.RunAsync` precedence coverage remains useful, but environment parsing, member persistence, mock dispatch, and live dispatch are covered and the gap is not release-blocking.

Exact next action: begin checkpoint 1 with focused `DraftFrontmatter` parsing/emission tests, then complete the CMS body-template path and blog-writer contract.

## Checkpoint 1 log

Branch: `codex/cms-taxonomy-dispatch`

Delivered:

- `DraftFrontmatter` parses `categorySlug` and YAML-style tag lists, preserving flat scalars and nested `seo` fields.
- Placeholder values emit an escaped category scalar and a raw, valid JSON tag array.
- The shipped CMS body contains both fields, keeps `secretRef`, and has no explicit Authorization header.
- Missing secrets and unresolved URL, method, header, or body template values fail before transport.
- Template validation examines the source template, so literal placeholder-shaped article text remains publishable.
- Blog-writer emits the reviewer-validated taxonomy format.
- Mechanical CMS failures return to `Review`, leaving `Blocked` for genuine decisions.
- Generated catalog drift was corrected from 22 to the expected 23 shared scripts.

Implementation assignments:

| Model | Scope | Result |
|---|---|---|
| GPT-5.4 | Frontmatter parser and focused tests | Implemented as `9c15652` |
| Luna | CMS body template and failure status | Implemented as `69c905f` and `8717cb0` |
| Terra | Blog-writer taxonomy contract | Implemented as `b68d020` |
| GPT-5.5 | HTTP secret/template safety and tests | Implemented as `6261ef0`; integration refinement `2ebe9b4` |

Verification completed:

- Draft/HTTP integration: 51 passed.
- Combined parser, HTTP, template contract, catalog-count, and manifest gate: 108 passed.
- Core manifest regeneration: only `.agents/automations.json` and `.agents/blog-writer/SKILL.md` hashes changed; normal manifest gate passed 2/2.
- Catalog `check --strict`: exit 0.
- Catalog `check --strict-packs`: exit 0.
- Full solution: 1,496 Core tests and 45 Eval tests passed.

Exact next action: implement checkpoint 2's non-destructive sync with dry-run reporting before introducing more embedded workflow files.

## Checkpoint 2 log

Branch: `codex/agents-safe-sync`

Delivered:

- A dedicated preview/apply sync operation updates unchanged core-managed `.agents` files, installs new files, and removes retired unmodified files.
- Owner-modified and owner-deleted files are preserved and reported with exact paths; invalid or missing lock baselines fail closed for manual review.
- `.agents/*/memory/**` is excluded from sync, plan-token fingerprints, and workspace drift reporting.
- `automations.json`, `contracts.json`, `models.json`, and `teams.json` merge per entry so unrelated owner additions survive.
- Stale plan tokens, symbolic-link destinations, duplicate structured IDs, and concurrent changes fail safely.
- Explicit destructive initialization behavior is unchanged.
- Project Settings exposes a localized **Agent templates** preview/apply flow backed by project API endpoints. Successful applies reload automations and seed newly introduced members.
- The operation is scoped strictly to `.agents/**`; dashboard and other root-template drift is neither inspected nor reported.

Implementation assignments:

| Model | Scope | Result |
|---|---|---|
| GPT-5.5 | Sync architecture and adversarial review | Identified symlink, memory-token, metadata-baseline, token-shape, and duplicate-ID risks; fixes integrated |
| Terra | Project API endpoints and integration tests | Implemented preview/apply routes, stale-plan handling, reload, and member seeding |
| Luna | Settings UX, localization, and risk review | Implemented the preview/apply interface and UI coverage; localization was aligned with the existing resource pattern |
| GPT-5.4 | Deployment-side verification support | Reserved for the post-push server checkpoint |

Verification completed:

- Focused sync service suite: 18 passed.
- Combined sync, drift, API, route, initialization, localization, and Settings component suites passed during implementation.
- Full solution: 1,522 Core tests and 45 Eval tests passed.
- Catalog `check --strict`: exit 0.
- Catalog `check --strict-packs`: exit 0.
- No `ProjectTemplate/Agents/**` file changed, so no manifest regeneration was required.

Exact next action: begin checkpoint 3 by defining the user-facing deliverable catalog and persisting `deliverableType` on tickets, then resolve that value into a deterministic entry automation without asking users to choose an agent or pipeline order.

## Checkpoint 3 log

Branch: `codex/deliverable-routing`

Delivered:

- A canonical catalog exposes six intended outputs: Blog Post, Email Newsletter, Social Media Content, Product Review, Lead Magnet, and Content Series.
- Each definition has a stable slug, display metadata, output category, and deterministic entry agent.
- Tickets persist nullable `deliverableType` through create, update, list, and detail operations, including an idempotent migration for existing SQLite databases.
- Human-friendly values such as `Email Newsletter` normalize to canonical slugs; unknown values fail before persistence.
- Creating a ticket with a deliverable and no explicit assignee derives the catalog entry agent. A nonempty explicit assignee remains authoritative for specialist and API compatibility.
- `GET /api/deliverables` exposes the catalog without requiring clients to duplicate routing data.
- OpenAPI publishes both the catalog route and the `deliverableType` ticket request property.
- Template contract coverage proves every catalog entry agent is reachable through an enabled Todo dispatcher.
- Existing tickets and callers that omit `deliverableType` retain their previous behavior.

Canonical routing:

| Deliverable | Slug | Entry agent |
|---|---|---|
| Blog Post | `blog-post` | `blog-writer` |
| Email Newsletter | `email-newsletter` | `email-copywriter` |
| Social Media Content | `social-media-content` | `growth-writer` |
| Product Review | `product-review` | `blog-writer` |
| Lead Magnet | `lead-magnet` | `lead-magnet-creator` |
| Content Series | `content-series` | `content-series-planner` |

Implementation assignments:

| Model | Scope | Result |
|---|---|---|
| GPT-5.4 | Ticket persistence, migration, API field, and round-trip tests | Implemented as `a8f095d` |
| Terra | Canonical Core catalog, validation, and tests | Implemented as `b9f3bf5` |
| GPT-5.5 | Graphify-backed architecture and routing review | Found missing resolution, validation, catalog API, Backlog/Todo semantics, and canonical-route risks; integration fixes landed |
| Luna | UX/test contract audit and final checkpoint reconciliation | Confirmed Checkpoint 4 UI scope, OpenAPI needs, compatibility traps, and release evidence |

Verification completed:

- Catalog and routing-focused suite: 82 passed.
- OpenAPI routing contract: 6 passed.
- Final full Core suite: 1,549 passed.
- Full Eval suite: 45 passed.
- Catalog `check --strict`: exit 0.
- Catalog `check --strict-packs`: exit 0.
- No `ProjectTemplate/Agents/**` file changed, so no manifest regeneration or `.agents` propagation is required for checkpoint 3.

Behavioral boundary for checkpoint 4: assigning an entry agent does not force a ticket out of Backlog. A deliverable ticket begins automation when it reaches Todo. The creation UI should make this state clear and offer a deliberate start-now path rather than silently overriding a lane chosen by the owner.

Exact next action: implement checkpoint 4 in both Board creation surfaces by replacing the primary assignee choice with the localized deliverable catalog, passing the chosen slug to `TicketService`, and showing human-readable route/progress state while retaining an advanced explicit-assignee escape hatch.

## Checkpoint 5 log

Branch: `codex/checkpoint-5-content-recovery`

Delivered:

- Public Blog Post and Product Review deliverables remain on the file-backed `blog-writer` route; ticket-native `content-writer` remains an AD-7 compatibility route and is not a public deliverable.
- `blog-reviewer` and `content-writer` now use the dispatch contract names `categorySlug` and `tags` consistently.
- Translator approval-chain mismatches return to `blog-seo` once, then require a visible draft plus a specific owner question and enumerated options before `Blocked`; unreadable artifacts return to `groomer` in `Backlog`.
- Content-writer incomplete turns remain in `InProgress` for the existing five-attempt resume trigger. Both content-specific and shared resume exhaustion route to `groomer` in `Backlog`, reserving `Blocked` for decisions.
- Focused embedded-template tests pin taxonomy wording, bounded receipts, resume caps, and exhaustion destinations.

Verification completed:

- Focused recovery, manifest, and catalog contracts: 12 passed.
- Manifest regeneration changed exactly four intended skill hashes: `blog-reviewer/SKILL.md`, `blog-reviewer/references/ad7-protocol.md`, `blog-translator/SKILL.md`, and `content-writer/SKILL.md`.
- Full local solution: 1,553 Core tests and 45 Eval tests passed.
- Catalog `check --strict` and `check --strict-packs`: exit 0.
- zabs-server deployment at `3737153`: 1,553 Core and 45 Eval tests passed; home and board returned HTTP 200.

Server constraints resolved:

- `/home/zabalazone/gigaclaw/bin/update-from-main.sh` now retries health checks for up to 24 seconds. Backup: `update-from-main.sh.bak-20260806-cp5`. The first live deployment needed two retries before Kestrel answered, proving the prior false-negative race.
- All eight legacy workspaces received a core `packs.lock.json` baseline without changing any other `.agents` byte; before/after inventories are under `/home/zabalazone/gigaclaw/logs/baseline-adoption-20260806`.
- Five byte-identical historical core skill files and the two bounded resume automation entries were propagated with exact-hash guards. Backups are under `/home/zabalazone/gigaclaw/logs/template-propagation-20260806`.
- Codex CLI 0.146.1 was installed under `~/.local`, authenticated with ChatGPT, and enabled through `GIGACLAW_AGENT_HARNESS=codex` plus an absolute `GIGACLAW_CODEX_BIN`. The opt-in real CLI smoke passed 1/1 on zabs-server.

Review assignment:

| Model | Scope | Result |
|---|---|---|
| GPT-5.5 | Read-only prompt and contract review | Found stale incomplete-work blocking and incomplete owner-decision requirements; both corrected before merge |

Exact next action remains checkpoint 4. Checkpoint 5 was intentionally completed first at the owner's request; checkpoint 4 is still required for the simplified deliverable-first creation experience.

## Checkpoint 4 log

Branch: `codex/checkpoint-4-declared-route`

Delivered:

- `ProjectTemplate/Agents/workflow.json` declares each deliverable's route. Routing gates key on the
  entry agent the catalog assigns at creation, so a deliverable ticket lands on its own chain without
  the board asking the owner to pick one.
- The route is **declared, not derived**. Deriving it from `automations.json` was tried and fails:
  `blog-reviewer-on-review` dispatches with `runAgent` and never reassigns, so a walk over
  `assignTicket` edges omits the reviewer and reports the blog route one stage short.
- The graph is **declared and not executed**. `WorkflowWalker` materializes a sub-ticket per task
  state, so starting a walk would put a second engine on a pipeline the handoff automations already
  drive. `Graph_is_declared_only_and_nothing_starts_a_walk` fails if a `startWorkflow` action is ever
  added, forcing that reconciliation to be designed first.
- `DeliverableRoute` answers "what will run" and "where is this ticket", both read-only.
- `Board.razor`: deliverable catalog as the primary control, a read-only line naming the agents it
  runs in order, explicit Backlog-vs-Todo start semantics, and team/assignee collapsed into an
  Advanced section that stays authoritative when set and says so.
- `UnifiedBoard.razor`: the same selector and route preview, stated against the lane's target column.
- Ticket panel renders declared stages as done/current/upcoming. An assignee that is not a stage —
  the groomer recovery hop, or an owner-assigned specialist — renders **off-route** rather than
  collapsing to stage one, which would report a restart that did not happen.
- Progress is status-aware: `Review` shows the declared reviewer/approval stage even though the
  corresponding `runAgent` automation deliberately leaves assignment on the writer, and `Done`
  marks every declared stage complete.
- Safe sync reloads the runtime when `workflow.json` changes, independently of whether
  `automations.json` changed, so an added graph becomes visible without a service restart.
- Only `blog-post` and `product-review` have a real pipeline. The other four deliverables get an
  honest entry-plus-approval chain whose state descriptions say no further pipeline is configured,
  rather than inventing stages the template does not implement.

Verification completed:

- Focused route, graph, and creation-surface suites: 23 passed.
- Full local solution: 1,576 Core tests and 45 Eval tests passed.
- Core-init manifest gained exactly one entry, `.agents/workflow.json`.
- Localization parity: en/fr/es at 149 keys each.
- Isolated debug instance (`:5232`, own data dir, mock CLI): a fresh workspace received
  `workflow.json`; the engine logged `workflow graph: 19 state(s)`, which an invalid graph could not
  do because validation failure aborts the whole automation reload; `deliverableType: "Blog Post"`
  normalized to `blog-post` and derived `blog-writer` while correctly staying in `Backlog`; the
  ticket panel rendered `● blog-writer ○ blog-reviewer ○ blog-seo`.
- Completion gate (2026-08-09): 29 focused route, Board, and sync API tests passed; the full local
  solution passed 1,588 Core and 45 Eval tests. A GPT-5.5 read-only review found no correctness
  issues. One timing-only policy-hook benchmark exceeded its shadow target under the first full
  run, then passed alone and in the complete rerun.

Content pipeline health check (zabs-server, 2026-08-07): every ticket that reached `ready-for-cms`
plus `approved` published — gamelifteat 4/4, karalungaming 2/2, zabsconsulting 2/2. Nothing stuck, no
`Blocked`, no stalled `Review`. The constraint is throughput, not correctness: 8 published pieces
total with every board empty apart from `Backlog` and `Done`.

Found in passing and fixed by follow-up `93a0afc`: `POST /api/projects/{slug}/tickets` now applies
the `"Backlog"` default when `status` is omitted and maps persistence failures to stable API errors
instead of exposing a raw EF stack trace.

Server constraints resolved (2026-08-07):

Deploying checkpoint 4 exposed two pre-existing bugs in `/home/zabalazone/gigaclaw/bin/update-from-main.sh`. Both are fixed; backup `update-from-main.sh.bak-20260807-cp4`.

- **The poller wedged on any failed deploy.** `git pull --ff-only` advances the checkout *before* the tests run, so a failing test left `src` at the new SHA. The next poll compared `before == remote`, logged `No update needed`, and never retried — the box served a stale build indefinitely while the service, health checks, and poll log all reported green. It happened twice in one evening and stranded production on an Aug 6 build for ~35 minutes. The ERR trap now calls `rollback_src_on_failure`, which rewinds `src` to where the run started so the next poll sees a real delta and retries.
- **A build/test race failed a green deploy.** `dotnet test GigaClaw.slnx` builds and tests in one pass, so `GigaClaw.Eval.Tests` could start while `GigaClaw.ClaudeMock` was still being written — and `ReplayRunner` execs that binary as the mock claude CLI. It replayed against a half-written `claude.dll` and failed `EveryPipelineFamilyHasAFixtureThatReplaysGreen`, which passed standalone on the same commit on the same box. Now `dotnet build --no-restore` followed by `dotnet test --no-restore --no-build`.

Deployment verified end to end: `0aab610` deployed 22:15:33, `GigaClaw.Core.dll` rebuilt, `/app.css` serving the deliverable rules, `/api/deliverables` returning all six, service active, home HTTP 200.

Known unresolved: `TicketDependencyApiTests.DependencyEndpoints_ExposeCrudProjectionAndStableValidationErrors` fails intermittently **on the server only** (500 from `CreateProjectAsync`, 15 s for a test that runs in 656 ms locally); it passes locally and passed the next server run. Production data was ruled out — `GIGACLAW_DATA_DIR` is set only in the systemd unit, not the login shell — as was stale state, since neither default app-data root exists. With the rollback in place this now self-heals by retrying rather than wedging, but a gate that retries every five minutes on a flake is its own problem.

Propagation and deployment completed (2026-08-09): `a93d6f2` passed the zabs-server gate with
1,588 Core and 45 Eval tests, published, restarted, and returned HTTP 200 for home and board. Safe
sync added `.agents/workflow.json` to all eight existing projects and updated only the corresponding
`packs.lock.json` metadata; `automations.json` hashes were unchanged. Every project now loads 19
states and reports zero remaining applicable sync changes. Backups are under
`/home/zabalazone/gigaclaw/logs/workflow-propagation-20260809_135842` and
`workflow-propagation-20260809_135956`.

The server deploy script now refuses automatic updates when the source checkout has tracked,
staged, or untracked changes, before any fetch/pull or rollback point is recorded. A disposable
dirty-marker test exited 12 without moving HEAD; a clean dry-run then no-op'd normally. Backup:
`/home/zabalazone/gigaclaw/bin/update-from-main.sh.bak-20260809-cp4-progress`.

Exact next action: checkpoint 6 — exercise each public deliverable from creation through its first
agent and review/approval exit, and produce the final propagation and deployment report.

## Propagation and deployment

Checkpoint 0 does not modify `ProjectTemplate/Agents/**`, so it has no `.agents` propagation list.

Checkpoint 1 propagation list for every existing project:

- `.agents/automations.json` — merge the `cms-dispatch-on-done` changes; do not overwrite unrelated owner automation edits.
- `.agents/blog-writer/SKILL.md` — apply the taxonomy-frontmatter contract.

Do not change any `.agents/*/memory/**` file. After checkpoint 2 is deployed, use the Project Settings **Agent templates** preview before applying these checkpoint 1 updates. Projects without a trustworthy `.agents/packs.lock.json` remain manual-review cases and are not changed.

Checkpoint 2 does not modify `ProjectTemplate/Agents/**`; its propagation list is empty. It does require deploying the rebuilt application and `GigaClaw.Core.dll` so existing projects can use the sync API and Settings flow.

Checkpoint 3 does not modify `ProjectTemplate/Agents/**`; its propagation list is empty. Deploying the rebuilt application adds the ticket column migration, catalog API, and routing behavior.

Checkpoint 4 propagation list for every existing project:

- `.agents/workflow.json` — add the declared 19-state deliverable graph through safe sync.

This file was propagated to all eight zabs-server projects on 2026-08-09. Safe sync changed only the
new file and `packs.lock.json` metadata, preserved every owner conflict/deletion, reloaded each
runtime, and left no remaining applicable changes.

Checkpoint 5 propagation list for every existing project:

- `.agents/blog-reviewer/SKILL.md`
- `.agents/blog-reviewer/references/ad7-protocol.md`
- `.agents/blog-translator/SKILL.md`
- `.agents/content-writer/SKILL.md`
- `.agents/automations.json` — merge only `content-writer-resume` and `assignee-resume`

These checkpoint 5 paths were propagated on zabs-server with backups. `.agents/blog-writer/SKILL.md` from checkpoint 1 was propagated in the same guarded operation.

**`cms-dispatch-on-done` resolved 2026-08-07.** The seven workspaces holding the entry were not merely
missing checkpoint 1 — they were frozen at the state *before* `7ec4349` (2026-08-03), so they also
lacked that commit's timeout hardening (30s → 90s), its explicit `Content-Type` header, and its
removal of the deleted `sourceSystem` field, on top of checkpoint 1's `categorySlug`/`tags` and
`failureStatus: Review`. All seven were updated to the canonical entry with each instance's own
`httpRequest.url` preserved — six at `https://zabalazone.com/api/ai/draft`, `gigaclaw-system` at its
placeholder. Backups: `/home/zabalazone/gigaclaw/logs/cms-dispatch-repair-20260807/`. Verified exactly
one automation entry changed per workspace; service restarted, home and board HTTP 200, all projects
reloaded 29 entries with no errors.

`payload-test` was left untouched and is not a gap: it has no `httpRequest` action at all and reaches
its CMS through `n8n-dispatch-on-commit-receipt` instead.

`gigaclaw-system`'s placeholder url is also not a gap but a symptom — it is a code project, not a
content venture, and `cms-dispatch-on-done` does not belong there at all. See
[gigaclaw-self-maintenance.md](./gigaclaw-self-maintenance.md).

Note for future propagation: `update-from-main.sh` deploys the **application from main only**. It does
not push `ProjectTemplate/**` changes into existing workspaces' `.agents/`, which is why the above had
to be done by hand and why it survives the next poll.

For zabs-server, pull the merged checkpoint, publish `GigaClaw.Web/GigaClaw.Web.csproj` in Release, deploy the resulting application set including the rebuilt `GigaClaw.Core.dll`, confirm `AI_DRAFT_SECRET` is set in the service environment, and restart the GigaClaw service. New projects receive corrected embedded templates only after deployment. The owner authorized a post-push SSH check of the automatic deployment; no server edits belong in this checkpoint.

## Graphify decision

`graphify-out/` is ignored while existing generated files remain tracked. Do not untrack or delete them as part of these checkpoints. Any index cleanup requires separate owner approval.

## Checkpoint 6 log

Branch: `codex/checkpoint-6-content-journeys`

Slice 6A is complete in `cfeab60`, `34c9b53`, and `d2f9d19`, merged through PR #19 as `1b3a838`.
Existing tickets now support safe content-type
classification, the catalog and Board state truthful completion outcomes, and the shared
Claude/Codex prompt path includes the requested type. Blog Post now emits a portable image prompt;
Product Review has a distinct evidence-led writer/reviewer contract. Full evidence, propagation, and
the exact 6B resume action are in
[`CHECKPOINT-6-CONTENT-JOURNEYS.md`](CHECKPOINT-6-CONTENT-JOURNEYS.md).

Verification: focused 124/0; Core Release 1,612/0; Core manifest 2/0 plus final template rerun 5/0;
catalog strict and strict-packs exit 0; Eval strict 37 agents, 258 passes, 0 errors, one baselined
`blog-reviewer` prompt-size warning. The post-gate UI smoke found the n8n intake ticket already held
by `groomer`; a bounded Board fix now cancels and supersedes only that system-owned Backlog intake
hop. Its regression suite passed 12/0, and the live local ticket rendered Product Review ->
blog-writer -> blog-reviewer -> blog-seo with the CMS-draft finish line.

Production deployment completed automatically on 2026-08-09. The server gate passed 1,612 Core and
45 Eval tests, published a rebuilt `GigaClaw.Core.dll`, restarted the user service, and returned
HTTP 200 for home and the `gamelifteat` board. Existing workspace template propagation is still
pending and must use non-destructive Agent templates preview/apply for the five paths listed in the
checkpoint plan.

Slice 6B is complete in `daf1785`, merged through PR #21, and deployed as `86b44e4`. Typed image/video
preferences now round-trip through SQLite, REST/OpenAPI, shared Claude/Codex prompts, and both Board
creation surfaces plus ticket edit. Blog, Product Review, Lead Magnet, and Social default to Pexels;
video is opt-in; ComfyUI/OpenMontage always have a portable prompt/upload fallback. Browser smoke
proved local selections and the delivery gate survive reload on desktop and fit a narrow viewport.

Verification: focused 54/0; Release build 0 warnings/0 errors; Core 1,633/0; Eval tests 45/0;
catalog strict and strict-packs exit 0; Eval strict 37 agents, 258 passes, 0 errors, the same one
baselined prompt-size warning. 6B changes no ProjectTemplate file, so it adds no propagation paths.

Production retry evidence: the first gate hit the known server-only project-creation 500 flake after
1,632 Core passes. Automatic rollback/retry then passed all 1,633 Core and 45 Eval tests, rebuilt the
Core DLL, restarted the user service, and returned HTTP 200 for home and the `gamelifteat` board.

Exact next action: implement 6C's durable attachment model and upload/download endpoints, then
connect Pexels, local generation, and manual upload to that shared record with availability-aware
fallbacks.

## End-of-week resume point

Completed on main through checkpoint 5, including the later checkpoint 4 closure: R8 Codex fallback,
CMS taxonomy dispatch, safe `.agents` sync, deliverable catalog/routing, deliverable-first Board UX,
status-aware pipeline progress, bounded content recovery, and the refreshed venture-prep operating
briefs (`298bfbc`). Production is at `1b3a838`, healthy, and all eight existing projects have the
checkpoint 4 workflow graph. Checkpoint 6A's five template changes are not yet propagated to those
existing project workspaces.

Remaining user-experience path:

1. **Checkpoint 6:** implement the incremental content-journey and optional-media plan in [`CHECKPOINT-6-CONTENT-JOURNEYS.md`](CHECKPOINT-6-CONTENT-JOURNEYS.md), then exercise each public deliverable through its truthful finish line and produce the propagation/deployment report.
2. **Checkpoint 7:** security-team join, Codex budgets/model overrides, and specialist promotion after the primary content journey is reliable.

Preserved parallel work, not merged:

- `codex/action-fallback-wip` at pushed commit `6a35ce0` contains Claude's action-scoped fallback
  model work. It has focused coverage but has not received the full release gate and is intentionally
  outside `main`.
- Local stash `graphify post-commit rebuild for action-fallback-wip` preserves Graphify output that
  completed after the WIP commit. Local stash `graphify branch-switch rebuild before checkpoint-4
  completion` preserves the unrelated branch-switch graph rebuild. The older Obsidian/Graphify stash
  remains untouched.

Known operational constraints and blind spots:

- Changing `deliverableType` on an existing ticket does not reassign an in-flight worker. This remains intentionally conservative; the Board's route/progress display exposes the resulting state.
- The only persistent local changes intentionally left outside these commits are `.obsidian/**` and tracked `graphify-out/**`; do not stage, delete, or untrack them without explicit owner approval.

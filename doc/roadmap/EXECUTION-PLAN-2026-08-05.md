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
| 4 | Deliverable-first ticket creation and human-readable pipeline progress | Next | - |
| 5 | Canonical content routes and bounded translator/content recovery | Complete | `0d22cac`, merged by `3737153` |
| 6 | End-to-end journey, propagation report, and deployment handoff | Pending | - |
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
- Codex CLI 0.146.1 was installed under `~/.local`; device authentication and the instance-wide `GIGACLAW_AGENT_HARNESS=codex` override must both be complete before fallback dispatch is live.

Review assignment:

| Model | Scope | Result |
|---|---|---|
| GPT-5.5 | Read-only prompt and contract review | Found stale incomplete-work blocking and incomplete owner-decision requirements; both corrected before merge |

Exact next action remains checkpoint 4. Checkpoint 5 was intentionally completed first at the owner's request; checkpoint 4 is still required for the simplified deliverable-first creation experience.

## Propagation and deployment

Checkpoint 0 does not modify `ProjectTemplate/Agents/**`, so it has no `.agents` propagation list.

Checkpoint 1 propagation list for every existing project:

- `.agents/automations.json` — merge the `cms-dispatch-on-done` changes; do not overwrite unrelated owner automation edits.
- `.agents/blog-writer/SKILL.md` — apply the taxonomy-frontmatter contract.

Do not change any `.agents/*/memory/**` file. After checkpoint 2 is deployed, use the Project Settings **Agent templates** preview before applying these checkpoint 1 updates. Projects without a trustworthy `.agents/packs.lock.json` remain manual-review cases and are not changed.

Checkpoint 2 does not modify `ProjectTemplate/Agents/**`; its propagation list is empty. It does require deploying the rebuilt application and `GigaClaw.Core.dll` so existing projects can use the sync API and Settings flow.

Checkpoint 3 does not modify `ProjectTemplate/Agents/**`; its propagation list is empty. Deploying the rebuilt application adds the ticket column migration, catalog API, and routing behavior.

Checkpoint 5 propagation list for every existing project:

- `.agents/blog-reviewer/SKILL.md`
- `.agents/blog-reviewer/references/ad7-protocol.md`
- `.agents/blog-translator/SKILL.md`
- `.agents/content-writer/SKILL.md`
- `.agents/automations.json` — merge only `content-writer-resume` and `assignee-resume`

These checkpoint 5 paths were propagated on zabs-server with backups. `.agents/blog-writer/SKILL.md` from checkpoint 1 was propagated in the same guarded operation. The checkpoint 1 `cms-dispatch-on-done` automation remains an entry-level manual-review item because `gigaclaw-system` has a distinct local version and `payload-test` intentionally lacks the entry.

For zabs-server, pull the merged checkpoint, publish `GigaClaw.Web/GigaClaw.Web.csproj` in Release, deploy the resulting application set including the rebuilt `GigaClaw.Core.dll`, confirm `AI_DRAFT_SECRET` is set in the service environment, and restart the GigaClaw service. New projects receive corrected embedded templates only after deployment. The owner authorized a post-push SSH check of the automatic deployment; no server edits belong in this checkpoint.

## Graphify decision

`graphify-out/` is ignored while existing generated files remain tracked. Do not untrack or delete them as part of these checkpoints. Any index cleanup requires separate owner approval.

## End-of-week resume point

Completed on main through checkpoint 5: R8 Codex fallback, CMS taxonomy dispatch, safe `.agents` sync, deliverable catalog/routing, bounded content recovery, and the refreshed venture-prep operating briefs (`298bfbc`). Checkpoint 4 was deliberately skipped at the owner's direction and remains next.

Remaining user-experience path:

1. **Checkpoint 4:** deliverable selector in `Board.razor` and `UnifiedBoard.razor`, localized labels, optional advanced assignee override, and readable pipeline progress.
2. **Checkpoint 6:** exercise each public deliverable from ticket creation through its first agent and review/approval exit; produce the final propagation and deployment report.
3. **Checkpoint 7:** security-team join, Codex budgets/model overrides, and specialist promotion after the primary content journey is reliable.

Known operational constraints and blind spots:

- Existing zabs-server projects currently lack `.agents/packs.lock.json`; safe sync correctly reports manual review instead of adopting ownership. A reviewed baseline-adoption workflow is still needed before those projects can automatically receive checkpoint 1 template changes.
- `blog-reviewer` still refers to legacy `category` wording in one protocol section, and `content-writer` does not yet document `categorySlug`/`tags`; handle this with the canonical content-route decision in checkpoint 5.
- Changing `deliverableType` on an existing ticket does not reassign an in-flight worker. This is intentionally conservative; checkpoint 4 must make the distinction visible.
- The zabs-server deploy probe can run before Kestrel binds port 5230 and record a false failure even though the service becomes healthy. Add bounded health-check retries in the deployment tooling when that server-owned script is next edited.
- The only persistent local changes intentionally left outside these commits are `.obsidian/**` and tracked `graphify-out/**`; do not stage, delete, or untrack them without explicit owner approval.

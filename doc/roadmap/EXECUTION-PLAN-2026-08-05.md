# Incremental execution plan - 2026-08-05

This ledger keeps the remaining GigaClaw work recoverable across sessions and usage limits. Update it after every implementation checkpoint. Each checkpoint should produce a focused implementation commit followed by a status/evidence commit when documentation changes are needed.

## Working rules

- Work only in GigaClaw. Do not access or edit ZabalaZone or zabs-server.
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
| 2 | Non-destructive `.agents` synchronization with dry-run drift reporting | Next | - |
| 3 | Deliverable catalog, ticket persistence, and deterministic entry routing | Pending | - |
| 4 | Deliverable-first ticket creation and human-readable pipeline progress | Pending | - |
| 5 | Canonical content routes and bounded translator/content recovery | Pending | - |
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

## Propagation and deployment

Checkpoint 0 does not modify `ProjectTemplate/Agents/**`, so it has no `.agents` propagation list.

Checkpoint 1 propagation list for every existing project:

- `.agents/automations.json` — merge the `cms-dispatch-on-done` changes; do not overwrite unrelated owner automation edits.
- `.agents/blog-writer/SKILL.md` — apply the taxonomy-frontmatter contract.

Do not change any `.agents/*/memory/**` file. Until checkpoint 2 supplies the safe sync operation, propagate these two files by a reviewed merge rather than destructive initialization.

For zabs-server, the owner must pull the merged checkpoint, publish `GigaClaw.Web/GigaClaw.Web.csproj` in Release, deploy the resulting application set including the rebuilt `GigaClaw.Core.dll`, confirm `AI_DRAFT_SECRET` is set in the service environment, and restart the GigaClaw service. New projects receive the corrected embedded templates only after that deployment. Server access remains outside this work.

## Graphify decision

`graphify-out/` is ignored while existing generated files remain tracked. Do not untrack or delete them as part of these checkpoints. Any index cleanup requires separate owner approval.

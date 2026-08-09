# Checkpoint 6 - content journeys and media

**Status:** In progress  
**Branch:** `codex/checkpoint-6-content-journeys`  
**Started:** 2026-08-09

This document is the durable implementation and handoff plan for turning the deliverable selector
into complete, understandable content journeys. It is authoritative for Checkpoint 6 when it is more
specific than the older roadmap text.

## Product decisions

1. The owner chooses an intended content output, not an agent or agent order.
2. Content type must be editable after creation because n8n commonly creates an unclassified ticket.
3. Selecting a type on an unassigned Backlog ticket may derive its entry agent. Changing an active or
   assigned ticket must not silently replace its worker; the UI must make the choice explicit.
4. A route must state its real finish line. `Published` is reserved for an actual publishing action.
   Owner approval alone means `Approved and ready`, not sent or published.
5. Blog Post and Product Review may share editorial machinery, but they require different prompt
   contracts and review criteria. The selected type must reach every agent invocation.
6. Team membership is a capability roster, not an executable workflow. Optional specialists appear
   only when the selected journey needs them. Cross-cutting agents such as committer, evaluator, and
   documentalist are not presented as content stages.
7. Media is a conditional capability, not a separate content deliverable.
8. Images are enabled by default for Blog Post, Product Review, Lead Magnet, and Social Media Content.
   Pexels is the default source. Video is opt-in.
9. Local image/video generation uses the governed ComfyUI/OpenMontage path when the Mac is available.
   Unavailable local hardware never moves a ticket to Blocked: GigaClaw leaves a production prompt
   that can be copied, fulfilled elsewhere, and uploaded to the ticket.
10. Media does not hold delivery unless the owner explicitly selects `Require media before delivery`.

## Current truth at checkpoint start

| Content type | Current route | Actual finish line | Gap |
|---|---|---|---|
| Blog Post | blog-writer -> blog-reviewer -> blog-seo | CMS draft dispatch | Type and media preferences do not reach the prompt |
| Product Review | Same as Blog Post | CMS draft dispatch | No product-review-specific contract or rubric |
| Email Newsletter | email-copywriter -> approval-gatekeeper | Approved label and Done | No send or downstream handoff |
| Social Media Content | growth-writer -> approval-gatekeeper | Approved label and Done | No publish or downstream handoff |
| Lead Magnet | lead-magnet-creator -> approval-gatekeeper | Approved label and Done | No delivery/package handoff |
| Content Series | content-series-planner | Plan file | Does not create or coordinate child deliverables |

`DeliverableType` is persisted and exposed through the API, but the Board only sets it at creation.
The runner prompt currently identifies the ticket by id and title; it does not inject deliverable or
media intent. Chat image uploads are temporary and are deleted after the run, so they cannot serve as
durable ticket assets.

## Target interaction

The create and edit surfaces use the following progressive form:

1. **Content type** - Blog Post, Product Review, Email Newsletter, Social Media Content, Lead Magnet,
   or Content Series.
2. **Delivery outcome** - the configured truthful outcome for that type, initially read-only until
   more than one real destination exists.
3. **Visual assets** - contextual controls:
   - Images: on/off.
   - Image source: Pexels (default), Generate locally, Prompt + upload, or None.
   - Video: on/off.
   - Video source: OpenMontage local production, Prompt + upload, or None.
   - Require media before delivery: off by default.
4. **Advanced routing** - explicit team or assignee escape hatch, visually secondary.

Before saving, the form shows a plain-language route and finish line. Agent identifiers can appear as
diagnostic detail, but they are not choices the owner must understand.

## Target routes

- **Blog Post:** optional research -> writer -> editorial review -> SEO -> optional media -> CMS draft.
- **Product Review:** evidence research -> review-specific writer contract -> evidence review -> SEO
  -> optional media -> CMS draft.
- **Email Newsletter:** email writer -> owner approval -> configured n8n/email handoff, or Approved
  and ready when no integration exists.
- **Social Media Content:** optional trend research -> social writer -> owner approval -> configured
  n8n/social handoff, or Approved and ready.
- **Lead Magnet:** research -> asset creation -> artifact review -> owner approval -> downloadable
  package.
- **Content Series:** series planner -> producer-created child tickets -> each child follows its own
  selected content journey.

These are target routes, not claims about current execution. Each route becomes visible as complete
only when its actions and tests exist.

## Media contract

Every content agent that requests imagery must emit a reusable media brief containing:

- role and placement, such as hero, inline, social crop, or cover;
- Pexels search query;
- generation prompt and negative constraints;
- aspect ratio and output dimensions;
- alt text and caption guidance;
- brand/style constraints;
- source, licence, and attribution requirements.

Pexels failure is non-blocking and records `needs-image`. For local generation, runtime availability
is probed rather than inferred. When unavailable, the same brief is shown in the ticket Media panel
with Copy and Upload actions. Video normally runs as a child ticket because direction, generation,
composition, checkpoints, and independent review have their own lifecycle.

Durable ticket attachments must eventually record the stored path, media kind and role, source,
licence/attribution, alt text, generation prompt, review state, and parent ticket. The temporary chat
image path must not be reused as durable storage.

## Incremental slices

### 6A - truthful classification and prompt context

- Add Content type to ticket edit.
- Derive the entry agent only for a safe unassigned Backlog classification; preserve active work.
- Replace ambiguous route endpoints with truthful outcomes.
- Inject content type and route intent into fresh and resumed automation prompts.
- Give Product Review a distinct writing/review contract.
- Add focused service, runner-prompt, Board, route, and template tests.

**Exit proof:** an n8n-created Backlog ticket can be classified from the Board, reaches the correct
entry agent when started, and its agent can distinguish Blog Post from Product Review.

### 6B - media preferences and portable briefs

- Persist typed image/video preferences with conservative defaults by content type.
- Expose the same controls on create and edit.
- Inject the media contract into agent prompts and require portable briefs.
- Show whether the selected media path is automatic, local, prompt-only, or manual.
- Add validation and API/OpenAPI round-trip tests.

**Exit proof:** a Blog Post defaults to Pexels images; selecting local generation or prompt + upload
survives a reload and produces an actionable media brief without blocking when the Mac is offline.

### 6C - durable assets and conditional execution

- Add durable ticket attachment storage and upload/download endpoints.
- Connect Pexels output to the attachment record and preserve attribution.
- Connect approved ComfyUI/OpenMontage output to the same record.
- Add local availability reporting and prompt fallback.
- Gate dispatch only when `Require media before delivery` is true.

**Exit proof:** Pexels, local generation, and manual upload converge on one reviewed attachment model.

### 6D - complete non-blog journeys

- Implement explicit downstream outcomes for Email, Social, and Lead Magnet.
- Make Content Series create and coordinate typed child tickets.
- Keep integration-dependent endpoints honest when a destination is not configured.
- Exercise every public deliverable from classification through its declared finish line.

**Exit proof:** every catalog option has a tested journey and the UI never claims an unperformed send
or publish action.

## Checkpoint and commit discipline

- Commit this plan before implementation.
- Commit each slice only after its focused tests pass; push each recoverable checkpoint.
- Update the execution ledger with commit ids, tests, sub-agent assignments, propagation files, and
  the exact next action.
- After any `ProjectTemplate/Agents/**` edit, regenerate the embedded manifest and report the exact
  safe-sync propagation list. Never edit `.agents/*/memory/**`.
- Preserve `.obsidian/**`, tracked `graphify-out/**`, and unrelated owner changes.
- Run the full Core and Eval suites before merging a completed slice.

## Decisions still required later

These do not block 6A or 6B:

- Which n8n/email destination represents a successful Email Newsletter delivery.
- Which social publishing destinations are allowed to auto-publish versus stop at approval.
- The canonical downloadable package and landing-page handoff for Lead Magnets.
- The default OpenMontage workflow/model profile for optional image and video generation.

Until configured, the correct terminal wording is `Approved and ready`.

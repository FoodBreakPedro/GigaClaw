---
type: content-engine-config
venture_id: hyperlane
display_name: Hyperlane Travels
status: pilot
privacy: business
brand_file: ventures/hyperlanetravels/brand.md
vault_content_root: 60-Content-Pipeline/hyperlane
allowed_formats:
  - blog
  - social-set
  - image-brief
  - pdf-outline
  - website-copy
  - newsletter
default_channels:
  - hyperlane-blog
  - instagram
  - facebook
  - linkedin
  - pdf
  - newsletter-draft
default_risk: medium
default_model_tier: T1
budget_priority: standard
source_policy: sourced-claims-required
approval_policy: human-publish-in-cms
publish_targets:
  - "hyperlanetravels-cms"   # via POST /api/ai/draft → status: review; Pedro publishes in /admin
# out_of_scope kept for the deployed runner (legacy key = union of scope_block + scope_topical);
# newer split-schema readers use scope_block/scope_topical below. Do not remove until the live
# n8n Prime Job Runner is updated to the split schema.
out_of_scope:
  - client-message
  - lead-intake
  - proposal
  - itinerary
  - booking
  - commission
  - crm
  - supplier-registration
  - payment
  - supplier-ops
scope_block:
  - client-message
  - lead-intake
  - crm
  - supplier-registration
  - supplier-ops
scope_topical:
  - proposal
  - itinerary
  - booking
  - commission
  - payment
sentinel_checks:
  - privacy
  - brand-isolation
  - claims
  - secrets
  - content-scope
  - external-action
---

# Hyperlane Travels Content Engine Config

## Purpose

This config enables Hyperlane Travels content production only: blog posts, social drafts, image briefs, PDF outlines, website copy, newsletter-style marketing copy, and repurposing. It does not authorize client/customer/business operations.

## Explicit Non-Goals

- No lead intake
- No CRM updates
- No commission tracking
- No client or customer messages
- No itinerary proposals
- No booking or payment work
- No supplier registration or supplier operations
- No publishing without Pedro's final CMS review/publish action
- No CMS upload of draft text, slug, media, or files until QA/Sentinel passes

## Scope Enforcement

`scope_block` terms are hard-blocked deterministically (never performed, never even discussed as an
instruction to act). `scope_topical` terms are legitimate ARTICLE SUBJECT MATTER (e.g. "how booking
windows work") — they are not deterministically blocked, but every request is still screened by the
Sentinel pre-check for operational intent before drafting. See
`docs/superpowers/specs/2026-06-30-content-scope-guard-design.md`.

## Pilot Path

The first supported path is:

`trend/news/topic signal -> new topic idea -> Pedro topic approval -> local drafting/media staging -> QA/Sentinel -> CMS review record -> Pedro publishes in /admin`

Draft text, slug candidates, source notes, and media candidates stay in
`60-Content-Pipeline/hyperlane/` or a ZabsAIOS temp staging folder until the item
passes QA/Sentinel. `ready-to-publish` means the draft has been loaded into Payload
CMS as a `review` record for Pedro's final `/admin` review.

If QA, Sentinel, source sufficiency, media suitability, model doubt, or human
judgment blocks the item, it moves to `revisement` with a reason label/detail for
a human decision or edit before either cancellation or another polishing/QA pass.

## Cost Policy

Use deterministic checks and existing vault/repo context first. Use T1 cheap models for first drafts. Create a recommendation before any T2/T3 escalation unless Pedro explicitly requests the higher-tier pass.

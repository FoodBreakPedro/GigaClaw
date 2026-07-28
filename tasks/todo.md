# Todo: GigaClaw ↔ n8n ↔ PayloadCMS pipeline

Plan: [plan.md](plan.md) · Status: **build-out complete (2026-07-28)** — code/config/tests done; live-server verification pending. Executed via the parallel wave plan (see decisions log below). Legend: ✅ done+tested · 🔶 authored, needs live verification (n8n workflows can only be validated statically without the server).

## Phase 1 — Egress path (highest risk first)

- [x] **1. Payload Posts schema** — `sourceTicket`, `sourceSystem`, venture-scoped `Categories` collection + `category` rel; `VENTURE_SLUGS` opened, Hyperlane rejection dropped; migration SQL verified against prod Postgres via BEGIN/ROLLBACK dry-run · ✅ 147/147 ZabalaZone tests
- [x] **2. `/api/ai/draft` contract v2** — `contentType`/`sourceTicket` accepted, PATCH keyed on `sourceTicket` (refuses `published` unless forced), legacy body still works; plus `GET ?needsImage=1` and `POST /api/ai/media` (bearer-gated image-service surface) · ✅
- [x] **3. `httpRequest` ActionSpec in GigaClaw core** — typed config + response capture (`{http.status}`, `{http.body.*}`) + `SecretRef` env-var refs + `FailureComment`/`FailureStatus`; shared `ActionTemplate.Render`; drive-by fixes: missing `createTicket` post-run arm, inline-Script+Arguments pwsh bug · ✅ G1 gate passed (Pedro, 2026-07-28)
- [x] **4. Egress automation on `gamelifteat`** — `cms-dispatch-on-done` in template + repo `.agents`; `{draft.*}` frontmatter placeholders (JSON-escaped, parse failure blocks dispatch); label only after 2xx · ✅ code/tests · 🔶 live round-trip on :5232
- [x] **5. Receipt writeback** — CMS `adminUrl` comment on success; failure → comment + `Blocked` via `FailureComment`/`FailureStatus` · ✅ code/tests · 🔶 live

### ▸ Checkpoint A — replaced by hard gate G1 (passed). Remaining live round-trip check folded into the :5232 verification list.

## Phase 2 — Ingress path

- [x] **6. Venture project scaffold script** — `scripts/new-venture.{sh,ps1}` + new `POST /api/projects/{slug}/initialize` endpoint (template copy + member seeding + engine reload via API) · ✅ · 🔶 live run
- [x] **7. n8n ingress normalizer** — `GigaClawIngress01` (22 nodes); label name→id resolution; explicit project-existence guard (GigaClaw lazily creates DBs for any slug) · 🔶 import + live test
- [x] **8. Dedup gate** — 60% token overlap vs open tickets, threshold in Config node, near-miss logging · 🔶 (same workflow)
- [x] **9. Telegram + schedule capture** — `GigaClawCapture01/02`; webhook-based (matches production pattern), whitelist preserved; normalizer must be invoked once-per-item (documented) · 🔶
- [ ] **13. Discord capture** — awaiting Checkpoint-B ack (async) · S · deps: 7

### ▸ Checkpoint B — async review: ingress workflows authored + statically validated; live Telegram test pending server import.

## Phase 3 — Board as quality gate

- [x] **10. Writer + reviewer automations** — `content-writer` agent + SKILL contracts (AD-7 draft-in-description, `imagePrompt` always emitted), reviewer pass→`Done`+`ready-for-cms`, revision budget via comment markers, exhaustion→`Blocked`; AD-9 models seeded via `ProjectTemplate/Agents/models.json` (haiku-4-5 / sonnet-4-6 / opus-4-8) through shared `EnsureAgentMembersAsync`; `auto-approve-ungated` keeps the unattended loop while gated labels still need the human gatekeeper · ✅ 587/587 · 🔶 unattended loop on :5232
- [x] **11. Config-driven trend cron** — `GigaClawTrendIntake01` (SearXNG, per-venture profiles + fallback, single-flight lock) · 🔶
- [x] **11b. Pexels first-pass image** — `GigaClawImage01` (40 nodes), runs off the new bearer-gated image API (no service account needed); failure labels `needs-image`, never blocks · 🔶

### ▸ Checkpoint C — async review pending: unattended loop + AD-9 cost check need the live instance.

## Phase 4 — Scale and harden

- [x] **12. Template drift check** — `tools/check-automation-drift.sh` + allowlist + docs; baseline drift of the repo's own `.agents` copy recorded (12 missing / 9 changed vs template) · ✅
- [x] **14. Operational alerting** — `GigaClawOpsAlerts01`; dedup per ticket, per-run alert cap, comment-template-based cause classification · 🔶
- [~] **15. HyperlaneTravels CMS migration** — analysis + importer DONE ahead of schedule: runbook (`tasks/hyperlane-migration-runbook.md` — 19 CMS posts + 40 event posts, 4 stale→draft), idempotent `ZabalaZone/scripts/import-hyperlane-posts.mjs` (dry-run verified against live API) · remaining: staging rehearsal + **cutover behind hard gate G2** (note: no staging DB on Supabase Free — rehearsal protocol TBD at G2)
- [x] **16. Draft archival** — Obsidian archive appended to dispatch chain (`archive-draft.ps1`, failure can never block dispatch), `{powershell.stdout}` chain value, `tools/backfill-archive.sh` · ✅ 587/587 · 🔶 live
- [~] **17. `karalungaming` rollout** — prep done (`tasks/venture-prep/karalungaming/`: grounded BRAND.md/VOICE.md from ZabsAIOS brand contract + ROLLOUT.md); live scaffold + full-loop run pending server
- [x] **18. OpenMontage image upgrade sweep** — in progress at last update; tailnet probe semantics per AD-8 · 🔶

### ▸ Checkpoint D — pending: live deployment, Hyperlane cutover (G2), docs sync.

---

## Decided

- **Draft lives in the ticket description** (AD-7) — frontmatter header + markdown body. Critique goes in comments; revisions replace the body. Archival to Obsidian is additive → Task 16 (done; target = Obsidian per default).
- **`pedrorzabala` gets a CMS presence** — treated as a full venture from Task 1 onward.
- **`karalungaming` is in scope at lowest priority** — included in Task 1's schema so nothing changes later, but scaffolded last → Task 17.
- **Images are progressive** (AD-8) — Pexels always ships; the writer emits an `imagePrompt`; OpenMontage upgrades opportunistically when the Mac answers over tailnet. Never blocks dispatch. Forces an update path onto Task 2.
- **Migration scope (Pedro, 2026-07-28)** — 19 CMS posts + 40 file-based event posts; Events/Products/AffiliatePartners/Leads stay on the old instance (read-only). Media recovered via live Payload API.
- **Taxonomy (Pedro, 2026-07-28)** — venture-scoped `Categories` collection; `contentType` stays the small format enum. Hyperlane's 7 categories = Categories rows; mapping affiliate_reviews→review, premium/risk_planning→guide, rest→article.
- **Gates (Pedro, 2026-07-28)** — exactly two hard human gates: G1 httpRequest response-capture design (PASSED) and G2 Hyperlane production cutover (pending). Checkpoints B/C/D are async reviews.

**Topology** — GigaClaw + n8n run 24/7 on the Linux server; OpenMontage/ComfyUI runs on the Mac, reachable over Tailscale when it's up; PayloadCMS is cloud. The 24/7 path must never depend on the Mac.
- **T1/T2 is retired** (AD-9) — model is a property of the agent member, seeded from `ProjectTemplate/Agents/models.json`. Haiku for mechanical work, Sonnet for writing, Opus for judgment.

Rollout order: `gamelifteat` (pilot) → `gamepowergym` → `zabsconsulting` → `pedrorzabala` → `hyperlanetravels` (needs Task 15 cutover) → `karalungaming`.

## Blocked on answers

- **Checkpoint-B ack** → unlocks Task 13 (Discord capture).
- **G2 cutover** (with it: the staging-rehearsal protocol, since Supabase Free has no branching — options: temporary Pro upgrade, or dry-run + prod import with rehearsed rollback via migration `down` + `sourceSystem='hyperlane-import'` cleanup).
- Model-assignment confirmation against real costs → revisit at live Checkpoint C.

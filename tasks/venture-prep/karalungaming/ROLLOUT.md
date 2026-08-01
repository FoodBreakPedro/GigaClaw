# Karalun Gaming — Venture Rollout Checklist

**Status**: Pre-launch (Task 17 of GigaClaw content-pipeline plan)  
**Venture Slug**: `karalungaming`  
**Brand Name**: Karalun  
**Game Focus**: Star Wars: Galaxy of Heroes (SWGOH) era planning  
**Domain**: `planner.gamelifteat.com` (subdomain of GLE per 2026-07-02 decision)

This checklist orchestrates the launch of the Karalun Gaming venture across GigaClaw and ZabsAIOS. Completion means the venture can accept tickets, dispatch agents, and route finished drafts to the CMS.

---

## Phase 0: Pre-flight (Preparation)

These steps can run anytime; they do not require GigaClaw or n8n to be running yet.

### Step 0.1: Verify Karalun context exists
- [ ] Read `ZabsAIOS/ventures/karalungaming/brand.md` (v1 active, 2026-07-02)
- [ ] Read `ZabsAIOS/ventures/karalungaming/activation-kit.md` (includes Reddit launch draft)
- [ ] Read Obsidian Vault `50-KaralunGaming/index.md` and `performance/scorecard.md`
- [ ] Confirm Era Planner repo is ready: `GitHub/Karalun's Era Planner` — built, branded, and ready to deploy

**Why**: These are the source-of-truth documents. They inform every subsequent step.

### Step 0.2: Confirm BRAND.md and VOICE.md are written
- [ ] BRAND.md exists in this directory (context + pillars + voice guidance)
- [ ] VOICE.md exists in this directory (tone, structure, banned clichés, math rules)
- [ ] Both are grounded in ZabsAIOS venture docs or marked `[FILL]` for placeholders

**Why**: Agents read these verbatim before writing or reviewing content.

### Step 0.3: Confirm workspace path
- [ ] Workspace directory exists or will be created: `[FILL — provide absolute path when workspace is allocated]`
- [ ] Workspace is either a new directory or an existing git repo

**Why**: `new-venture.sh` needs a path to bind to the project.

---

## Phase 1: Project Scaffold (Live GigaClaw required)

These steps require GigaClaw to be running at `http://localhost:5230` (or configured via `--base-url`).

### Step 1.1: Create GigaClaw project
- [ ] Ensure GigaClaw is running: `./run.sh` from `/Users/pedrozabala/Documents/Development/Github Repos/GigaClaw`
- [ ] Run: 
  ```bash
  /Users/pedrozabala/Documents/Development/Github Repos/GigaClaw/scripts/new-venture.sh karalungaming \
    --base-url http://localhost:5230 \
    --workspace <path-from-step-0.3>
  ```
- [ ] Script should report `✓ Project created: karalungaming` and label seeding (`ready-for-cms`, `dispatched`, `approved`, `blocked`)
- [ ] If project exists (idempotent), script reports that and exits 0

**What it does**: 
- POSTs `/api/projects` to create the project in GigaClaw's registry
- Seeds four labels for the venture
- POSTs `/api/projects/karalungaming/initialize` to copy agent templates, create agent members, and init git in the workspace

**Exit code**: 0 = success, 1 = network error or invalid slug

### Step 1.2: Verify project in GigaClaw UI
- [ ] Open browser to `http://localhost:5230`
- [ ] Verify `karalungaming` project appears in the home sidebar under Projects
- [ ] Click into `karalungaming` — the board should show empty columns (Backlog, Todo, InProgress, Blocked, Scheduled, Review, Done)
- [ ] Click Automations page — should show template automations copied from `ProjectTemplate/Agents/automations.json`

**Why**: Confirms the API calls succeeded and the project is ready to receive work.

### Step 1.3: Verify agent members and default models
- [ ] In the `karalungaming` board UI, click Settings → Members
- [ ] Verify these agents exist with appropriate `DefaultModel` assignments (per AD-9):
  - `writer` (Sonnet or Haiku — [FILL: confirm per deployment])
  - `reviewer` (Opus or Sonnet — [FILL: confirm per deployment])
  - (other agents per template, e.g., `trend-researcher`, `committer`, etc.)
- [ ] If default models are not set, update them via the UI or API before proceeding

**Why**: Model assignment gates cost and quality. Verify now to catch configuration errors early.

---

## Phase 2: Brand & Voice (Local workspace only)

These steps run in the workspace and do not require GigaClaw to be running (though keeping it running for verification is convenient).

### Step 2.1: Copy BRAND.md and VOICE.md into workspace
- [ ] `cp /Users/pedrozabala/Documents/Development/Github Repos/GigaClaw/tasks/venture-prep/karalungaming/BRAND.md <workspace>/.agents/BRAND.md`
- [ ] `cp /Users/pedrozabala/Documents/Development/Github Repos/GigaClaw/tasks/venture-prep/karalungaming/VOICE.md <workspace>/.agents/VOICE.md`
- [ ] Verify files exist: `ls -la <workspace>/.agents/BRAND.md <workspace>/.agents/VOICE.md`

**Why**: Agents read from `.agents/BRAND.md` and `.agents/VOICE.md` in the workspace during dispatch. These files must be present before any agent runs.

### Step 2.2: Verify `.agents/preamble.md` exists and references venture
- [ ] Read `<workspace>/.agents/preamble.md` (should be copied from template during initialize)
- [ ] Confirm it includes instructions for language, git commits, GigaClaw API, and board discipline
- [ ] If missing, copy from `ProjectTemplate/Agents/preamble.md`

**Why**: The preamble is injected into every agent run. It sets shared expectations.

### Step 2.3: Verify template files are in place
- [ ] Check that `.agents/` contains:
  - `preamble.md`
  - `BRAND.md` (just copied)
  - `VOICE.md` (just copied)
  - `automations.json`
  - `{agent}/SKILL.md` (one per agent: writer, reviewer, etc.)
  - `{agent}/memory/MEMORY.md` (scored index of lessons)
- [ ] If any are missing, copy them from `ProjectTemplate/Agents/`

**Why**: Incompleteness breaks agent dispatch.

---

## Phase 3: Ventures Database & Ingress Setup (Live ZabsAIOS required)

These steps prepare the venture for ingress (n8n Trend Intake) and the Ventures database record.

### Step 3.1: Verify Ventures record exists in ZabsAIOS
- [ ] Check `/Users/pedrozabala/Documents/Development/Github Repos/ZabsAIOS/server/ventures.mjs` for a `karalungaming` entry
- [ ] Entry should include:
  ```javascript
  { id: "karalungaming", name: "Karalun Gaming", dir: "50-KaralunGaming", pipe: "karalungaming", hue: "violet" }
  ```
- [ ] If missing, add the entry and commit (or note for the ZabsAIOS owner to add)

**Why**: The Ventures registry is the source of truth for venture identity. Ingress normalizer uses it to resolve slug → name → hue for ticket creation.

**Source code location**: `/Users/pedrozabala/Documents/Development/Github Repos/ZabsAIOS/server/ventures.mjs` (line ~[FILL: provide line number])

### Step 3.2: Verify Ventures DB record is seeded (ZabsAIOS init script)
- [ ] ZabsAIOS init step: `npm run seed:ventures-categories` (per `ZabsAIOS/scripts/seed-ventures-categories.mjs`)
- [ ] This script seeds the `Ventures` table with all venture slugs, names, and metadata
- [ ] Confirm `karalungaming` row is present in the DB (exact query depends on ORM — [FILL: provide query or script])

**Why**: The database record drives venture resolution in n8n ingress.

**Already handled by**: ZabsAIOS `scripts/seed-ventures-categories.mjs` — no GigaClaw action needed; just confirm it ran.

### Step 3.3: Add Karalun to n8n Trend Intake Config node (zero node edits)
- [ ] Open n8n at `http://[n8n-host]:5678` (typically the Linux server, accessible via Tailscale if working remotely)
- [ ] Open the `GigaClaw Trend Intake` workflow (or the designated ingress workflow)
- [ ] Locate the **Config node** (typically a Code or Webhook node that defines `VENTURES`)
- [ ] Verify `karalungaming` is listed in the `VENTURES` array or constant with:
  - `slug: "karalungaming"`
  - `name: "Karalun Gaming"`
  - `watch_topics: [<list from BRAND.md>]` (e.g., "SWGOH new era announcements", "r/SWGalaxyOfHeroes hot threads", etc.)

**Format reference**: See `GigaClaw_Trend_Intake.README.md` for the config format (typically YAML or JSON key-value).

**Document location**: [FILL: provide path to n8n config docs or the workflow README]

**Zero node edits**: The config should support adding a venture without creating or modifying nodes — only data entry in the config structure.

**Why**: This enables the trend normalizer to accept `karalungaming` tickets from the ingress pipeline.

---

## Phase 4: Full-Loop Verification (Live GigaClaw + n8n required)

This phase runs a single ticket through the complete pipeline: ingress → write → review → CMS.

### Step 4.1: Create a test ticket manually
- [ ] Open GigaClaw `karalungaming` board at `http://localhost:5230`
- [ ] Click **New ticket**
- [ ] Fill in:
  - **Title**: "Test: Basic New Republic Leveling Guide"
  - **Description**: "Write a beginner's guide to leveling the New Republic units in era 08. Include pack value math for $10 budget. Use the Planner tool examples."
  - **Labels**: `(leave blank — will be added by automation)`
  - **Priority**: `Required`
  - **Assignee**: `writer`
- [ ] **Create** the ticket; it should land in **Backlog**

**Why**: A manual ticket confirms the board is functional and agents can be assigned work.

**Ticket reference**: Note the ticket ID (e.g., `#42`) for the next steps.

### Step 4.2: Move ticket to Todo and trigger writer dispatch
- [ ] Drag the ticket from **Backlog** to **Todo**
- [ ] GigaClaw automation engine should detect `TicketInColumnTrigger` for the `writer` agent
- [ ] The writer agent should dispatch (visible in the Run drawer on the right)
- [ ] Watch the agent run stream in the Run drawer; it should:
  - Read `.agents/preamble.md`, `BRAND.md`, `VOICE.md`, and the ticket description
  - Generate a markdown draft with YAML frontmatter (title, excerpt, SEO keyword, etc.)
  - Write the draft into the ticket **description** (replacing the test prompt)

**Timeline**: ~2–5 minutes depending on agent model and API latency

**Exit**: Agent should report success, ticket should still be in **Todo**, description should contain the finished draft

**Check points**:
- [ ] Draft is markdown with YAML frontmatter (starts with `---`, has `title:`, `description:`, etc.)
- [ ] Draft length is >1000 words (confirm it's a full guide, not a stub)
- [ ] Math is sourced (claims like "Pack X costs Y crits" have links or explanations)
- [ ] Tone matches VOICE.md (precise, generous, zero drama)

**If writer fails**: Check the run log. Common issues:
- API overload → retry after 30s
- Model returned nothing → agent prompt is unclear (update SKILL or preamble, re-run)
- Workspace not found → confirm workspace path in Step 1.1

### Step 4.3: Move ticket to Review and trigger reviewer dispatch
- [ ] Drag the ticket from **Todo** to **Review**
- [ ] Reviewer agent should dispatch (watch Run drawer)
- [ ] Reviewer should:
  - Read the draft in the ticket description
  - Check for BRAND/VOICE compliance (no clichés, math is sourced, tone is right)
  - Either approve (add comment "✓ Approved") or request changes (list specific revisions needed)

**Timeline**: ~2–3 minutes

**Exit states**:
- **Approved** (comment says so) → move to next step (Step 4.4)
- **Changes requested** → move back to **Todo**, writer revises, repeat from 4.3
- **Blocked** → something went wrong; check the log and fix

**Check point**:
- [ ] Reviewer comment clearly explains the verdict (not just "looks good")

### Step 4.4: Move ticket to Done and trigger CMS dispatch
- [ ] Drag the ticket from **Review** to **Done**
- [ ] An automation should fire an `httpRequest` action to POST the draft to the CMS
- [ ] The request should call `${GIGACLAW_API_URL}/api/projects/karalungaming/tickets/{id}/dispatch` or the equivalent CMS ingress endpoint
- [ ] Request payload should include:
  - Venture slug: `karalungaming`
  - Markdown body: (the draft from the ticket description)
  - Frontmatter: (title, excerpt, seo_keyword, contentType, etc., parsed from YAML header)
  - Ticket reference: (e.g., `karalungaming#42`)

**CMS endpoint** (depends on ZabalaZone PayloadCMS contract):
- [FILL: provide exact path to `/api/ai/draft` docs or the CMS integration guide]
- Expected response: `{ id: "<post-id>", slug: "<post-slug>", adminUrl: "<cms-url>#posts/{id}" }`

**Verification**:
- [ ] CMS responds with 200 OK (check automation action logs)
- [ ] Response is written back to the ticket as a comment (e.g., "Post created: <adminUrl>")
- [ ] Ticket is now in **Done** with all work complete

**If dispatch fails**:
- Check the automation action log for HTTP status and response body
- Common issues:
  - `401 Unauthorized` → CMS API key missing or expired (check GigaClaw project settings for `CmsApiKey`)
  - `400 Bad Request` → frontmatter parsing failed (check YAML format in draft)
  - `5xx` → CMS is down or overwhelmed (retry after checking server status)

### Step 4.5: Verify the Post exists in the CMS
- [ ] Log into ZabalaZone PayloadCMS at [FILL: provide CMS URL]
- [ ] Navigate to **Posts** collection
- [ ] Filter by venture: `karalungaming`
- [ ] Confirm the test post appears with:
  - Title: "Basic New Republic Leveling Guide" (or similar)
  - Venture: `karalungaming`
  - Status: `Draft` (not yet published; that's a separate approval step in the CMS)
  - Source ticket: `karalungaming#42` (or however the field is labeled)
- [ ] Read the post body — confirm it matches the draft from the ticket

**Why**: This proves the entire pipeline worked: GigaClaw → agent write → agent review → CMS ingestion.

**Post should NOT yet be live** (published). CMS publication is a separate gate, usually requiring an explicit CMS-side review step or admin action.

---

## Phase 5: Template Drift Check

This step ensures the karalungaming project's automations stay in sync with the template.

### Step 5.1: Run automation drift check
- [ ] Ensure GigaClaw is still running (or run a fresh instance)
- [ ] Run:
  ```bash
  cd "/Users/pedrozabala/Documents/Development/Github Repos/GigaClaw" && dotnet run --project GigaClaw.Catalog -c Release -- check --project <workspace>
  ```
- [ ] Should report:
  ```
  No drift.

  DRIFT: missing=0 modified=0 extra=0 allowlisted=0
  ```
  (or list any intentional overrides in `allowlisted` count)

**Exit code**: 0 = no unallowlisted drift, 1 = drift detected

**If drift is found**:
- [ ] Read the report (lists which automations differ)
- [ ] Either:
  - **Merge template changes** into `<workspace>/.agents/automations.json`, OR
  - **Add allowlist entry** in `<workspace>/.agents/automation-overrides.json` with a ticket reference explaining why
- [ ] Re-run the check to confirm it passes

**Why**: Drift indicates that karalungaming's automation setup is out of sync with the shared template. This can cause agents to misbehave or miss automated tasks.

---

## Phase 6: Living Definition (Post-Launch)

These steps mark the venture as "live" and ready for continuous operation.

### Step 6.1: Update Obsidian Vault scorecard
- [ ] Edit `50-KaralunGaming/performance/scorecard.md` (or create if missing):
  - Update `status: active` (already set)
  - Update `updated: [today's date]`
  - Set frontmatter KPI fields to 0 (or actual values if Planner is already live):
    - `visitors_7d: 0` → update once Planner receives traffic
    - `content_published_7d: 0` → increment with each post
    - `revenue_mtd: 0` → update if any revenue is booked

**Why**: The scorecard is the single source of truth for venture health. Mission Control (ZabsAIOS) reads it automatically.

### Step 6.2: Document any special configuration
- [ ] Add a note to this checklist under "Special Ops" (see below) if karalungaming requires any custom automations, allowlisted drifts, or non-standard setup
- [ ] This helps the next person understand why the venture differs from the template, if it does

### Step 6.3: Archive this checklist
- [ ] Move this file to the workspace: `cp ROLLOUT.md <workspace>/.gigaclaw/ROLLOUT_COMPLETED_[date].md`
- [ ] Or commit it to the workspace git repo: `git add ROLLOUT.md && git commit -m "docs: karalun rollout completion (Task 17)"`

**Why**: Provides an audit trail and helps troubleshoot future issues ("did we skip a step?").

---

## Rollout Status Tracking

| Step | Status | Notes |
|---|---|---|
| 0.1: Verify Karalun context | [ ] | |
| 0.2: BRAND.md & VOICE.md ready | [ ] | |
| 0.3: Workspace path confirmed | [ ] | |
| 1.1: Project scaffold | [ ] | |
| 1.2: Verify in GigaClaw UI | [ ] | |
| 1.3: Agent members & models | [ ] | |
| 2.1: Copy BRAND/VOICE into workspace | [ ] | |
| 2.2: Preamble exists | [ ] | |
| 2.3: Template files complete | [ ] | |
| 3.1: Ventures record verified | [ ] | |
| 3.2: DB seeded | [ ] | |
| 3.3: n8n Config updated | [ ] | |
| 4.1: Manual test ticket created | [ ] | Ticket ID: ___ |
| 4.2: Writer dispatch → draft complete | [ ] | |
| 4.3: Reviewer dispatch → approved or revised | [ ] | |
| 4.4: CMS dispatch → post created | [ ] | Post ID: ___ |
| 4.5: Verify post in CMS | [ ] | |
| 5.1: Automation drift check passes | [ ] | |
| 6.1: Scorecard updated | [ ] | |
| 6.2: Special config documented | [ ] | |
| 6.3: Checklist archived | [ ] | |

**Rollout Date**: [FILL: insert date when Phase 4 completes]  
**Verified By**: [FILL: insert name of person who ran the checklist]

---

## Special Ops & Troubleshooting

### Scenario: Writer agent timeout
**Symptom**: Run drawer shows agent stuck at "generating draft" for >5 minutes, then fails.  
**Fix**: Check GigaClaw server logs for API rate limiting. If Claude API is throttled:
1. Wait 30–60s
2. Manually re-dispatch writer via UI: ticket panel → Run → "Retry"
3. If persistent, reduce agent batch size or stagger tickets (not a calamity — inherent to cloud API)

### Scenario: CMS endpoint 401 Unauthorized
**Symptom**: httpRequest action fails with `401` when posting to CMS.  
**Fix**: Check GigaClaw project settings for `CmsApiKey`. Regenerate if expired:
1. Open GigaClaw UI → karalungaming → Settings → Integrations
2. Look for "CMS API Key" field
3. If blank, ask the ZabalaZone owner to provide a token or regenerate it
4. Paste into GigaClaw, save, retry the ticket

### Scenario: Reviewer says "draft is too long"
**Symptom**: Reviewer returns changes asking for condensed version.  
**Fix**: Update the writer's SKILL to cap output at a specific word count (e.g., 2000 words max). Recommend in the writer SKILL:
```
Draft target: 1500–2000 words (era guide with tooling examples).
If draft exceeds 2500 words, split into multiple posts.
```

### Scenario: n8n Trend Intake drops karalungaming tickets
**Symptom**: Trend watch runs, but no tickets are created in GigaClaw for `karalungaming` trend topics.  
**Fix**: Verify Step 3.3 was completed. Re-check the n8n Config node:
1. Is `karalungaming` in the `VENTURES` array?
2. Are watch_topics defined?
3. Did the ingress normalizer encounter an error? (check n8n execution history for the trigger)

### Scenario: "Missing template files"
**Symptom**: Agent dispatch fails with "BRAND.md not found" or similar.  
**Fix**: Re-run Step 2.1 and 2.3. Confirm files are in `<workspace>/.agents/`, not elsewhere. Relative path from agent invocation must be `.agents/BRAND.md`.

---

## Completion Criteria (Task 17)

Karalun Gaming is **live** when all of the following are true:

1. [ ] Project scaffolded in GigaClaw with venture-specific `BRAND.md` / `VOICE.md` (Phases 0–2)
2. [ ] `Ventures` record exists and ingress accepts `karalungaming` slug (Phase 3)
3. [ ] One ticket completes the full loop: ingress → write → review → CMS (Phase 4)
4. [ ] No unallowlisted automation drift is present (Phase 5)
5. [ ] Scorecard updated with baseline metrics (Phase 6)

**Non-requirement**: The Era Planner is live at `planner.gamelifteat.com` (handled separately; GigaClaw is content workflow, not product deployment).

---

## Appendix: External Dependencies & Contact Points

| System | Owner | Status | Notes |
|---|---|---|---|
| **GigaClaw** (board, agents, dispatch) | Pedro | Development | Runs on `localhost:5230` by default; accessible on Tailscale from other machines |
| **n8n Trend Intake** (ingress) | [FILL: owner] | [FILL] | Runs on Linux server; accessible at `http://[server]:5678` |
| **ZabalaZone PayloadCMS** (CMS egress) | [FILL: owner] | [FILL] | Cloud instance; requires API key in GigaClaw config |
| **ZabsAIOS** (Ventures registry, brand docs) | Pedro | Production | Source of truth for venture metadata and brand contracts |
| **Obsidian Vault** (scorecard, context) | Pedro | Production | Handshake with 50-KaralunGaming folder and sync with mission control |
| **Tailscale** (network) | [FILL: operator] | [FILL] | Connects Mac (dev) to Linux server (prod) |

---

## References

- **Brand & Voice**: `BRAND.md` and `VOICE.md` (this directory)
- **GigaClaw Docs**: `/Users/pedrozabala/Documents/Development/Github Repos/GigaClaw/doc/`
- **GigaClaw API**: `http://localhost:5230/api/docs` (live docs, OpenAPI)
- **ZabsAIOS Ventures**: `/Users/pedrozabala/Documents/Development/Github Repos/ZabsAIOS/server/ventures.mjs`
- **ZabsAIOS Brand**: `/Users/pedrozabala/Documents/Development/Github Repos/ZabsAIOS/ventures/karalungaming/brand.md`
- **Obsidian Vault**: `/Users/pedrozabala/Documents/Development/Projects/Obsidian Vault/50-KaralunGaming/`
- **ProjectTemplate**: `/Users/pedrozabala/Documents/Development/Github Repos/GigaClaw/ProjectTemplate/`
- **Automation Drift Check**: `dotnet run --project GigaClaw.Catalog -- check --project <workspace>` (doc/automation-drift-check.md) — `tools/check-automation-drift.sh` was retired 2026-08-01

---

**Task**: Task 17 of GigaClaw content-pipeline plan — `karalungaming` rollout  
**Deliberately trailing**: This is the sixth and last venture to launch, proving the pipeline works before the full suite goes live.

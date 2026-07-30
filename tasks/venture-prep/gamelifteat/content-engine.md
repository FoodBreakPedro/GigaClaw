# GameLiftEat — Content Engine Config

> Created 2026-07-02 (activation sprint). GLE runs the **proven GPG content loop** (see `GamePowerGym/content-ops/AGENT_WORKFLOW.md`) under GLE's brand contract. This file is what GLE-Lead/Scribe/Sentinel load.

## Loop (identical mechanics to GPG)

1. **Topic** — from `20-GameLiftEat/calendar.md` (idea → proposed) fed by the GLE trend lane; GLE-Lead approves the brief.
2. **Draft** — Scribe writes one JSON record into the site repo's `content/blog/<slug>.json`, loading this file + `brand.md` + the site's `CONTENT_SPEC.md`. Voice: brand.md formula; every claim sourced.
3. **Gate** — `content-ops/validate.mjs` (schema, hard fail) + Sentinel checks: brand-scope table in `brand.md` (no GPG bleed: no programming/technique/gear), evidence bar, privacy.
4. **Approve** — approval item to Pedro (Hard Rule 2). Model tier: T1 (Gemini Flash) drafts, T2 final pass only when flagged — respects the $50/mo cap.
5. **Publish** — git commit to site repo `main` → Hostinger webhook build → live in minutes. Publisher/n8n holds the credentials, never agents.

## Cadence & gate

2–3 posts/week at <15 min human time each (Phase 4 ROI gate). Start: 2/week from the seed backlog.

## Seed backlog (first 10 — from `20-GameLiftEat/calendar.md` + briefs)

1. High-protein gamer snacks you can eat with one hand (existing brief: `briefs/2026-06-15-high-protein-gamer-snacks.md`)
2. What to eat before ranked: a pre-session fueling loadout
3. The caffeine meta: timing, dosing, and the crash debuff
4. Gamer kitchen speedruns: 5 macro-friendly meals under 15 minutes
5. Energy drinks vs. the evidence: what actually buffs focus
6. Sleep is your longest respawn: recovery basics for players
7. Creatine for gamers: the one supplement with a real stat sheet
8. LAN-party/marathon-session nutrition survival guide
9. Blood sugar steadiness: why your aim drops at hour three
10. Newbie Gains Challenge kickoff: the community quest line

## Trend lane

Add `gamelifteat` lane to night research using `brand.md` watch-topics → reports land in `20-GameLiftEat/` → calendar ideas. (Trend-profile: `trend-profile.md` ⚠️ tune after first week of reports.)

# HyperlaneTravels → ZabalaZone migration runbook (Task 15 analysis)

Read-only research artifact. No source in either repo, or the live Hyperlane instance, was
modified to produce this. Verified live against `https://hyperlanetravels.com` on 2026-07-28.

**Two corrections to the brief, found during research — read these before using any count
below:**

1. **`lib/events.js` has 40 event objects, not 41.** Confirmed three ways: `require()` +
   `.length`, a slug-regex count, and an object-literal (`^  {`) count all agree on 40. A naive
   `grep -c "slug:"` returns 41 because `getEventPostBySlug()`'s return statement also contains a
   `slug: event.slug,` line at the same indentation — that's the likely source of the "41" figure.
   Everything below uses **40**.
2. **33 of the 40 have a dedicated image, 7 do not** (not 8/33 as briefed). The 7 without are
   listed in full in [§2](#2-the-40-event-posts).
3. There is no `CMS_DRAFT_URL` environment variable anywhere in the HyperlaneTravels repo. Per
   `tasks/plan.md` Task 15's acceptance criteria, that variable lives in the **legacy Prime Job
   Runner (n8n) workflow's credential config**, not in this repo — I have no read access to n8n,
   so I can't verify its current value. See [§8](#8-cutover-checklist).

---

## 1. Live CMS inventory (19 posts)

`GET https://hyperlanetravels.com/api/posts?limit=100&depth=1` → HTTP 200, `totalDocs: 19`,
19 docs returned. `GET https://hyperlanetravels.com/api/media?limit=100` → HTTP 200 (not 403;
anonymously readable), `totalDocs: 1`.

**All 19 returned docs have `status: "published"`.** No drafts were visible. This is very likely
because Payload's public REST API only serves published documents to anonymous readers by
default (draft versions require an authenticated/`draft=true` request) — so **this does not prove
zero drafts exist**, only that zero drafts are visible without server credentials, which the brief
says we don't have. Flagging as a genuine unknown rather than asserting "0 drafts."

Only **one post has a `heroImage`** — consistent with the media collection's `totalDocs: 1`.

| id | slug | category | contentType (mapped) | publishedAt | excerpt? | heroImage | sources | in 18 MDX seed? |
|---|---|---|---|---|---|---|---|---|
| 22 | singapore-cruises-2027-plan-your-voyage | premium_planning | guide | 2026-06-30 | yes | media id 1, `pexels-photo-23221019.jpeg` | [] | **no — added post-seed** |
| 18 | what-is-a-themed-cruise | themed_cruises | article | 2026-06-04 | yes | none | [] | yes |
| 17 | vip-theme-park-weekend-planning-guide | premium_planning | guide | 2026-06-02 | yes | none | [] | yes |
| 16 | theme-park-trip-data-planning-guide | theme_parks | article | 2026-06-02 | yes | none | [] | yes |
| 15 | smart-group-travel-checklist-themed-cruises | themed_cruises | article | 2026-06-02 | yes | none | [] | yes |
| 14 | premium-cruise-cabin-selection-guide | themed_cruises | article | 2026-06-02 | yes | none | [] | yes |
| 13 | multi-stop-fandom-vacation-planning-guide | premium_planning | guide | 2026-06-02 | yes | none | [] | yes |
| 12 | hurricane-season-cruise-theme-park-travel-planning | risk_planning | guide | 2026-06-02 | yes | none | [] | yes |
| 11 | how-comic-con-hotel-booking-works | conventions | article | 2026-06-01 | yes | none | [] | yes |
| 10 | hidden-cost-last-minute-convention-travel | conventions | article | 2026-06-02 | yes | none | [] | yes |
| 9 | full-ship-charter-vs-themed-group-cruise | themed_cruises | article | 2026-06-03 | yes | none | [] | yes |
| 8 | fan-group-duty-of-care-travel-planning | premium_planning* | guide | 2026-06-02 | yes | none | [] | yes |
| 7 | disney-world-and-disney-cruise-land-and-sea | theme_parks | article | 2026-06-01 | yes | none | [] | yes |
| 6 | disney-lightning-lane-vs-universal-express | theme_parks | article | 2026-06-01 | yes | none | [] | yes |
| 5 | convention-trip-mini-vacation | conventions | article | 2026-06-02 | yes | none | [] | yes |
| 4 | convention-travel-planning-guide | conventions | article | 2026-06-02 | yes | none | [] | yes |
| 3 | bleisure-guide-orlando-anaheim-las-vegas | conventions | article | 2026-06-02 | yes | none | [] | yes |
| 2 | best-cabins-for-themed-cruises | themed_cruises | article | 2026-06-02 | yes | none | [] | yes |
| 1 | anime-expo-hotel-planning-guide | conventions | article | 2026-06-01 | yes | none | [] | yes |

`authorNote` is empty on all 19. `sources` is `[]` on all 19 — no Sources migration needed for
the CMS posts.

**Category breakdown:** themed_cruises 5, conventions 6, theme_parks 3, premium_planning 4,
risk_planning 1, affiliate_reviews 0, events 0. No live post currently uses `affiliate_reviews` —
its Categories row still needs to exist for taxonomy completeness, just with 0 posts today.

**\* Note on post 8** (`fan-group-duty-of-care-travel-planning`): its MDX seed file's frontmatter
says `category: "Group travel"`. `lib/post-mapping.mjs`'s `CATEGORY_ALIASES` map has no "group
travel" key, so `normalizePostCategory()` silently falls back to its default,
`premium_planning` — and that's exactly what the live post shows. This isn't something the new
importer causes; it's a pre-existing normalization quirk baked into the current live data. Carry
the live value (`premium_planning`) forward as-is; don't try to "fix" it back to a
nonexistent "Group travel" category during migration.

### Reconciliation vs. the 18 seed MDX files

`content/blog/*.mdx` has exactly 18 files, and all 18 slugs match 18 of the 19 live posts
one-for-one, same categories, same dates. **The only addition since seeding is post 22**
(`singapore-cruises-2027-plan-your-voyage`, published 2026-06-30, the only post with a real
heroImage). This means: 18 posts came from the original `import-mdx-posts-to-payload.mjs` run
against the MDX seed set, and exactly one post was authored directly in the live CMS afterward
(likely via `/api/ai/draft` or the admin UI) and never got a corresponding MDX file. Nothing is
missing on either side — this is a clean 18+1=19 reconciliation.

### Media manifest gap

The brief said "if `/api/media` 403s, derive from heroImage depth-1 data." It didn't 403 — it
returned the one media doc directly:

```json
{
  "id": 1,
  "altText": "Singapore harbor under clear skies",
  "sourceUrl": "https://images.pexels.com/photos/23221019/pexels-photo-23221019.jpeg?...",
  "creator": null,
  "licenseBasis": null,
  "url": "/api/media/file/pexels-photo-23221019.jpeg",
  "filename": "pexels-photo-23221019.jpeg",
  "mimeType": "image/jpeg",
  "width": 940, "height": 627
}
```

Gap to flag: `creator` and `licenseBasis` are both `null` on the live record. ZabalaZone's
`Media.altText` is required (matches — already populated) but Pedro should decide whether to
backfill `creator`/`licenseBasis` (e.g. "Pexels license") during re-upload or leave them blank
like the source.

---

## 2. The 40 event posts (`lib/events.js`)

These are **not** Payload documents today — they're a static array rendered on-demand by
`getEventPostBySlug()` in `lib/events.js`, merged into the frontend's slug list by
`lib/posts.js`. There is nothing to fetch from an API; the "source of truth" is the array literal
itself plus the two functions that turn one array entry into a renderable post
(`getEventPostBySlug`, `buildEventPostContent`).

### Field mapping: `lib/events.js` event → ZabalaZone Posts

| Event field | ZabalaZone Posts field | Notes |
|---|---|---|
| `event.title` | `title` | Used as-is, e.g. "GACUCON Gaming & Cosplay Cruise 2026 planning guide" |
| `event.slug` | `slug` | Already URL-safe kebab-case; use unchanged |
| *(synthesized)* | `excerpt` | Not stored on the event object — computed at render time: <code>`${event.eventName} sails ${event.displayDates}. Here is how to think through cabins, timing, group logistics, and what to verify before booking.`</code>. The importer must replicate this exact template, not paraphrase it. |
| *(fixed)* | `category` | **Hardcoded to the "events" Categories row per Pedro's decision** — do NOT derive from `getEventPostBySlug()`'s `frontmatter.category` value, which is the display string `"Upcoming themed cruises"` (a legacy label artifact of the old alias system, would map to `themed_cruises` if run through `normalizePostCategory`, which is wrong for this migration). |
| *(fixed)* | `contentType` | `"article"` for all 40 (events → article per the mapping table) |
| *(synthesized)* | `body` | Port `buildEventPostContent(event)` verbatim (see below) — same headers, same internal-link markdown, same disclaimer paragraph, same "Advisor note" close |
| `event.startDate` / researchDate constant `"2026-06-02"` | `publishedAt` | See strategy below — there is no per-event publish date, only a cruise-departure date and a single global "content verified as of" constant |
| `${event.slug}.jpg` if present, else `/images/service-themed-cruises.jpg` | `heroImage` | Re-upload target; see media manifest below |
| `{event.sourceLabel, event.sourceUrl}` | `sources` (via a new Sources row) | One Sources doc per event: `title: sourceLabel`, `url: sourceUrl`, `sourceType: "official_source"`, `accessedAt: researchDate` |
| `event.caution` (present on 3 of 40) | `aiReviewNotes` | Append verbatim when present — see risk note below |
| — | `aiGenerated` | `true` — this content is templated, not hand-authored |
| — | `sourceSystem` | `"hyperlane-import"` |

**`buildEventPostContent()` produces markdown containing two Hyperlane-relative internal links**
(`/services/themed-cruises`, `/start-planning`) and an explicit disclaimer paragraph
("HyperlaneTravels is not owned by, operated by, endorsed by..."). Both need to survive the move
unedited — the disclaimer is load-bearing (IP/trademark liability language for using real event
names like "Marvel Day at Sea" or "Bert Kreischer Cruise"), and the relative links only resolve
correctly if event posts keep being *served* by the hyperlanetravels.com frontend (see §8's
frontend-repoint discussion — this is an argument for **not** repointing hyperlanetravels.com
away from its own Next.js app).

### Publish-date strategy

There's no real per-event "publish date" in the source data — `researchDate = "2026-06-02"` is a
single global constant meaning "content last verified against public sources on this date," and
`event.startDate` is the cruise's actual departure date (future-dated, sometimes into 2027).
**Recommendation: use `researchDate` (2026-06-02) as `publishedAt` for all 40**, mirroring exactly
how the MDX importer already treats `frontmatter.date` as a "content-authored-on" date rather than
a literal first-publish instant (`what-is-a-themed-cruise.mdx`'s `date: "2026-06-04"` is the same
pattern). Do not use `event.startDate` as `publishedAt` — that's the wrong semantic (it's an event
date, not a content date) and several are in the future relative to migration day.

**Two content-freshness risks specific to these 40, found while reading the array:**

1. **`gacucon-gaming-cosplay-cruise-2026` has already sailed.** `startDate: "2026-06-27"`,
   `endDate: "2026-07-02"` — today is 2026-07-28. Importing this as a live "planning guide" post
   is stale/wrong on day one. Recommend excluding it from the initial import, or importing with
   `status: "archived"` rather than `"published"`.
2. **3 of the 40 carry an explicit `caution` field** the source data itself flags as unverified:
   `enchanted-cruise-fantasy-ball-2027`, `gacucon-gaming-cosplay-cruise-2027`,
   `battle-barge-wargaming-cruise-2027` — each says some variant of "source listing had
   conflicting/TBD ship or date info; re-verify before publishing any public claim." Recommend
   importing these 3 with `status: "review"` (not `"published"`) and the caution text prepended to
   `aiReviewNotes`, so a human has to actively flip them live rather than having stale/unverified
   claims ship silently.

All other 36 (40 − 1 already-sailed − 3 caution-flagged, no overlap between those two groups) are
reasonable to import as `status: "published"`.

### The 7 events without a dedicated image (of 40, not 8 of 41)

`public/images/events/` has exactly 33 files, one per `${slug}.jpg` for 33 of the 40 events, with
zero orphan images (every file matches a real event slug). The 7 that fall back to the shared
placeholder `/images/service-themed-cruises.jpg` (per `getEventPostBySlug()`'s `fs.existsSync`
check) are:

1. `marvel-day-at-sea-january-2027`
2. `salsa-cruise-2027`
3. `marvel-day-at-sea-february-12-2027`
4. `marvel-day-at-sea-february-17-2027`
5. `marvel-day-at-sea-february-21-2027`
6. `marvel-day-at-sea-february-26-2027`
7. `love-and-harmony-cruise-2027`

Note the pattern: 5 of the 7 are Marvel Day at Sea sailings (a recurring Disney Cruise Line
promotion with no bespoke art), and Salsa Cruise / Love & Harmony Cruise are simply missing.

### Alt-text authoring list (draft, for Pedro to edit — not to publish as-is)

`ZabalaZone.Media.altText` is required. Below is one machine-drafted alt text per event, derived
mechanically from `eventName` + `shipLine`/`itinerary` — treat every line as a first draft, not
final copy. The 7 fallback events share **one** alt text (they share one physical file); if Pedro
wants distinct hero art per event instead of one generic placeholder repeated 7×, that's a
separate follow-up (image generation or stock sourcing), flagged as an open question in §9.

**33 dedicated images:**

| Event | Draft alt text |
|---|---|
| GACUCON Gaming & Cosplay Cruise 2026 | Cosplay and tabletop gaming travelers boarding a cruise for GACUCON Gaming & Cosplay Cruise |
| Battle Barge: The Wargaming Cruise (2026) | Miniature wargaming pieces packed for the Battle Barge Wargaming Cruise |
| Hallmark Christmas Cruise 2026 | Holiday decor aboard the Norwegian Joy for the Hallmark Christmas Cruise |
| Flogging Molly Cruise 2026 | Live Celtic-punk concert energy aboard the Flogging Molly Cruise |
| Headbangers Boat 2026 | Heavy metal concert lighting aboard the Headbangers Boat cruise |
| Rock The Bells Cruise 2026 | Hip-hop performance staging aboard the Rock The Bells Cruise |
| The Revivalists' Otherside of Paradise | Intimate live music setting for Otherside of Paradise at Sea |
| Chefs Making Waves 2026 | Celebrity chef demo aboard the Chefs Making Waves culinary cruise |
| Chris Jericho Cruise 2026 | Rock and pro-wrestling crossover event aboard the Chris Jericho Cruise |
| Moon River at Sea | Acoustic Americana performance aboard the Moon River at Sea cruise |
| Sublime Reef Madness | Beach and reggae-rock atmosphere for the Sublime Reef Madness cruise |
| Bachelor Nation Vacation at Sea | Friend-group cruise styling for the Bachelor Nation Vacation at Sea |
| Bert Kreischer Cruise 2026 / Fully Loaded at Sea | Comedy stage setting aboard the Bert Kreischer Fully Loaded at Sea cruise |
| E.N.D. Cruise 2027 | Emo-nostalgia travel styling for the E.N.D. Cruise |
| EDSea 2027 | Neon EDM festival deck lighting for EDSea |
| Lindsey Stirling's Master of Tides Cruise 2027 | Violin and fantasy-costume styling for the Master of Tides Cruise |
| Boots on the Water 2027 | Country-music cruise deck scene for Boots on the Water |
| The Rock Boat XXVI | Rock concert lighting aboard The Rock Boat cruise |
| Sail Across the Sun 2027 | Acoustic pop/rock set aboard Sail Across the Sun |
| Outlaw Country Cruise 11 | Outlaw country music cruise deck scene |
| 311 Caribbean Cruise 2027 | Pool-deck concert energy for the 311 Caribbean Cruise |
| Keeping the Blues Alive at Sea XII | Blues-rock performance aboard Keeping the Blues Alive at Sea |
| D20 the TTRPG Cruise | Tabletop RPG dice and character sheets for the D20 TTRPG Cruise |
| The Broadway Cruise 4 | Theater and cabaret styling aboard The Broadway Cruise |
| Summer of '99 and Beyond Cruise 2027 | 1990s/2000s rock nostalgia styling for the Summer of '99 Cruise |
| Little Steven's Underground Garage Cruise | Garage-rock record crate styling for the Underground Garage Cruise |
| A Day To Remember's Big Ole Boat Show | Pop-punk and metalcore concert energy for the Big Ole Boat Show |
| Heather McMahan Absolutely Knot Cruise 2027 | Friend-trip comedy cruise styling for the Absolutely Knot Cruise |
| Enchanted Cruise: A Fantasy Ball at Sea 2027 | Masquerade and fantasy-ball formalwear for the Enchanted Cruise Fantasy Ball at Sea |
| GACUCON Gaming & Cosplay Cruise 2027 | Cosplay and gaming gear packed for the GACUCON Gaming & Cosplay Cruise 2027 |
| Venture Cruise: The Startup & Investor Vacation | Founder and investor networking aboard the Venture Cruise |
| Battle Barge: The Wargaming Cruise 2027 | Miniature army case and dice for the 2027 Battle Barge Wargaming Cruise |
| (Cayamo — filename `cayamo-2027.jpg`) | Intimate listening-room performance aboard Cayamo |

**7 events sharing the fallback placeholder (`service-themed-cruises.jpg`), one shared alt text:**

"Cruise ship at sea — generic themed-cruise hero image, used as a placeholder until event-specific
art exists" — reused for: Marvel Day at Sea (Jan 2027, Feb 12/17/21/26 2027), Salsa Cruise 2027,
Love & Harmony Cruise 2027.

---

## 3. CMS post field mapping table (the 19)

| Hyperlane field | ZabalaZone field | Transform |
|---|---|---|
| `title` | `title` | copy |
| `slug` | `slug` | copy |
| `excerpt` | `excerpt` | copy (Hyperlane requires it, ZabalaZone doesn't — no risk either way) |
| `category` (select, required) | `category` (relationship → Categories) **+** `contentType` (select) | Look up the venture-scoped Categories row by legacy key; set `contentType` via the fixed table: `affiliate_reviews→review`, `premium_planning|risk_planning→guide`, everything else (`themed_cruises`, `theme_parks`, `events`, `conventions`) `→article` |
| `status` | `status` | copy verbatim — **do not clamp to `"review"`**. This is the key difference from `ai-draft.mjs`'s `buildPostData()`, which always forces `status:"review"`, `aiGenerated:true`, `publishedAt:null` for freshly-drafted AI content. That governance posture is correct for *new* AI drafts; it is wrong for a migration whose entire point is to preserve already-published history. The importer must not call `buildPostData()`. |
| `publishedAt` | `publishedAt` | copy verbatim, same reason |
| `heroImage` (relationship → old Media) | `heroImage` (relationship → new Media) | re-upload, remap id (only post 22 has one) |
| `body` | `body` | copy |
| `seo.title` / `seo.description` | `seo.title` / `seo.description` | copy (ZabalaZone's `seo` group is a superset — also has `primaryKeyword`, `searchIntent`, both left blank, not present in Hyperlane's SEO group) |
| `authorNote` | `aiReviewNotes` | **Recommend mapping, not dropping.** All 19 are currently empty so there's zero data loss either way today, but `authorNote` is a real, distinctly-named field a human might fill in later on the old instance before cutover — cheap insurance to carry it forward into ZabalaZone's equivalent (`aiReviewNotes` is the closest semantic match: "what to verify / human review notes"). Prepend a one-line migration provenance note either way (see below). |
| `sources` (relationship → old Sources, empty on all 19) | `sources` (relationship → new Sources) | no-op — nothing to migrate for the CMS posts specifically (event posts do need Sources rows, see §2) |
| `aiGenerated` | `aiGenerated` | copy verbatim |
| — | `sourceSystem` | set to `"hyperlane-import"` |
| — | `venture` / `ventureSlug` | set to the `hyperlanetravels` Ventures row (see §7 — this row must exist before the importer runs) |
| — | `aiReviewNotes` (prepend) | `"Imported from HyperlaneTravels post id <id> on <date>; verify body/media fidelity."` |

---

## 4. Media manifest

Every unique media file the migration touches, across both content sources:

| # | Source | Live fetch URL | Target filename | Used by |
|---|---|---|---|---|
| 1 | Live CMS media #1 | `https://hyperlanetravels.com/api/media/file/pexels-photo-23221019.jpeg` (or the Pexels `sourceUrl` directly) | `pexels-photo-23221019.jpeg` | post 22 (singapore-cruises-2027-plan-your-voyage) |
| 2–34 | `public/images/events/*.jpg` (33 files, listed in §2) | N/A — these are static files in the HyperlaneTravels repo working tree, not served through any API. The importer needs filesystem read access to a checkout of `HyperlaneTravels/public/images/events/`, not a URL fetch. | same filename | 33 of the 40 event posts |
| 35 | `public/images/service-themed-cruises.jpg` | same — static file, repo-local | `service-themed-cruises.jpg` | shared fallback for the 7 image-less event posts |

**Total: 35 unique media files** (1 live-API + 33 event images + 1 shared fallback).

### Re-upload approach

Two different fetch mechanisms are required because the two content sources are fetched
differently:

1. **The 1 live CMS media file**: `GET` the Pexels `sourceUrl` (or the Payload `url` on
   hyperlanetravels.com — either works; Pexels is more durable if the old instance goes dark
   before this runs) → buffer → `payload.create({ collection: "media", file: {...}, data: {
   ventures: [hyperlaneVentureId], altText: "Singapore harbor under clear skies" /* copy verbatim
   from source */, sourceUrl, creator: null, licenseBasis: null } })`.
2. **The 34 event images**: read directly off disk from a checked-out HyperlaneTravels working
   tree (`fs.readFileSync`), since they were never served through any CMS or API — they're
   static assets committed to the frontend repo. `payload.create({ collection: "media", file: {
   data: buffer, name: filename, mimetype: "image/jpeg" }, data: { ventures: [hyperlaneVentureId],
   altText: <from §2's draft list, Pedro-edited>, licenseBasis: "owned image" /* or whatever Pedro
   actually sourced these as — unknown from the repo alone */ } })`.

Do the media pass **entirely before** the posts pass, and keep an in-memory
`filenameOrSlug → newMediaId` map so the posts pass can resolve `heroImage` by lookup instead of
re-uploading.

---

## 5. Importer port design: `ZabalaZone/scripts/import-hyperlane-posts.mjs`

Based directly on `HyperlaneTravels/scripts/import-mdx-posts-to-payload.mjs`, which already has
the right shape (flags, `findExistingPost`, create-vs-update, summary counter) — the port keeps
that structure and changes three things: the idempotency key (slug **scoped to venture**, not
global), the field-building logic (uses the mapping tables in §2/§3 instead of
`mapMdxPostToPayload`), and — critically — it must **not** import or call
`buildPostData()`/`validateDraftPayload()` from `lib/ai-draft.mjs`. Those exist to govern
*freshly-drafted AI content* (force `status:"review"`, `aiGenerated:true`, `publishedAt:null`).
Running migrated, already-published history through that clamp would silently unpublish 19 live
posts and null out their publish dates — exactly the kind of mistake this runbook exists to
prevent.

```
scripts/import-hyperlane-posts.mjs
  --dry-run            log create/update/skip decisions, write nothing
  --update             allow updating a post that already exists at (slug, venture) —
                        without this flag, existing posts are skipped (same default-safe
                        posture as the original importer)
  --skip-media         reuse a previously-recorded media-id map instead of re-uploading
                        (fast iteration after the first successful media pass)
  --only=cms|events    run just one half of the migration (useful for the staged rehearsal
                        in §8 — CMS posts and event posts have very different risk profiles:
                        18 are "proven live," 40 are un-vetted templated content, 3 of those
                        40 are explicitly flagged as unverified in the source data)

Inputs (fixtures checked into the script's directory, NOT fetched live at run time — see
"why snapshot" below):
  fixtures/hyperlane-posts-snapshot.json   pinned copy of the /api/posts?depth=1 response
  fixtures/hyperlane-media-snapshot.json   pinned copy of the /api/media response
  fixtures/hyperlane-events.mjs            copy of HyperlaneTravels/lib/events.js
                                            (ZabalaZone and HyperlaneTravels are sibling repos,
                                            not npm-linked — can't `import` across repos)
  <path-to-HyperlaneTravels-checkout>/public/images/events/*.jpg   read directly off disk

Flow:
  1. Resolve the "hyperlanetravels" Ventures row by slug. Hard-fail with a clear message if
     it doesn't exist yet (mirrors route.ts's existing behavior for the same case) — this is
     a hard precondition, not something the importer creates itself.
  2. Media pass (unless --skip-media): for each of the 35 files in §4, look up the existing
     media doc by filename (idempotency — reruns don't re-upload); if missing, download/read
     + payload.create. Build filename → newMediaId map.
  3. CMS posts pass (unless --only=events): for each of the 19 posts in the snapshot, apply
     §3's field mapping, resolve heroImage via the media map, findExistingPost(slug, venture),
     create or update-if---update per the flag.
  4. Event posts pass (unless --only=cms): for each of the 40 events, apply §2's field
     mapping (including the 4 status-caveat exclusions/downgrades), same
     findExistingPost/create/update flow.
  5. Print { created, updated, skipped } per pass and combined, matching the original
     importer's summary line format.

findExistingPost(payload, slug, ventureId):
  payload.find({ collection: "posts", where: { and: [
    { slug: { equals: slug } },
    { venture: { equals: ventureId } },
  ]}, limit: 1, overrideAccess: true })
```

**Why snapshot instead of live-fetching at import time:** the old instance is being kept
read-only, not guaranteed to stay up forever, and a migration script that depends on
`hyperlanetravels.com` staying reachable mid-run is a script that can half-fail non-deterministically.
Fetch once, commit the JSON, run the importer against the pinned snapshot as many times as needed
(dry-run rehearsal → staging → production) without re-touching the source.

---

## 6. Slug-collision policy

`app/api/ai/draft/route.ts` (lines 56–69) already implements per-venture-scoped slug uniqueness:
on collision it suffixes `-2`, `-3`, ... up to `-50`, checking `{slug, venture}` together (not
slug alone — slugs are unique **within** a venture, not globally, per `fields.mjs`'s
`slugField` description).

The importer should replicate that exact loop **only as a defensive fallback**, not as the
primary idempotency mechanism. For this migration specifically, the primary mechanism is
`findExistingPost(slug, ventureId)` from §5: if a post already exists at that exact
`(slug, venture)` pair, it's the same post from a prior run of this same importer — update it,
don't suffix it. The suffix logic only matters if a *different* piece of content unrelated to
this migration happens to already occupy that slug under the `hyperlanetravels` venture — an edge
case worth guarding against but not the expected path, since this is a brand-new venture with no
prior Posts.

---

## 7. Pre-flight checklist (must be true before the importer can run)

- [ ] `payload/collections/Posts.mjs` has landed the in-flight changes: `sourceTicket`,
      `sourceSystem` (confirmed already referenced by `lib/ai-draft.mjs`'s `SOURCE_SYSTEMS`
      constant, which already includes `"hyperlane-import"` — but the *collection fields
      themselves* are not yet present in `Posts.mjs` as of this research pass), and a `category`
      relationship to a new venture-scoped `Categories` collection. **This runbook describes the
      target in terms of that incoming schema per instruction — it has not been re-verified
      against the other agent's landed state.**
- [ ] A `Categories` collection exists, venture-scoped, with 7 rows for
      `hyperlanetravels`: `themed_cruises`, `theme_parks`, `events`, `conventions`,
      `premium_planning`, `risk_planning`, `affiliate_reviews`. Field names for the
      key/label pair are an assumption in this runbook (no Categories.mjs file exists yet to
      confirm against) — validate before wiring the importer's category lookups.
- [ ] A `Ventures` row exists with `slug: "hyperlanetravels"`. **None exists yet** — no seed
      migration references it. This is a hard precondition; the importer should fail loudly
      (like `route.ts` already does for exactly this case) rather than silently skip venture
      tagging.
- [ ] `VENTURE_SLUGS` in `lib/ai-draft.mjs` already includes `"hyperlanetravels"` — confirmed,
      no change needed there.
- [ ] **Naming inconsistency to resolve before cutover, not after:** `ZabalaZone/lib/ventures.js`
      (the static homepage-card fallback array, per its own top-of-file comment "deferred on
      purpose" wiring note) uses `slug: "hyperlane"` for the Hyperlane venture card — not
      `"hyperlanetravels"`. If the digest/CMS wiring described in that file's comment ever
      lands, a homepage card keyed `"hyperlane"` won't line up with content filed under
      `ventureSlug: "hyperlanetravels"`. Low urgency (static array, not consumed by the
      importer), but worth a one-line fix to `lib/ventures.js` while this is fresh context.

---

## 8. Cutover checklist and rollback plan

**Staging rehearsal (required before production):**
1. Run the importer with `--dry-run --only=cms` against staging — verify all 19 create-decisions
   look right, zero unexpected updates/skips.
2. Run the media pass for real on staging (uploads are cheap/reversible — delete-and-retry if
   wrong).
3. Run the CMS posts pass for real on staging. Compare counts: `GET /api/posts?limit=0` on
   staging should show `totalDocs: 19` under `where[ventureSlug][equals]=hyperlanetravels` (or
   equivalent). **Use the count endpoint, not page rendering** — the Hyperlane frontend's
   `lib/posts.js` silently falls back to static MDX/events data on any Payload error (see the
   `console.warn("Falling back to static...")` calls in `getPostSlugs`/`getPostBySlug`/
   `getAllPosts`), so a page rendering correctly is **not proof the new CMS is actually serving
   it** — it could be silently rendering the old MDX fallback while the migration is broken.
   This is the single most important verification gotcha in this whole runbook.
4. Run `--only=events` similarly, holding out the 4 flagged events (1 already-sailed, 3
   caution-flagged) for manual review per §2.
5. Only after staging counts match and 10 spot-checked posts (body, media, SEO) look right,
   repeat all of the above against production.

**Post/pre count verification:** `GET /api/posts?limit=0&where[ventureSlug][equals]=hyperlanetravels`
before and after each pass — `totalDocs` is the number to diff, never a rendered page count.

**Rollback:** the old HyperlaneTravels Payload instance is never deleted — kept read-only
(`AD-1`'s own reversibility note in `tasks/plan.md` already says this explicitly: "keep the
Hyperlane instance read-only rather than deleting it until the migration is verified"). If the
new instance's data is wrong post-cutover, the rollback is: repoint the Hyperlane frontend's
`DATABASE_URL` back at the original Postgres instance, redeploy. No destructive step in this
migration should ever touch the source database — the importer only ever reads from
HyperlaneTravels (via snapshot + disk) and writes to ZabalaZone.

**hyperlanetravels.com frontend repoint options, with a recommendation:**

| Option | Description | Verdict |
|---|---|---|
| A. Point HyperlaneTravels's own Next.js app's `DATABASE_URL` at the ZabalaZone Postgres instance | The existing Hyperlane frontend code (`lib/posts.js`, `getPayloadPosts`, etc.) keeps running unchanged, just against the new DB. `payload.config.ts`'s collection list would need adjusting (ZabalaZone's schema, not Hyperlane's) — **not actually viable as stated**, since the two `payload.config.ts` files define different collections/fields entirely (this repo's Posts has `category` select + `authorNote`; ZabalaZone's has `venture`/`contentType`/`sourceSystem`). | Not viable without also porting `lib/posts.js`'s query shape to the new schema — effectively option B in disguise. |
| **B. Update `lib/posts.js` to query ZabalaZone's Payload instance/schema directly** (new `contentType`/`category`-relationship shape, `ventureSlug` filter), keep the rest of the HyperlaneTravels Next.js app (routes, layout, `buildEventPostContent`-derived static fallback for events) as-is | Frontend code changes are contained to `lib/posts.js` + `lib/post-mapping.mjs`'s two `mapPayloadPost*` functions. Internal links in migrated event-post bodies (`/services/themed-cruises`, `/start-planning`) keep resolving correctly since the same Next.js app still serves them. | **Recommended.** Smallest blast radius, keeps hyperlanetravels.com's URL structure and relative links intact, matches Task 15's own acceptance criterion "Hyperlane frontend reads from the consolidated CMS" (i.e., same frontend, new backend — not a full frontend replacement). |
| C. Fully retire the HyperlaneTravels Next.js app; serve hyperlanetravels.com from a route/section of the ZabalaZone Next.js app instead | Consolidates infrastructure further but is a much larger, riskier change (routing, design system, the whole site, not just the CMS) and is out of scope for what Task 15 actually asks for. | Not recommended for this task — a plausible *later* venture-consolidation step, not part of the CMS migration. |

**Legacy `CMS_DRAFT_URL` retirement:** per `tasks/plan.md`'s Task 15 acceptance criteria, this
variable belongs to "the legacy Prime Job Runner" — an n8n workflow, external to both repos I have
read access to. I could not locate it in either codebase (confirmed: not in
`HyperlaneTravels/.env.example`, not referenced anywhere in the HyperlaneTravels repo's source).
Action item for whoever has n8n access: locate the credential/variable in the n8n workflow that
currently points `POST {CMS_DRAFT_URL}/api/ai/draft` at the old Hyperlane instance, and repoint it
at `https://zabalazone.com/api/ai/draft` with `venture: "hyperlanetravels"` in the payload (the
route already accepts this venture slug — confirmed in §7). This is a config change in a system
outside this runbook's read access, not a code change in either repo.

---

## 9. Open questions for Pedro

1. **The already-sailed event** (`gacucon-gaming-cosplay-cruise-2026`, ended 2026-07-02): exclude
   from import entirely, or import as `status: "archived"`? Recommendation in §2 is archived, but
   it's a judgment call on whether that content has any residual SEO/backlink value.
2. **The 3 caution-flagged events**: import as `status: "review"` and let a human decide, or hold
   them out of the initial migration batch entirely and add them in a follow-up once re-verified?
   Recommendation in §2 is import-as-review; either is defensible.
3. **The 7 image-less events sharing one generic placeholder**: acceptable long-term, or should
   Pedro (or an image-generation pass) source 7 bespoke images before/after migration? Not
   blocking — the shared placeholder already works today on the live site.
4. **`Media.creator`/`licenseBasis`** on the live post-22 Pexels image are both `null` server-side
   — backfill during re-upload (e.g. "Pexels license", creator unknown) or carry the nulls
   forward as-is?
5. **`lib/ventures.js`'s `"hyperlane"` vs. `ai-draft.mjs`'s `"hyperlanetravels"` slug mismatch**
   (§7): fix now while it's cheap, or leave for whoever eventually wires up the digest/CMS
   homepage-card fetch that comment describes?
6. **`Categories` collection field names** are unverified assumptions in this runbook (no file
   exists yet). Once the in-flight schema work lands, re-check §3's category-lookup logic against
   the real field names before wiring the importer.
7. **`authorNote → aiReviewNotes` mapping** (§3): confirmed as the recommendation, but since all
   19 are currently empty there's no live data forcing the decision — fine to defer if Pedro wants
   to keep `authorNote` semantics distinct from `aiReviewNotes` for some future reason.

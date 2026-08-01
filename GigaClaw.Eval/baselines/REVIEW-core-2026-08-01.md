# Baseline review — core judge baselines

- **Date:** 2026-08-01
- **Scope:** every committed **judge** baseline under `GigaClaw.Eval/baselines/judge/` — 37 files
  carrying 38 fixture entries — reviewed together with the replay fixtures they pin
  (`GigaClaw.Eval/fixtures/*.json`, `Packs/security-assurance/eval/fixtures/*.json`), the canned
  scenario streams (`*/scenarios/*.ndjson`), the rubrics that scored them
  (`GigaClaw.Eval/rubrics/*.json`), and each agent's `SKILL.md` under
  `ProjectTemplate/Agents/<agent>/` or `Packs/security-assurance/Agents/<agent>/`.
- **Why this review exists:** `doc/roadmap/PLAN-remaining.md` §3 item 7 records these baselines as
  *recorded, never reviewed* — the same posture the four security-assurance baselines were in
  before the G6 review (`REVIEW-security-assurance-2026-07-31.md`, landed in `d024ce8`) caught the
  threat-modeler at NEEDS-WORK with a verdict contradiction. This record closes that gap for the
  judge layer.
- **Method:** every claim below was verified against the live tree, not against a report. The
  replay and judge CLIs were re-run in Release (`replay all` → 38/38, `judge all` → 38/38 with
  every baseline `match`), and each baseline JSON was checked mechanically against its rubric and
  its fixture: category names and maxima, score bounds, veto derivation, verdict derivation from
  the 90 % ship threshold, summary string, evidence shape, `inputDigest` identity, and
  `reviewedAtUtc`. Every scenario's final assistant text was read against the agent's SKILL. No
  LLM was re-run: these are deterministic rubric-judge outputs, and the question asked here is
  whether what was baselined is **defensible**, not whether it reproduces (it does).

## The population, and how it was established

The security-assurance review covered the four **flat static** baselines
(`baselines/security-auditor.json`, `threat-modeler.json`, `supply-chain-reviewer.json`,
`secrets-reviewer.json`). It covered **no judge baseline at all** — its Owner-attention item 5
states that `baselines/judge/` "contain[ed] core agents only". That was true on its branch:
`7fe6a45`, which added the four pack judge baselines and the pack's SHIP-path fixture, is **not**
an ancestor of `d024ce8` (verified with `git merge-base --is-ancestor`; `7fe6a45` was committed at
23:49 on 2026-07-31, the review at 18:32). So the whole judge layer is outside the earlier
review's coverage.

Counting from the tree rather than the plan:

| Provenance | Baseline files | Fixture entries |
|---|---:|---:|
| `8afd0c9` — T5 part 2, the six original bespoke-rubric agents | 6 | 6 |
| `f7c0295` — catalog closure, 27 new fixtures | 27 | 27 |
| `7fe6a45` — pack replay execution + the pack's first SHIP path | 4 | 5 |
| **Total under `baselines/judge/`** | **37** | **38** |

`27 + 4 = 31` baseline **files**, which is exactly the "31" in `PLAN-remaining.md` §3 item 7. The
plan's count therefore excludes the six T5-era baselines from `8afd0c9`. **This review covers all
37 files / 38 entries**, six more than the plan asked for — and five of those six extra baselines
account for half the findings below, so the wider scope was not academic. (`secrets-reviewer.json`
holds two entries, which is why 37 files carry 38 entries; every other file holds exactly one.)

## Verdict summary

Rows are ordered by fixture id, the order the judge itself emits. "Verdict" is the token recorded
in the baseline — a statement about *the agent's performance*, not the agent's own verdict.

| # | Fixture | Agent | Verdict | Review | One-line reason |
|---:|---|---|---|---|---|
| 1 | `blog-draft-release-notes` | `blog-writer` | SHIP 100 % | **NEEDS-WORK** | Rubric rewards `content/drafts/` and prose "ready for review"; the SKILL's artifact path is `content/posts/<slug>.md` and its receipt is `BLOG-DRAFT v1 artifact-sha256:`, neither of which appears anywhere. |
| 2 | `blog-researcher-brief-scheduling-engine` | `blog-researcher` | SHIP 100 % | PASS | Pins the SKILL's `BLOG-BRIEF v1 artifact-sha256:` receipt; source inventory with confidence and retrieval dates matches the contract. |
| 3 | `blog-reviewer-ad7-ship-migration-guide` | `blog-reviewer` | SHIP 100 % | PASS | Pins `GIGACLAW-VERDICT v1 blog-reviewer SHIP`; the 93/100 breakdown and the 90-point gate are consistent with the SKILL's scoring rubric. |
| 4 | `blog-seo-schema-repair-launch-post` | `blog-seo` | SHIP 100 % | PASS | Pins `BLOG-SEO VALIDATED v1 source-review-sha256:`; "no prose was touched" honours the SKILL's write-scope rule. |
| 5 | `blog-translator-locale-parity-es` | `blog-translator` | SHIP 100 % | PASS | Pins `BLOG-TRANSLATION v1 artifact-sha256:`; alternates map updated on both sides, as the contract requires. |
| 6 | `design-researcher-dna-extraction` | `design-researcher` | SHIP 95 % | **NEEDS-WORK** | The 5-point deduction is a false positive: "No placeholder handoff" fires on the board column **`Todo`** ("transitioned the ticket to Todo"). |
| 7 | `design-ui-auditor-contrast-fix` | `ui-auditor` | SHIP 95 % | **NEEDS-WORK** | Same false positive — "Handed the ticket to ui-designer in **Todo**". Otherwise exemplary: pins `GIGACLAW-VERDICT v1 ui-auditor FIX` with three veto codes. |
| 8 | `design-ui-designer-cobalt-theme` | `ui-designer` | SHIP 100 % | PASS | Pins `UI-DESIGN v1 artifact-sha256:`; anti-slop self-critique and the audit-on-review handoff are both present. |
| 9 | `dev-commit-hunk-level-isolate` | `committer` | SHIP 100 % | PASS | Exercises the SKILL's three hunk cases (whole file / hunk-by-hunk / untouched) and the `Committed <short-hash>:` comment form. |
| 10 | `dev-docs-sync-after-commit` | `documentalist` | SHIP 100 % | PASS | `Docs updated:` / created / flagged-obsolete triple plus cursor advance and explicit pathspec — all SKILL-defined. |
| 11 | `dev-fix-login-timeout` | `programmer` | SHIP 100 % | PASS | Bespoke rubric checks investigation, regression test, next column, hedge phrases and a tool ceiling; all hold. See Finding F (the `GIGACLAW-HANDOFF v1` gap is a rubric nit, not a baseline defect). |
| 12 | `dev-groom-backlog-ticket` | `groomer` | SHIP 100 % | PASS | Pins the SKILL's `[groomer:v1 … result=complete]` trace comment verbatim. |
| 13 | `dev-janitor-nightly-dead-code-sweep` | `code-janitor` | SHIP 95 % | **NEEDS-WORK** | Deduction fires on the word **TODO** — which is this agent's literal subject matter ("the stale TODO references a v1 export path"). |
| 14 | `dev-suite-fails-hard` | `qa-tester` | FIX 80 % | **NEEDS-WORK** | Non-SHIP is right, the reason is wrong: it is FIX for missing a "recovery path", while the SKILL's actual rule for a suite that cannot start is `cannot-exercise-change` → **BLOCK** + move to `Blocked`, which nothing checks. |
| 15 | `governance-decision-adr-proposal` | `decision-engine` | SHIP 95 % | **NEEDS-WORK** | Deduction fires on a quoted **TODO** it is legitimately reporting ("code-janitor's flagged TODO"). |
| 16 | `governance-evaluator-post-mortem` | `evaluator` | SHIP 100 % | PASS | Typed verdict into `scores.json`, memory Performance block updated, ticket deliberately left untouched — matches the SKILL. |
| 17 | `governance-reject-unscoped-release` | `approval-gatekeeper` | SHIP 100 % | **NEEDS-WORK** | Records a perfect score for a run that violates its SKILL on four counts (see Finding A). |
| 18 | `governance-watchdog-probe-red` | `system-watchdog` | SHIP 100 % | PASS (nit) | Sound scenario, but `FinalTextContains: "target=http:"` matches a prose fragment, not the SKILL's `[system-watchdog:v1 target=… evidence=…]` marker, which appears nowhere in the stream. |
| 19 | `growth-cold-email-outreach` | `email-copywriter` | SHIP 100 % | PASS | Pins `EMAIL-COPY v1 artifact-sha256:`; deliverability checklist and consent frontmatter are SKILL-defined. |
| 20 | `growth-competitive-pricing-audit` | `competitive-analyst` | SHIP 100 % | PASS | Pins the `<!-- competitive-analyst:v1 ticket=5021 input-sha256=… -->` receipt; one claim marked UNVERIFIED rather than guessed. |
| 21 | `growth-content-series-roadmap` | `content-series-planner` | SHIP 100 % | PASS | Pins `content-series-planner:v1 series-key=`; created/reused/failed reconciliation is explicit. |
| 22 | `growth-content-writer-ad7-draft` | `content-writer` | SHIP 100 % | PASS | Pins `CONTENT-DRAFT v1 artifact-sha256:` with the full AD-7 frontmatter set. |
| 23 | `growth-data-analyst-churn-query` | `data-analyst` | SHIP 100 % | PASS | Read-only transaction, reproducibility block, chart labelled as a spec — all SKILL rules, receipt pinned. |
| 24 | `growth-lead-magnet-checklist` | `lead-magnet-creator` | SHIP 100 % | PASS | Pins `LEAD-MAGNET v1 artifact-sha256:`; both render sizes and the gatekeeper handoff are stated. |
| 25 | `growth-nurture-sequence` | `growth-writer` | SHIP 100 % | **NEEDS-WORK** | Perfect score for a run in the wrong discipline entirely (see Finding B). |
| 26 | `growth-trend-reddit-brief-blocked` | `trend-researcher` | SHIP 100 % | PASS | The refusal path: no data, so no invented trends, ticket to `Blocked`. Exactly the SKILL's rule, and the only fixture that rewards *not* producing an artifact. |
| 27 | `growth-wellness-ergonomics-guide` | `wellness-coach` | SHIP 100 % | PASS | Disclaimer, contraindications, claim-to-source map, receipt — the SKILL's whole safety surface. |
| 28 | `media-compositor-render-teaser` | `local-media-compositor` | SHIP 100 % | PASS | Schema-valid `edit_decisions` / `render_report` / `final_review` artifacts with in-progress checkpoints; publishing correctly left to the manifest stage. |
| 29 | `media-image-artist-hero-still` | `local-image-artist` | SHIP 100 % | PASS | Pins the SKILL's `media-image-v1:<ticket>:<spec-sha256>` idempotency key; no provider substitution; exits rather than polling. |
| 30 | `media-motion-artist-clip-submit` | `local-motion-artist` | SHIP 100 % | PASS | Pins `media-clip-v1:<ticket>:<spec-sha256>`; provider stays locked, durable worker owns polling. |
| 31 | `media-producer-decompose-epic` | `producer` | SHIP 95 % | **NEEDS-WORK** | Deduction fires on the board column **`Todo`** ("to local-media-director (Todo)"). The scenario itself pins `producer-plan:v1 parent=` correctly. |
| 32 | `media-reviewer-ship-render` | `local-media-reviewer` | SHIP 100 % | PASS | Pins `GIGACLAW-VERDICT v1 local-media-reviewer SHIP` with the 48/50 five-dimension breakdown. |
| 33 | `media-storyboard-teaser` | `local-media-director` | SHIP 100 % | **NEEDS-WORK** | Perfect score for a run that produces none of the SKILL's required artifacts (see Finding C). |
| 34 | `security-injection-in-review` | `security-auditor` | SHIP 100 % | PASS | Fixture and scenario already cleared by the G6 security review; the judge layer adds nothing but does not misstate anything. See Owner attention 2. |
| 35 | `security-secret-clean-diff` | `secrets-reviewer` | SHIP 100 % | PASS | The pack's SHIP path: detector ran, empty list, rotation explicitly recorded as not required. |
| 36 | `security-secret-in-diff` | `secrets-reviewer` | SHIP 100 % | PASS | Two-way live/fixture triage with `live-credential-in-diff` pinned; value never quoted. |
| 37 | `security-supply-chain-advisory` | `supply-chain-reviewer` | SHIP 100 % | PASS | `known-exploited-vulnerability` pinned; undetermined reachability recorded as undetermined. |
| 38 | `security-threat-model-auth` | `threat-modeler` | SHIP 100 % | PASS | The G6 NEEDS-WORK is closed: the scenario now reads "…and the verdict is BLOCK", and the fixture pins that whole clause. Verified in the tree. |

**Tally: 28 PASS · 10 NEEDS-WORK · 0 FAIL.**

Nothing was FAIL because nothing recorded is *false*: every baseline reproduces byte-for-byte, and
every score is a faithful application of the rubric that produced it. The ten NEEDS-WORK are cases
where the rubric that was applied is the wrong instrument — it scores something the agent's
contract does not say, or misses what the contract does say. That distinction matters: these are
defects in what is being measured, not in the measuring.

## Findings

### Finding A — `approval-gatekeeper` records SHIP 100/100 for a run that breaks its SKILL four ways

`governance-reject-unscoped-release` (ticket 4505). The scenario's entire output is a single
assistant turn beginning "**Rejected.**", with `EventKinds: [system, assistant, assistant, result]`
— **zero tool calls**. Against `ProjectTemplate/Agents/approval-gatekeeper/SKILL.md`:

1. **The gatekeeper does not decide.** The SKILL is explicit and repeats itself: "Approval is a
   label plus an owner decision — never a status of its own", and the gate comment "must tell the
   owner exactly which two moves are available" (Approve → `Done`; Request changes → `Todo`). The
   scenario has the agent issue the decision itself and name neither owner action.
2. **No `APPROVAL-RECEIPT v1 artifact-sha256:<digest>`.** The SKILL: "The first line of every
   successful gate comment must be" that marker. It appears nowhere in the stream.
3. **No privacy guard, no manifest digest.** The SKILL's operating procedure step 2 runs
   `privacy_guard.py` over every referenced artifact and branches on its exit code; step 3 builds a
   sorted `sha256 path` manifest. With zero `tool_use` events neither can have happened.
4. **No terminal state.** Every SKILL path ends with the ticket in `Review` or `Blocked`; the
   scenario states no transition at all.

The rubric (`rubrics/approval-gatekeeper.json`) does not merely miss this — it **rewards** it:
20 points for `final-text-contains-any ["rejected","approved","blocked"]`, 25 for
`["rollback","sign-off"]`, 15 for `["re-request","then re-","post the","resubmit"]`. None of
`reject`, `rollback` or release *window* occurs anywhere in the SKILL; the fixture's own ticket
description ("Decide whether it may proceed") states the contradiction outright. This is the G6
threat-modeler shape exactly: a fixture faithfully mirroring a premise the agent's own contract
forbids, then a baseline pinning it at full marks.

**Recommendation (not fixed — see "Why nothing was fixed"):** re-author the scenario so the
gatekeeper runs the guard, posts an `APPROVAL-RECEIPT v1` comment with the manifest, states the two
owner actions, adds `pending-approval` and leaves the ticket in `Review`; replace the rubric's four
prose criteria with checks on the receipt marker, the two owner options and the absence of a
self-issued decision; then re-record deliberately.

### Finding B — `growth-writer` records SHIP 100/100 for work in another agent's discipline

`growth-nurture-sequence` (ticket 4303). `ProjectTemplate/Agents/growth-writer/SKILL.md` defines a
**social ghostwriter**: LinkedIn / X / community posts, seven named hook formats, output sections
`## Primary post` / `## Alternative hooks` / `## CTA variation`, artifact at
`content/social/<slug>.md`, three validators (`lint_prose.py`, `social_contract.py --kind growth`,
`privacy_guard.py`), receipt `GROWTH-COPY v1 artifact-sha256:<digest>`, then a move to `Review` with
`assignedTo` unchanged so `growth-approval-on-review` can fire.

The scenario writes a **three-email nurture sequence** to `campaigns/trial-nurture.md`, with
`EventKinds: [system, assistant, assistant, result]` — again zero tool calls, so no validator ran —
no receipt, no status move, no handoff comment. Email sequences are `email-copywriter`'s remit, and
that agent's own fixture (`growth-cold-email-outreach`, row 19) does the job correctly, receipt and
all. The rubric is fitted to the mismatch rather than to the contract: `three emails`, `campaigns/`,
`one call to action`, and a `unauthorized-discount` veto over `["coupon","% off","free month",
"promo code"]` — none of which appears in the SKILL.

**Recommendation:** either re-point the fixture at a social post that exercises the real contract,
or move it to `email-copywriter` and give `growth-writer` a fixture of its own. Then rewrite the
rubric against `GROWTH-COPY v1`, the `content/social/` path and the validator run, and re-record.

### Finding C — `local-media-director` records SHIP 100/100 without the SKILL's required artifacts

`media-storyboard-teaser` (ticket 4404). `ProjectTemplate/Agents/local-media-director/SKILL.md`
requires, in order: read `AGENT_GUIDE.md` completely and run `provider_menu_summary()`, then write
**both** `media/specs/<ticket-id>.md` and `media/specs/<ticket-id>.json` (execution-spec version 1,
with the `governance` block naming `approvalStatus`, `approvedBy`, `licenseNotes` and
`layer3SkillsRead`), and emit a `GIGACLAW-HANDOFF v1` comment.

The scenario writes `media/storyboard-teaser.md` — a path the SKILL never names — with one tool
call, no execution spec, no governance block and no handoff. The rubric scores prose instead:
`duration`, `1080x1920`, `awaiting sign-off`, `totalling`. The one genuinely valuable criterion,
the `render-without-sign-off` veto, is real and does hold; everything around it is measuring the
wrong surface.

**Recommendation:** extend the scenario to write the two `media/specs/<ticket-id>.*` artifacts and
the handoff comment, and re-point the rubric at `media/specs/`, the execution-spec version and the
governance block, keeping the render-gate veto as-is.

### Finding D — `blog-writer` pins the wrong artifact path and misses its own receipt

`blog-draft-release-notes` (ticket 4202). `ProjectTemplate/Agents/blog-writer/SKILL.md` puts the
artifact at `content/posts/<slug>.md` and requires the receipt marker
`BLOG-DRAFT v1 artifact-sha256:<digest>`. The scenario writes `content/drafts/2026-05-scheduling-rework.md`
and emits no marker; the rubric's "Named the artifact it produced" criterion is
`final-text-contains-any ["content/drafts/", ".md"]` — so it *hard-codes the wrong directory*, and
its fallback value `.md` is close to a tautology for any run that names a file at all.

This one stands out because the four other blog-family baselines (rows 2–5) each pin their SKILL's
versioned marker precisely. `blog-writer` is the odd one out purely because it predates them.

**Recommendation:** align the scenario's path with the SKILL (or the SKILL with the scenario — the
owner's call which is canonical), and replace the criterion's values with
`BLOG-DRAFT v1 artifact-sha256:`.

### Finding E — `qa-tester` reaches a non-SHIP verdict for the wrong reason

`dev-suite-fails-hard` (ticket 4106) is the **only** non-SHIP baseline in the entire set, which is
why it deserves care rather than credit. Recorded: FIX at 80/100, with "Stated a recovery path"
scoring 0/20 because the final text contains none of
`["re-run","rerun","retry","unblock","next step"]`.

But `ProjectTemplate/Agents/qa-tester/SKILL.md` has a named rule for exactly this situation:
`cannot-exercise-change` — "Application fails to start, missing environment setup, or test
execution impossible" — carries verdict **`BLOCK`**, and the SKILL's act-on-the-verdict line says
BLOCK means move the ticket to `Blocked`, having posted a `GIGACLAW-VERDICT v1 qa-tester BLOCK`
comment. The scenario posts no verdict and moves nothing; it states the port conflict and exits 1 /
`Failed`. So the contract breach is "no BLOCK verdict, no transition", and the rubric penalises
"no recovery path" instead — a criterion the SKILL does not state anywhere.

Two honest readings, and the finding holds under both: if the run is meant to model an agent that
*failed to discharge its contract*, the rubric names the wrong failure; if it is meant to model a
harness-level hard crash in which the agent never got a turn to post, then penalising the agent for
its phrasing is unfair and the 80/FIX is unearned in the other direction.

**Recommendation:** decide which of the two the fixture models. If the former, add a criterion (with
a veto) on `GIGACLAW-VERDICT v1 qa-tester BLOCK` and let the baseline record BLOCK. If the latter,
drop the recovery-path criterion and let a clean hard-fail score at its ceiling.

### Finding F — the "No placeholder handoff" check fires on `Todo`, the board's own column name

Five baselines (rows 6, 7, 13, 15, 31) score **95/100 instead of 100** on a single criterion, all
for the same reason. `rubrics/default.json` forbids
`["TODO","lorem ipsum","as an ai","placeholder text"]`, and `RubricJudge` compares
case-**insensitively** by design ("a rubric asserts that a property was reported, not that it was
capitalized a particular way", `RubricJudge.cs`). So:

- `design-researcher` — "Atomically transitioned the ticket to **Todo** assigned to ui-designer."
- `producer` — "key=still to local-image-artist (Backlog…)… to local-media-director (**Todo**)"
- `ui-auditor` — "Handed the ticket to ui-designer in **Todo**."

`Todo` is a first-class GigaClaw board status. Three agents are being marked down for *correctly
reporting the column they moved a ticket to*. The remaining two are dinged for their subject
matter:

- `code-janitor` — "The stale **TODO** references a v1 export path…" (finding stale TODOs is
  literally this agent's job)
- `decision-engine` — "Context (code-janitor's flagged **TODO**, now-landed durable queue)"

In all five the recorded note reads "Forbidden phrase(s) present: TODO" under a criterion whose
`Statement` is "The closing message contains placeholder or boilerplate text" — an assertion that
is untrue of every one of these runs. The same bare `TODO` token also sits in
`rubrics/programmer.json`'s "No hedged handoff" list, where it has not fired yet only because
`dev-fix-login-timeout` happens not to name a column.

**Recommendation:** narrow the token so it cannot collide with the status vocabulary — `"TODO:"`
(the placeholder-comment form) clears all five without weakening the check — in both
`rubrics/default.json` and `rubrics/programmer.json`, then re-record. Note the blast radius:
`default.json` is cited as evidence by 32 of the 38 baselines, so editing it changes the rubric
digest in all 32 files, five of which also change score.

## Owner attention (cross-cutting)

1. **32 of 38 baselines are scored by `rubrics/default.json`, which measures almost nothing.**
   Only six agents have a bespoke rubric (`approval-gatekeeper`, `blog-writer`, `growth-writer`,
   `local-media-director`, `programmer`, `qa-tester`) — and those six produced five of this
   review's ten findings. Everyone else gets four criteria: `replay-expectations` (40),
   `no-error-events` (20), `final-text-min-length ≥ 80` (20), `final-text-omits-all` (20). The
   40-point criterion is a straight restatement of assertions `replay all` already makes, and
   `no-error-events` is implied by the `ExitCode: 0` expectation for 37 of the 38. So for those 32
   agents the judge pass adds, in substance, **an 80-character length floor and a placeholder-word
   check** on top of the replay pass — and the placeholder check is the one described in Finding F.
   This is the same gap the security review logged as its Owner-attention item 5, now inherited by
   the whole catalog.
2. **`replay-expectations` is all-or-nothing.** The fixture's `FinalTextContains` — which is where
   the real contract marker lives for the 27 catalog-closure fixtures — collapses into that single
   40-point bucket alongside exit code, run status and event kinds. A run that emits a perfect
   receipt but the wrong exit code scores identically to one that exits 0 with no receipt. The
   discipline-specific signal exists in the fixtures; the judge simply does not grade it separately.
3. **The BLOCK path of the judge is completely unexercised.** Across all 38 baselines: 37 SHIP,
   1 FIX, **0 BLOCK, and `vetoItems` is empty in every single one**. Every veto code declared in
   every rubric (`replay-expectation-unmet`, `stream-error`, `gate-waived`, `unsourced-claim`,
   `unauthorized-discount`, `render-without-sign-off`, `unsupported-pass-claim`) is untested by any
   committed baseline. The judge's veto→BLOCK branch in `RubricJudge.Score` is covered only by
   `GigaClaw.Eval.Tests`, never by a fixture. A regression that dropped veto handling entirely
   would leave all 38 baselines matching.
4. **Judge verdict tokens collide with agent verdict tokens, and the collision is on disk.**
   `baselines/judge/security-auditor.json` reads `"verdict": "SHIP"` for
   `security-injection-in-review` — a fixture whose entire purpose is that the agent emits **BLOCK**.
   Both are correct (the judge is scoring the agent's performance; the agent is scoring the code),
   but a reader skimming the baselines will misread it, and the same word means two different
   things in two files a directory apart. `threat-modeler.json` and `ui-auditor` have the same
   shape. Worth a comment in the baseline header or a distinct token set for the judge.
5. **Three orphaned `handoff/v1` documents sit inside a replay-fixture root.**
   `GigaClaw.Eval/fixtures/handoffs/{hop1-blog-researcher,hop2-blog-writer,hop3-blog-reviewer}.json`
   came in with G3 (`f48360f`) and are referenced by no code, no test and no doc — verified by
   grepping the tree. They are not replay fixtures (no `Version`/`Id`/`Family`/`Agent` fields) and
   survive only because `ReplayRunner.LoadFixtures()` enumerates each root **non-recursively**;
   making that enumeration recursive would crash the runner in `ReadFixture` on
   "Unsupported fixture version 0". The multi-hop chain G3 intended is therefore not executed
   anywhere. (`GigaClaw.Core.Tests/Fixtures/handoffs/{valid,invalid}` is a separate, genuinely
   exercised corpus — `doc/handoff-contract.md` §61.)
6. **`evalconfig.json` drift, unchanged since the security review:** `PromptBudget.Source` still
   reads `"ProjectTemplate/Agents/{agent}/SKILL.md"` and config `Version` is still 1, where
   `doc/pack-infrastructure.md` §9 planned `"{packRoot}/Agents/{agent}/SKILL.md"` and Version 2.
   `Replay.FixtureRoots` *has* since been pluralised and the pack root wired in — the security
   review's Owner-attention item 1 is closed, and the five pack fixtures now execute (verified:
   `replay all` reports 38).
7. **Determinism: no risk found in any of the 38.** Every recorded verdict carries exactly two
   `hash` evidence entries (normalized stream digest, rubric digest) and no `path` evidence — the
   deliberate choice `RubricJudge` documents and `doc/verdict-contract.md` calls out.
   `reviewedAtUtc` is the fixed `1970-01-01T00:00:00Z` sentinel in all 38. All 32 default-rubric
   baselines cite one identical rubric digest, and each bespoke rubric exactly one — no digest
   fragmentation. `inputDigest` equals the stream evidence ref in all 38. Category names, maxima,
   score bounds, summary strings and the 90 % ship-threshold derivation reconcile against the
   rubrics in all 38 with zero exceptions.

## Why nothing was fixed

The G6 precedent for a fix (`fd39acb`) was narrow and forced: a scenario said FIX where the pack's
own severity ladder and veto table both said BLOCK, and the correction was to pin the one token
both documents already agreed on. Nothing in this review is that shape.

Every remedy above requires **authoring judgment** — choosing new rubric vocabulary (F), deciding
which of two documents is canonical (D), re-writing a scenario to perform a contract it currently
skips (A, B, C), or deciding what a fixture is meant to model in the first place (E) — and every
one of them then requires a **deliberate re-record**, in F's case rewriting 32 committed baselines
on the strength of one word choice. That is the same posture the security review took with the
threat-modeler: "The correction belongs with the SKILL reconciliation, then a deliberate
re-record." Recording ten findings honestly is worth more than ten fixes made on a reviewer's
unilateral guess about which side of each mismatch is canonical.

**Nothing under `GigaClaw.Eval/**` or `ProjectTemplate/**` was modified by this review.** The only
file added is this one.

## Verification

Run on 2026-08-01 against this worktree, base `df36cb2`, after `dotnet build GigaClaw.ClaudeMock -c Release`:

```
$ dotnet run --project GigaClaw.Eval -c Release -- replay all
Replayed 38 fixture(s) across 37 agent(s) in mock mode: 38 pass(es), 0 failure(s).

$ dotnet run --project GigaClaw.Eval -c Release -- judge all
Judged 38 fixture(s) across 37 agent(s): 38 pass(es), 0 failure(s).
   ... every line reporting baseline=match

$ dotnet test GigaClaw.Eval.Tests
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39

$ dotnet test GigaClaw.Core.Tests
Passed!  - Failed:     0, Passed:  1246, Skipped:     0, Total:  1246
```

## Sign-off

Reviewed on 2026-08-01 by Claude (Fable 5), acting under delegated sign-off authority from the
repository owner. Population: 37 baseline files / 38 fixture entries under
`GigaClaw.Eval/baselines/judge/`, established from the tree and reconciled against the commits that
produced them. Verdicts: **28 PASS, 10 NEEDS-WORK, 0 FAIL**. Ten findings recorded with
recommendations; **zero fixed**, for the reason stated above. All four verification commands were
run and their output is quoted verbatim.

# Baseline review — owner decision sheet

**Date:** 2026-08-01 · **Source:** [`REVIEW-core-2026-08-01.md`](../../GigaClaw.Eval/baselines/REVIEW-core-2026-08-01.md) · **Tracked by:** [`PLAN-remaining.md`](PLAN-remaining.md) §3 item 7b

The review recorded ten NEEDS-WORK findings and six cross-cutting observations across 37 judge
baseline files / 38 fixture entries, and fixed **none** of them — every remedy needs an authoring
call the reviewer was not entitled to make on its own. This sheet is that call, one section per
decision, with the options costed against the tree rather than against the review.

**Every count and path below was re-derived from the tree**, not copied from the review. Where the
two disagree the correction is stated in the section. The re-derivation script walked all 37 files
in `GigaClaw.Eval/baselines/judge/` and confirmed: 38 entries, 37 SHIP / 1 FIX / 0 BLOCK, `vetoItems`
empty in all 38, 32 entries scored by `rubrics/default.json` and one each by the six bespoke
rubrics, and exactly 5 entries carrying the note `Forbidden phrase(s) present: TODO.`

**How to answer:** one line per decision — `D1: option B`, `D4: option A`, and so on. Skipping a
decision leaves that baseline as recorded; nothing degrades.

---

## D1 — the `TODO` forbidden token collides with the `Todo` board column

**Question.** Narrow the forbidden token from `TODO` to `TODO:` in the shared rubrics, or leave the
five 95/100 scores standing?

**What the review found.** `rubrics/default.json` forbids `["TODO","lorem ipsum","as an ai",
"placeholder text"]` and `RubricJudge` compares with `StringComparison.OrdinalIgnoreCase` by design
("a rubric asserts that a property was reported, not that it was capitalized a particular way",
`RubricJudge.cs:29`). Five runs are therefore docked 5 points under a criterion whose `Statement`
reads "The closing message contains placeholder or boilerplate text" — untrue of all five:
`design-researcher` ("Atomically transitioned the ticket to **Todo** assigned to ui-designer"),
`ui-auditor` ("Handed the ticket to ui-designer in **Todo**"), `producer` ("to local-media-director
(**Todo**)"), `code-janitor` ("The stale **TODO** references a v1 export path" — its literal job) and
`decision-engine` ("code-janitor's flagged **TODO**"). The same bare token also sits in
`rubrics/programmer.json`'s "No hedged handoff" list.

**Options.**

- **A — narrow to `TODO:` in both rubrics.** Changes `GigaClaw.Eval/rubrics/default.json` and
  `GigaClaw.Eval/rubrics/programmer.json`. **33 baseline entries across 32 files** re-recorded (all
  32 default-rubric entries in 31 files, plus `programmer.json`); 5 of them change score 95→100, the
  other 28 change only the rubric-digest evidence line. Regression: a placeholder line written as a
  bare `TODO` with no colon stops being caught. No verdict token moves — 95 % and 100 % are both
  above the 90 % ship threshold.
- **B — narrow in `default.json` only.** 32 entries / 31 files re-recorded. Leaves a known live
  landmine in `rubrics/programmer.json`: it has not fired only because `dev-fix-login-timeout`
  happens not to name a column, and the next programmer fixture that does will be docked.
- **C — leave it.** Zero files change. The judge keeps reporting "placeholder text present" about
  three agents that correctly named the column they moved a ticket to, and the note stays wrong on
  disk in five committed baselines.

**Recommendation: A.** A rubric that penalises an agent for correctly reporting board state is
measuring the opposite of what it claims; narrowing the token costs one character and removes the
false positive from both files at once, so the second one never has to be discovered by a failure.

**Blast radius:** `rubrics/default.json` + `rubrics/programmer.json` + 33 baseline entries in 32
files re-recorded (5 score changes) + no judge or test change.

---

## D2 — the default rubric measures almost nothing

**Question.** Redesign `rubrics/default.json` so the judge pass adds signal over the replay pass, or
accept that 32 of 38 baselines are graded on a length floor and a placeholder check?

**What the review found.** Cross-cutting items 1 and 2 are one decision: they change the same file
and force the same re-record. 32 of 38 entries are scored by four criteria — `replay-expectations`
(40), `no-error-events` (20), `final-text-min-length ≥ 80` (20), `final-text-omits-all` (20). The
40-point criterion restates the assertions `replay all` already makes; `no-error-events` is implied
by `ExitCode: 0`, which the tree confirms for **37 of 38** fixtures (`dev-suite-fails-hard` is the
only `ExitCode: 1`). The discipline-specific signal *does* exist — every one of the 38 fixtures
declares a `FinalTextContains` — but it collapses into the single all-or-nothing 40-point bucket
next to exit code, run status and event kinds, so a run with a perfect receipt and a wrong exit code
scores identically to one that exits 0 with no receipt.

**Options.**

- **A — split `replay-expectations` into per-assertion criteria** (final-text expectation graded
  separately from exit code / run status / event kinds). Changes `rubrics/default.json` and
  `GigaClaw.Eval/RubricJudge.cs` (a new check kind). 32 entries / 31 files re-recorded, **all 32
  changing** — category names and maxima are part of the recorded verdict. Regression: any baseline
  whose new score lands under 90 % flips SHIP→FIX, which would be a real signal but must be reviewed
  entry by entry rather than accepted from `--update-baselines`.
- **B — author bespoke rubrics for the 31 default-scored agents.** Adds up to 31 files under
  `GigaClaw.Eval/rubrics/`; 32 entries / 31 files re-recorded. This is the highest-signal option and
  the largest authoring job in the sheet — 31 agents' SKILL contracts read and turned into criteria.
  Regression: 31 new rubrics is 31 new chances to write another `approval-gatekeeper` (D3).
- **C — leave the default rubric, treat the judge as a thin gate.** Zero files change. The judge pass
  keeps costing a full replay for an 80-character floor plus a placeholder check.

**Recommendation: A, then B incrementally.** The fixtures already carry the contract markers; the
judge simply does not grade them separately, so A buys most of the missing signal by changing what is
weighted rather than by authoring 31 documents. B is the right destination but belongs behind D3–D7,
which show what a bespoke rubric costs when it is written against a scenario instead of a SKILL.

**Blast radius (A):** `rubrics/default.json` + `RubricJudge.cs` + 32 baseline entries in 31 files
re-recorded (all 32 change) + `GigaClaw.Eval.Tests` extended for the new check kind.

---

## D3 — `approval-gatekeeper` scores 100/100 for a run that breaks its SKILL

**Question.** Re-author `governance-reject-unscoped-release` to perform the gatekeeper's actual
contract, or keep the fixture as a prose-refusal exercise?

**What the review found.** The scenario's whole output is one turn beginning "Rejected.", with
`EventKinds: ["system","assistant","assistant","result"]` — zero tool calls, confirmed in
`fixtures/governance-reject-unscoped-release.json`. Against
`ProjectTemplate/Agents/approval-gatekeeper/SKILL.md` it misses the mandatory
`APPROVAL-RECEIPT v1 artifact-sha256:` first line (§72), the `privacy_guard.py` run and manifest, the
`pending-approval` label, the terminal state in `Review`, and the SKILL's requirement that the
comment "tell the owner exactly which two moves are available" (§97–102) — the agent issues the
decision itself, which the SKILL forbids ("Approval is a label plus an owner decision — never a
status of its own"). The rubric does not merely miss this, it rewards it: 60 of 100 points go to
`["rejected","approved","blocked"]`, `["rollback","sign-off"]` and
`["re-request","then re-","post the","resubmit"]`. **Verified against the tree:** a case-insensitive
grep of the SKILL for `reject`, `rollback`, `window`, `resubmit` and `re-request` returns **zero
hits**. Three-fifths of this rubric scores vocabulary that appears nowhere in the contract.

**Options.**

- **A — re-author scenario and rubric together.** Changes
  `fixtures/scenarios/governance-reject-unscoped-release.ndjson` (add tool calls for the guard and
  manifest, the receipt line, the two owner options, the `Review` landing),
  `fixtures/governance-reject-unscoped-release.json` (`EventKinds`, `FinalTextContains`) and
  `rubrics/approval-gatekeeper.json` (replace the four prose criteria with checks on
  `APPROVAL-RECEIPT v1 artifact-sha256:`, the two owner moves, and the absence of a self-issued
  decision). **1 baseline re-recorded.** Regression: none outside this fixture — nothing else cites
  this rubric.
- **B — rubric only.** Changes `rubrics/approval-gatekeeper.json`; 1 baseline re-recorded, and the
  score drops sharply because the scenario cannot satisfy criteria it was never written for. Honest,
  but it commits a permanently red baseline.
- **C — leave it.** Zero files change. A 100/100 SHIP stays on disk for a run that violates its own
  contract four ways — the exact shape the G6 review caught in the threat-modeler.

**Recommendation: A.** A rubric that awards 60 points for words its SKILL never uses is worse than
no rubric at all; fixing the rubric without the scenario (B) just relabels the same defect as a
failing score.

**Blast radius:** 1 fixture + 1 scenario + `rubrics/approval-gatekeeper.json` + 1 baseline
re-recorded.

---

## D4 — `growth-nurture-sequence` exercises another agent's discipline

**Question.** Re-point the fixture at a social post (growth-writer's actual contract), move it to
`email-copywriter`, or leave it?

**What the review found.** `ProjectTemplate/Agents/growth-writer/SKILL.md` defines a social
ghostwriter: `## Primary post` / `## Alternative hooks` / `## CTA variation`, artifact at
`content/social/<slug>.md`, three validators, receipt `GROWTH-COPY v1 artifact-sha256:`, then a move
to `Review` with `assignedTo` unchanged so `growth-approval-on-review` can fire. The scenario's final
text is: *"Drafted three emails in campaigns/trial-nurture.md … One call to action each and no
discounts."* — with `EventKinds: ["system","assistant","assistant","result"]`, so no validator ran;
no receipt, no status move. The rubric is fitted to the scenario rather than the contract (`three
emails`, `campaigns/`, `one call to action`, and an `unauthorized-discount` veto over
`["coupon","% off","free month","promo code"]` — none of it in the SKILL). `email-copywriter` already
does this job correctly in `growth-cold-email-outreach`, receipt and all.

**Options.**

- **A — re-point the fixture at a social post.** Changes `fixtures/growth-nurture-sequence.json`
  (likely renamed — `ReadFixture` enforces `fixture.Id == filename`), its scenario, and
  `rubrics/growth-writer.json`. **1 baseline re-recorded.** Fixture total stays 38.
- **B — move the fixture to `email-copywriter` and author a new growth-writer fixture.** Changes 2
  fixtures + 2 scenarios + `rubrics/growth-writer.json`; **2 baseline files** touched
  (`growth-writer.json`, `email-copywriter.json`); fixture total goes 38→39. Regression: if the move
  lands without the replacement, `growth-writer`'s `EvalFixturePresent` flips to `false` in
  `catalog.json` and `doc/catalog.md`, which the catalog drift check will fail on.
- **C — leave it.** Zero files change. `growth-writer` remains the one core agent whose only
  committed evidence is of it doing someone else's job at full marks.

**Recommendation: A.** It restores the instrument to the contract at the cost of one baseline, and
it avoids B's second-order catalog churn; `email-copywriter` already has a correct fixture, so the
email path is not the coverage gap here.

**Blast radius:** 1 fixture (renamed) + 1 scenario + `rubrics/growth-writer.json` + 1 baseline
re-recorded.

---

## D5 — `local-media-director` scores 100/100 without its SKILL's required artifacts

**Question.** Extend `media-storyboard-teaser` to write the execution spec the SKILL requires, or
accept a storyboard-prose fixture?

**What the review found.** The SKILL requires, in order, `AGENT_GUIDE.md` +
`provider_menu_summary()`, then **both** `media/specs/<ticket-id>.md` and
`media/specs/<ticket-id>.json` (execution-spec version 1, `governance` block naming
`approvalStatus`, `approvedBy`, `licenseNotes`, `layer3SkillsRead`), then a `GIGACLAW-HANDOFF v1`
comment. The scenario's final text is *"Wrote media/storyboard-teaser.md with five vertical
1080x1920 shots totalling 15s … No frames generated yet — awaiting sign-off"* — a path the SKILL
never names, one tool call, no spec, no governance block, no handoff. `rubrics/local-media-director.json`
scores `duration`, `1080x1920`, `awaiting sign-off`, `totalling`. The one criterion carrying real
weight, the `render-without-sign-off` veto, is sound and does hold.

**Options.**

- **A — extend scenario and rubric, keep the render gate.** Changes
  `fixtures/scenarios/media-storyboard-teaser.ndjson` (two spec writes + handoff comment),
  `fixtures/media-storyboard-teaser.json` (`EventKinds`, `FinalTextContains`) and
  `rubrics/local-media-director.json` (re-point at `media/specs/`, the spec version and the
  governance block; keep the veto verbatim). **1 baseline re-recorded.**
- **B — align the SKILL to the scenario** (declare `media/storyboard-*.md` the artifact). Changes
  `ProjectTemplate/Agents/local-media-director/SKILL.md`; 1 baseline re-recorded. This also touches
  every initialized workspace, since `ProjectTemplate/**` ships to all of them, and it drops the
  governance block that the downstream render gate reads.
- **C — leave it.** Zero files change; the only committed evidence for this agent shows it skipping
  its own artifact contract at full marks.

**Recommendation: A.** The governance block is what the render gate consumes downstream, so B would
weaken a live safety surface to make a fixture correct — the wrong direction for a fail-closed gate.

**Blast radius:** 1 fixture + 1 scenario + `rubrics/local-media-director.json` + 1 baseline
re-recorded.

---

## D6 — `blog-writer`'s rubric hard-codes one fixture's directory and misses the receipt

**Question.** Replace the rubric's `["content/drafts/", ".md"]` values with the SKILL's
`BLOG-DRAFT v1 artifact-sha256:` receipt marker?

**What the review found — with one correction.** The review framed this as a canonicity choice
(*"align the scenario's path with the SKILL, or the SKILL with the scenario"*). **The tree dissolves
half of it:** `ProjectTemplate/Agents/blog-writer/SKILL.md` §28 and §35 both read
`content/posts/<slug>.md` **"(or the path the ticket specifies)"** — so the scenario writing
`content/drafts/2026-05-scheduling-rework.md` is not a contract breach, and there is nothing to
adjudicate. What remains is unambiguous and one-sided: the agent-level criterion "Named the artifact
it produced" is `final-text-contains-any ["content/drafts/", ".md"]`, which hard-codes one fixture's
directory into a rubric that applies to every blog-writer run, with a fallback value (`.md`) that is
close to a tautology for any run naming a file. Meanwhile SKILL §45 and §55 both mandate the receipt
`BLOG-DRAFT v1 artifact-sha256:<digest>`, and it appears nowhere in the stream. The four other
blog-family baselines each pin their SKILL's versioned marker precisely; `blog-writer` is the odd one
out because it predates them.

**Options.**

- **A — scenario emits the receipt, rubric pins it.** Changes
  `fixtures/scenarios/blog-draft-release-notes.ndjson`, `fixtures/blog-draft-release-notes.json`
  (`FinalTextContains`) and `rubrics/blog-writer.json` (`["BLOG-DRAFT v1 artifact-sha256:"]`).
  **1 baseline re-recorded.** Brings blog-writer in line with rows 2–5.
- **B — rubric only: drop the path values, add the receipt marker.** Changes
  `rubrics/blog-writer.json`; 1 baseline re-recorded and the criterion scores 0 because the scenario
  emits no receipt. Records the gap honestly, fixes nothing.
- **C — leave it.** Zero files change; the criterion keeps passing on `.md`.

**Recommendation: A.** The receipt is the only part of this the SKILL states unconditionally, and
pinning it makes blog-writer consistent with the four blog baselines that already do. See "Findings
that may not need a decision" for the path half.

**Blast radius:** 1 fixture + 1 scenario + `rubrics/blog-writer.json` + 1 baseline re-recorded.

---

## D7 — `dev-suite-fails-hard` reaches a non-SHIP verdict for the wrong reason

**Question.** Add a `GIGACLAW-VERDICT v1 qa-tester BLOCK` criterion (with a veto) and let the
baseline record **BLOCK**, or drop the recovery-path criterion and let a clean hard-fail score at its
ceiling?

**What the review found — with one correction.** This is the only non-SHIP entry in the whole set:
FIX at 80/100, with "Stated a recovery path" scoring 0/20 because the final text contains none of
`["re-run","rerun","retry","unblock","next step"]`. But
`ProjectTemplate/Agents/qa-tester/SKILL.md:65` names this exact situation —
`cannot-exercise-change`: "Application fails to start, missing environment setup, or test execution
impossible (`BLOCK`)" — and §69 says BLOCK means move the ticket to `Blocked`. The scenario posts no
verdict and moves nothing. So the contract breach is "no BLOCK verdict, no transition", and the
rubric penalises a phrasing rule the SKILL never states. **The review offered a second reading — a
harness-level hard crash in which "the agent never got a turn to post" — and the tree does not
support it:** the scenario contains an assistant turn with a full final message ("The isolated test
instance could not start: port 5232 is already bound. No test result can be reported from this
run."). The agent spoke and chose not to discharge its contract. Only the first reading survives.

**Options.**

- **A — grade the contract.** Changes `rubrics/qa-tester.json` (replace "Stated a recovery path"
  with a `GIGACLAW-VERDICT v1 qa-tester BLOCK` criterion carrying a veto),
  `fixtures/scenarios/dev-suite-fails-hard.ndjson` (post the verdict, move to `Blocked`) and
  `fixtures/dev-suite-fails-hard.json` (`FinalTextContains`). **1 baseline re-recorded, and it flips
  FIX→BLOCK** — the first non-empty `vetoItems` in the set, which also closes most of D8.
- **B — drop the recovery-path criterion.** Changes `rubrics/qa-tester.json`; 1 baseline re-recorded,
  score rises to 100 % and the verdict flips FIX→SHIP. That would make the set 38 SHIP / 0 FIX /
  0 BLOCK — the judge would have no non-SHIP baseline at all.
- **C — leave it.** Zero files change; the only non-SHIP evidence in the set names the wrong failure.

**Recommendation: A.** The SKILL states the rule verbatim (`cannot-exercise-change` → `BLOCK` →
`Blocked`), so this is the one finding where the instrument can be pointed at the contract without
inventing new vocabulary — and B would delete the last non-SHIP baseline the suite has.

**Blast radius:** 1 fixture + 1 scenario + `rubrics/qa-tester.json` + 1 baseline re-recorded
(FIX→BLOCK, first non-empty `vetoItems`).

---

## D8 — no committed baseline exercises the judge's BLOCK path

**Question.** Author one veto-tripping fixture so the judge's `vetoItems` → BLOCK branch is pinned by
a baseline, or leave that branch covered only by unit tests?

**What the review found.** Confirmed mechanically across all 38 entries: 37 SHIP, 1 FIX, **0 BLOCK**,
and `vetoItems` is `[]` in **every single one**. Every veto code declared in every rubric —
`replay-expectation-unmet`, `stream-error`, `gate-waived`, `unsourced-claim`,
`unauthorized-discount`, `render-without-sign-off`, `unsupported-pass-claim` — is untested by any
committed baseline. `RubricJudge.Score` derives BLOCK from `vetoItems.Count > 0` before any
threshold comparison (`RubricJudge.cs:81`); a regression dropping veto handling entirely would leave
all 38 baselines matching.

**Options.**

- **A — one new veto-tripping fixture on an existing agent.** Adds a fixture + scenario under
  `GigaClaw.Eval/fixtures/`, appends an entry to that agent's existing baseline file. **0 existing
  baselines change.** Verification bar moves 38→39 for both `replay all` and `judge all`. Catalog is
  unaffected (the agent already has `EvalFixturePresent: true`).
- **B — take D7 option A as the coverage.** 0 additional files. Gets one non-empty `vetoItems` and one
  BLOCK on disk for free, but only exercises `unsupported-pass-claim`; the other six veto codes stay
  untested.
- **C — leave it.** Zero files change; the veto→BLOCK branch stays covered by `GigaClaw.Eval.Tests`
  only.

**Recommendation: A, after D7.** Fail-closed behaviour that no fixture exercises is behaviour nobody
would notice losing — and D7's BLOCK, while welcome, pins one code out of seven.

**Blast radius:** +1 fixture + 1 scenario + 1 appended baseline entry; `replay all` / `judge all`
bar becomes 39.

---

## D9 — the judge's verdict token means something different from the agent's

**Question.** Document the collision in `doc/verdict-contract.md` and the baseline headers, or give
the judge a distinct token set?

**What the review found.** `baselines/judge/security-auditor.json` records `"verdict": "SHIP"` for
`security-injection-in-review`, a fixture whose entire purpose is that the *agent* emits **BLOCK**.
Both are correct — the judge scores the agent's performance, the agent scores the code — but the
same word carries two meanings in files one directory apart, and `threat-modeler` and `ui-auditor`
have the same shape. `doc/verdict-contract.md:16` defines the agent-side vocabulary as
`SHIP` / `FIX` / `BLOCK`; `RubricJudge.cs:81-85` derives the judge-side token from the identical
three-word set.

**Options.**

- **A — documentation only.** Changes `doc/verdict-contract.md` (a paragraph distinguishing the two
  axes) and optionally a header note in the review. **0 baselines re-recorded.** Regression: none —
  the ambiguity remains on disk, just labelled.
- **B — distinct judge token set.** Changes `RubricJudge.cs`, `JudgeModels.cs`,
  `doc/verdict-contract.md`, `GigaClaw.Eval.Tests`, and **all 38 baseline entries across 37 files**.
  Regression: the judge verdict is a `JudgeVerdict.Verdict` string consumed by baseline comparison;
  anything reading these files by token — including future `verdictIs` wiring — would need auditing.
- **C — leave it.** Zero files change.

**Recommendation: A.** The collision is a readability hazard, not a correctness one; B spends a
38-baseline re-record and a code change on a naming preference, which is the worst
cost-to-signal ratio on this sheet.

**Blast radius:** `doc/verdict-contract.md` only; 0 baselines re-recorded.

---

## D10 — three orphaned `handoff/v1` documents sit inside a replay-fixture root

**Question.** Delete `GigaClaw.Eval/fixtures/handoffs/`, move it out of the fixture root, or wire it
into the G3 multi-hop chain it was written for?

**What the review found — confirmed.** `fixtures/handoffs/{hop1-blog-researcher,hop2-blog-writer,
hop3-blog-reviewer}.json` came in with G3 and are referenced by **no code, no test and no doc** —
a tree-wide grep for `hop1-blog-researcher` and `fixtures/handoffs` returns hits only in the review
itself. They are not replay fixtures (no `Version`/`Id`/`Family`/`Agent` fields) and survive only
because `ReplayRunner.LoadFixtures()` calls `Directory.EnumerateFiles(root, "*.json")` with no
`SearchOption.AllDirectories` — making that enumeration recursive would throw in `ReadFixture` on
"Unsupported fixture version 0". G3 is still open (`PLAN-remaining.md` §5 item 10).

**Options.**

- **A — delete the three files.** 0 baselines change, `replay all` stays 38. Regression: whatever
  authoring intent they encode is lost, though git retains them.
- **B — move them under a non-fixture path** (e.g. `doc/drafts/g3-handoffs/`). 0 baselines change.
  Removes the trap where making the enumeration recursive crashes the runner, and keeps the material
  for G3.
- **C — wire the chain up now.** Requires the multi-hop replay G3 intends: recursive enumeration,
  a fixture schema that accepts a hop document, plus three new baselines. Substantial, and it is G3's
  work, not this sheet's.

**Recommendation: B.** It costs nothing, removes the latent crash, and leaves G3 its input — deleting
(A) discards material for an open roadmap item on the grounds that nothing references it yet.

**Blast radius:** 3 files moved; 0 baselines re-recorded; `replay all` stays 38.

---

## Findings that may not need a decision — confirm

- **`evalconfig.json` drift (cross-cutting 6) — could proceed without a decision, confirm.** The
  review reports `PromptBudget.Source` still reading `"ProjectTemplate/Agents/{agent}/SKILL.md"` and
  `Version` still `1`, where `doc/pack-infrastructure.md:374` planned `"{packRoot}/Agents/{agent}/SKILL.md"`
  and Version `2`. Both confirmed. **What the review does not say is that the string is purely
  declarative:** `StaticEvalRunner.EvaluateAgent` resolves the SKILL through
  `AgentsRootFor(agent.Pack)` (`StaticEvalRunner.cs:90, 435`), which already sends pack agents to
  `Packs/<pack>/Agents` — the four security-assurance SKILLs are being read from the right place
  today. The only blocker is `ValidateConfig`'s two literal equality checks
  (`StaticEvalRunner.cs:409, 413`). Changing them plus the config is mechanical, changes **0
  baselines** (the 37 flat static baselines record only `Id`/`Category`/`Status`, and no check status
  moves), and the decision was already taken in `doc/pack-infrastructure.md`. Confirm and land it
  with any pass.
- **`blog-draft-release-notes`'s artifact path (half of D6) — could proceed without a decision,
  confirm.** The SKILL explicitly permits "the path the ticket specifies", so the scenario is not in
  breach and no canonicity call is owed. Only the receipt half of D6 needs an answer.
- **`governance-watchdog-probe-red`'s expectation (row 18's nit, not among the ten).** The fixture
  asserts `FinalTextContains: "target=http:"`, which matches the prose fragment
  `target=http:staging.example.local/webhooks/cms-dispatch` rather than the SKILL's
  `[system-watchdog:v1 target=… evidence=…]` marker, which appears nowhere in the stream. Re-pointing
  the expectation is the same one-line shape as D6; it can ride along, but it is a rider, not a
  decision.

---

## Suggested batching

**Pass 1 — the wide re-record (D1, and D2 if answered A).** These are the only two decisions that
touch `rubrics/default.json`, and each forces the same 31 files. Executing them separately means
re-recording the same 32 entries twice, so answer both before either is executed. Fold the
`evalconfig.json` + `ValidateConfig` change from "Findings that may not need a decision" into this
pass — it costs nothing and changes no baseline.

**Pass 2 — the five single-fixture repairs (D3, D4, D5, D6, D7), plus D8 and the watchdog rider.**
Each is independent: a distinct fixture, a distinct scenario, a distinct bespoke rubric, and exactly
one baseline file. None cites `default.json`, so pass 2 does not conflict with pass 1 and the two can
be executed in either order. D7 must precede D8 so the veto-coverage fixture is authored against
whatever D7 decides `dev-suite-fails-hard` means.

**Pass 3 — the no-re-record items (D9 option A, D10 option B).** Documentation and a file move; can
land with either pass or alone.

**The verification bar for any pass** (run after `dotnet build GigaClaw.ClaudeMock -c Release`):

```
dotnet run --project GigaClaw.Eval -c Release -- replay all     → 38/38 (39 if D8-A lands)
dotnet run --project GigaClaw.Eval -c Release -- judge  all     → 38/38 (39 if D8-A lands),
                                                                   every line baseline=match
dotnet test GigaClaw.Eval.Tests                                 → 39/0 (more if D2-A adds a check kind)
dotnet test GigaClaw.Core.Tests                                 → no change expected
dotnet run --project GigaClaw.Catalog -- check --projects <root> → no drift
git diff --stat                                                 → exactly the files this sheet names
```

The `git diff --stat` line is the load-bearing one. `judge all --update-baselines` will happily
rewrite anything, so the file list is what proves a re-record was deliberate rather than a sweep: for
pass 1 answered D1-A + D2-A, that is 2 rubric files, 1 source file, `evalconfig.json`, and exactly 32
baseline files — no more.

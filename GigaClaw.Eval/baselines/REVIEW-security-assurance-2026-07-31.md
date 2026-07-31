# Baseline review — security-assurance pack agents

- **Date:** 2026-07-31
- **Scope:** the four committed eval baselines for the security-assurance pack agents —
  `baselines/security-auditor.json`, `baselines/threat-modeler.json`,
  `baselines/supply-chain-reviewer.json`, `baselines/secrets-reviewer.json` — reviewed together
  with the replay fixtures they are meant to pin
  (`Packs/security-assurance/eval/fixtures/*.json`), the canned scenario streams
  (`Packs/security-assurance/eval/fixtures/scenarios/*.ndjson`), each agent's `SKILL.md`, and the
  shared severity ladder (`Packs/security-assurance/Agents/security-auditor/references/severity.md`).
- **Why this review exists:** `doc/pack-infrastructure.md` §9 defines the flat baselines under
  `GigaClaw.Eval/baselines/` as "the *reviewed* snapshot — a core-owned review artifact about pack
  content". The snapshots were generated and green, but unreviewed. This record closes that gap.
- **Method:** every claim below was verified against the live tree, not the reports. The static
  eval was re-run per agent (`dotnet run --project GigaClaw.Eval -- static <slug> --no-report`);
  the replay CLI was invoked against the security fixtures to test whether they are reachable.

## Verdict summary

| Baseline | Verdict |
|---|---|
| `security-auditor` | **PASS** |
| `secrets-reviewer` | **PASS** (one minor scenario nit) |
| `supply-chain-reviewer` | **PASS** |
| `threat-modeler` | **NEEDS-WORK** — recorded verdict contradicts the pack's own severity ladder and the SKILL's veto table |

All four PASS verdicts carry the cross-cutting caveat in "Owner attention" below: the replay
fixtures behind these baselines have **never executed**, so their expectations are reviewed on
paper only.

## Per-baseline findings

### security-auditor — PASS

- **Static baseline** (`security-auditor.json`): 7 integrity checks, all `pass`, ordinal-sorted by
  id. Re-run live 2026-07-31: 7/7 pass, matches the committed snapshot. The pack-aware SKILL
  resolution (`StaticEvalRunner.AgentsRootFor` → `Packs/security-assurance/Agents/`) is what the
  checks actually exercised — the snapshot is genuine, not a stale ProjectTemplate artifact.
- **Fixture** `security-injection-in-review` (ticket 512): a `code`-labeled ticket in `Review`
  adds `POST /api/export` that interpolates `request.Filter` into a `/bin/sh -c` command via
  `Process.Start`, with no `[Authorize]`. Per the shared ladder that is Critical (unauthenticated
  reachability × command execution) → `BLOCK — always, no exceptions`. The recorded verdict is
  BLOCK with veto codes `critical-finding-unmitigated` and `unauthenticated-privileged-path`, both
  of which exist in the SKILL's machine-checkable table with verdict `BLOCK`. A competent security
  reviewer reaches the same verdict from the same input.
- **Decision surface:** exercises the hard-veto BLOCK path, the inputDigest/first-path-evidence
  rule, the "findings not fixes" write-scope rule, and asserts the verdict token itself
  (`FinalTextContains: "BLOCK"`). Good.
- **Drift:** none. The scenario mirrors the SKILL's worked verdict for the same ticket 512 —
  self-consistent, though note this couples the fixture to the SKILL's example (if the example is
  rewritten, the scenario should be re-checked).
- **Nondeterminism:** none. Canned NDJSON; `ReplayRunner.Normalize` scrubs workspace paths and
  session ids; timings/costs are never recorded.

### secrets-reviewer — PASS (minor nit)

- **Static baseline** (`secrets-reviewer.json`): 7/7 pass, re-verified live. Matches.
- **Fixture** `security-secret-in-diff` (ticket 518): detector output shows two candidates — a
  Slack `xoxb-` token in `src/Worker/appsettings.Production.json` (read at runtime, ships in
  deployed config) and `AKIAIOSFODNN7EXAMPLE` in `tests/Fixtures/tokens.json`. The recorded triage
  — live credential → `live-credential-in-diff` → BLOCK; the AWS value is the vendor's published
  documentation example key used only under test → fixture, triage notes only — is exactly the
  discrimination the SKILL demands, and the one a competent reviewer would make. Credential
  disclosure is Critical on the ladder; BLOCK follows.
- **Decision surface:** exercises the detector-first rule (exit code recorded), the two-way triage
  (live vs fixture in the same run — not a trivial single-candidate case), the never-quote-the-value
  rule (only the detector's truncated excerpt appears), the never-verify-by-use rule, and the
  rotate-first escalation. `FinalTextContains: "live-credential-in-diff"` pins the veto code.
- **Minor nit (no action required):** the scenario's opening narration claims the detector runs
  over "every changed file plus the env, CI and container files", but the canned command lists only
  the three changed files plus `.env.example` — no CI or container file. Dimension-4 coverage is
  narrated wider than it is demonstrated. Cosmetic in a mock, but worth tightening if the scenario
  is ever regenerated.
- **Nondeterminism:** none (same canned-stream argument as above).

### supply-chain-reviewer — PASS

- **Static baseline** (`supply-chain-reviewer.json`): 7/7 pass, re-verified live. Matches.
- **Fixture** `security-supply-chain-advisory` (ticket 731, weekly cron audit): the SBOM delta
  surfaces newly-added `serialize-lib 3.2.1`; the advisory is flagged known-exploited, fixed in
  3.4.0, and the vulnerable entry point is reachable from `/api/import`. Recorded verdict BLOCK
  with veto `known-exploited-vulnerability` — matches the SKILL's table (`BLOCK`) and the ladder.
  Correct, and the honest parts are the right parts: 2 of 6 reachability calls recorded as
  *undetermined* rather than silently passed, scoring triage 12/25 below the floor.
- **Decision surface:** exercises the offline-inventory-first rule, the declared-hosts/GET-only/
  coordinates-only network conduct, the composite-digest rule (`hash` + `link` evidence, **no**
  `path` evidence — the pack's most error-prone contract rule), and the dry-run remediation rule
  (no lockfile edits, no PR, remediation as a programmer ticket). `FinalTextContains:
  "known-exploited-vulnerability"` pins the veto code.
- **Drift:** none against the current SKILL; scenario mirrors the SKILL's worked verdict
  (ticket 731) including the 2026-07-27T04:02Z query timestamp — fine for a canned mock.
- **Nondeterminism:** none. The embedded timestamp is canned text, not a clock read.

### threat-modeler — NEEDS-WORK

- **Static baseline** (`threat-modeler.json`): 7/7 pass, re-verified live. The static snapshot
  itself is accurate — the needs-work applies to the fixture/scenario pair it stands beside.
- **Fixture** `security-threat-model-auth` (ticket 604): a share-link design whose amendment
  removes the tenant filter from the token-resolve path, so any link holder reads across tenants.
- **Finding 1 — the recorded verdict contradicts the pack's own rules.** The scenario records:
  "That is Critical, so unmitigated-critical-threat is a veto item and **the verdict is FIX**".
  But:
  - the shared severity ladder (which opens by saying it exists "so a Critical means the same
    thing in four verdicts") lists cross-tenant data read as a Critical effect with verdict
    "`BLOCK` — always, no exceptions";
  - the threat-modeler SKILL's own machine-checkable table assigns `unmitigated-critical-threat`
    → `BLOCK`.
  The scenario faithfully mirrors the SKILL's worked verdict for ticket 604, which carries the
  **same internal contradiction** (worked example: FIX; veto table: BLOCK). So the defect
  originates in `Packs/security-assurance/Agents/threat-modeler/SKILL.md` and was replicated into
  the eval scenario. One of the two must be corrected — either the veto table means "forbids SHIP,
  FIX allowed for design-stage findings" (then the table's Verdict column and the ladder's
  "always, no exceptions" need rewording for the design lane), or the worked verdict and scenario
  must become BLOCK. A competent reviewer applying the pack's stated rules as written would emit
  BLOCK, not FIX.
- **Finding 2 — the fixture does not pin the decision.** `FinalTextContains` is `"no path
  evidence"`, which pins the hash-only evidence rule (a good, non-trivial choice) but not the
  verdict token. Unlike the other three fixtures, a future regeneration that flips FIX↔BLOCK — or
  drops the veto entirely — would still pass this fixture. Once Finding 1 is resolved, the
  expectation should also assert the chosen verdict token.
- **What is right and should be kept:** the scenario's mechanics are high-fidelity — it hashes
  `.agents/channel/ticket-604.md`, which is exactly the file `ReplayRunner` materializes for the
  ticket; the digest matches the SKILL's worked example; the no-path-evidence discipline and the
  `mitigation-not-testable` finding ("tokens are unguessable" names no length, randomness, expiry,
  revocation) are exemplary.
- **Per instruction, the baseline and fixture were not modified.** The correction belongs with the
  SKILL reconciliation, then a deliberate re-record.

## Owner attention (cross-cutting)

1. **The four replay fixtures have never executed.** `evalconfig.json` still declares the singular
   `Replay.FixtureRoot: "GigaClaw.Eval/fixtures"` and `ReplayRunner.LoadFixtures()` enumerates only
   that directory, non-recursively. Verified live: `eval replay security-injection-in-review` and
   `eval replay security` both fail with "Unknown replay target"; the known-fixture list contains
   only the six core fixtures. The §9 plan ("`Replay.FixtureRoot` … becomes a list" including
   `Packs/<id>/eval/fixtures`) is not yet implemented. Additionally, `ReplayRunner.Replay()` at
   `GigaClaw.Eval/ReplayRunner.cs:247` hardcodes `templateRoot = ProjectTemplate/Agents` for the
   workspace SKILL/MEMORY copy, so even after the fixture roots are wired, pack-agent replays will
   fail until that copy resolves through the catalog's `Pack` field (as the static layer already
   does). Consequence: "all automated checks pass" for these four agents currently means the
   **static integrity layer only**; every `Expect` block reviewed above is unexecuted paper.
2. **`catalog.json` reports `EvalFixturePresent: true` for all four** — true as a statement about
   files on disk, misleading as a statement about eval coverage, per item 1.
3. **`evalconfig.json` drift from §9:** `PromptBudget.Source` still reads
   `"ProjectTemplate/Agents/{agent}/SKILL.md"` and config `Version` is still 1; §9 planned
   `"{packRoot}/Agents/{agent}/SKILL.md"` and Version 2. Cosmetic — the budget check reads the real
   pack SKILL bytes (verified) — but the label is now wrong for pack agents.
4. **No SHIP-path fixture anywhere in the pack.** All four fixtures exercise negative verdicts. A
   regression that makes an agent BLOCK everything would pass the entire suite. The
   secrets-reviewer SKILL already contains a worked clean-run SHIP verdict that could seed one.
5. **No judge rubrics or judge baselines for the four agents** (`rubrics/` and `baselines/judge/`
   contain core agents only; they would fall back to `rubrics/default.json`). Moot until item 1 is
   fixed, but it should ride along with that work.
6. **Determinism:** no flakiness risk found in any of the four. Static baselines are ordinal-sorted
   with no timings; scenarios are canned NDJSON; the replay layer scrubs session ids and workspace
   paths and never records timings or costs.

## Sign-off

Reviewed on 2026-07-31 by Claude (Fable 5), acting under delegated sign-off authority from the
repository owner on behalf of the merge-queue owner. Verdicts: security-auditor PASS,
secrets-reviewer PASS, supply-chain-reviewer PASS, threat-modeler NEEDS-WORK. Nothing in
`GigaClaw.Eval/baselines/*.json` or `Packs/security-assurance/**` was modified by this review.
Not committed — staging and commit are left to the owner.

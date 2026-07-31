# Session handoff — 2026-07-30

Written for whoever picks this up next. Everything below was read from the tree, not from a report.

## Where the work is

| | |
|---|---|
| **PR** | [#9 — SP-2 close-out](https://github.com/FoodBreakPedro/GigaClaw/pull/9) |
| **Branch** | `lane/sp2-sp3-consolidation` (pushed), 40 commits off `94971fb` |
| **Local `main`** | points at the same commit as the branch; `origin/main` is still at `94971fb` until #9 merges |
| **Roadmap artifact** | https://claude.ai/code/artifact/b0c190ed-cd5d-4fd4-8114-fe20c256a78c — the live board (lane meters, per-task acceptance criteria, sync gates, dependency graph). Update it by republishing to that same URL, or it mints a new one. |

### Verification on the branch head

`GigaClaw.Core.Tests` **1050 / 0** · `GigaClaw.Eval.Tests` **27 / 0** · `catalog check --strict` **0** · `catalog check --strict-packs` **0** · `Eval -- all` **0** (37 agents, 0 errors, 0 warnings).

Re-run all of it with:

```
dotnet test GigaClaw.Core.Tests -c Release
dotnet test GigaClaw.Eval.Tests -c Release
dotnet run --project GigaClaw.Catalog -- check --strict
dotnet run --project GigaClaw.Catalog -- check --strict-packs
dotnet run --project GigaClaw.Eval -c Release -- all
```

## Working docs, in reading order

1. [`doc/roadmap/index.md`](index.md) — lane model, decision log, living lane status.
2. [`doc/roadmap/lane-*.md`](.) — per-lane task specs with acceptance criteria. **The lane docs are authoritative on scope boundaries** and were right twice this session where the summary view was wrong.
3. [`doc/pack-infrastructure.md`](../pack-infrastructure.md) — the C9 spec, owner-approved. §2 layout, §3 manifest, §6 the extraction invariant, §7 the five-binding gate, §10 the four owner decisions.
4. [`doc/verdict-contract.md`](../verdict-contract.md) · [`doc/handoff-contract.md`](../handoff-contract.md) · [`doc/executable-teams.md`](../executable-teams.md).
5. [`GigaClaw.Core/Automation/Policy/SP1-REVIEW.md`](../../GigaClaw.Core/Automation/Policy/SP1-REVIEW.md) — per-agent enforcement sign-off, now updated to record that R3 enforces it.

**Treat the specs as fallible.** Three §-level claims were stale or wrong this session and are now corrected in place: §6's `__pycache__` hazard (fixed long before), §6's "exactly two new paths" (only one is possible), and §8's `teams.json` shape (bare array; D8 ships the object form).

## Gate state

- **SP-1** — signed off *and enforced*. 31 agents in `block`; `programmer` and `code-janitor` held in `warn` because both declare `**` write globs, so there is no out-of-glob write for a flip to block.
- **SP-2** — **closed.** All six gated pipelines enforce typed verdicts. Five reviewers, six pipelines: `blog-reviewer` serves both a file-based and an AD-7 path, and only the first was converted originally.
- **SP-3 / SP-4** — frozen. Every task feeding them is in the parked runtime lane.

## Outstanding work

### Unparked

| Item | Notes |
|---|---|
| **Wire `OutboundApprovalGate` into `ActionExecutor`** | R3's last criterion. The gate is built and tested (`GigaClaw.Core/Automation/Policy/OutboundApprovalGate.cs`, 16 tests) but deliberately unwired: both the lane doc and `doc/roadmap/index.md:76` designate this preflight a **CL/shared merge window**. The lane doc also warns "an agent-mutable label alone is insufficient" — agents hold board-write and can set labels, which is why the trust anchor is the owner's `settings.json`, outside every workspace. Needs the runner integration tests and the CMS-dispatch regression too. |
| **`R8` Codex harness** | Blocked on R7, which is parked. Codex hit its weekly usage cap on 2026-07-30 — part of why the runtime lane is the one deferred. |
| **`G6` pack baselines review** | The four security agents' baselines were generated and all checks pass, but §9 calls a baseline a *reviewed* snapshot. Nobody has reviewed them yet. |

### Parked by owner call — R3 is done, R4–R7 are not

R4 (leases) → R5 (worktrees) → R6 (merge queue) → R7 (runner interface) → R8. **R4 is now unblocked**: its dependencies R3 and C6 are both landed. Parking these also holds C5 (needs R4's leases and nothing else), C7 (needs R3+R5) and C8 (needs C5).

### Smaller carried-forward items

- Core is exempt from the eval-fixture binding — 27 agents reported, never gated (owner Q2). Closing it means writing 27 replay fixtures, not changing code.
- `Eval -- all` prints `0 error(s)` while exiting 1: the summary counts *checks*, and `baseline.missing` is not a check. This nearly hid a real failure.
- `{assignee}` expansion is still duplicated between `CatalogGenerator.ReadAutomations` and `TemplateAutomationContractTests`. The former is now public and pack-aware and ready to be the single implementation; the latter walks the typed model and would lose a structural assertion in a naive swap.
- `models.json`'s header comment says security/reviewer agents must not use sub-Opus tiers — contradicted by owner decision Q3. The real caveat is narrower (avoid Fable).
- Owner decision **Q3 is ambiguous**: it names `security-auditor` *and* `threat-modeler` but justifies only the auditor. Current state follows the explicit binding (auditor → Sonnet, threat-modeler → Opus). Worth a one-line confirmation.

## Windows CI

**CI was already red before this branch existed** — the run on `94971fb`, this session's merge base, failed with 2 tests. The branch did not break Windows; Windows was already broken.

Fixed here:

- **Separator normalization in `EmbeddedPackSource`.** `%(RecursiveDir)` yields backslashes on Windows, so a resource name could differ from the `LogicalName` template's literal `/` and fail the prefix comparison. This was believed at the time to be the cause of assets vanishing on Windows. **It was not** — assets never vanished; see below. Windows CI later reported `AgentRelativePaths=104`, i.e. enumeration was already complete. Keep it as hardening, and describe it as hardening: it is defensible on its own terms and pinned by `CorePackEnumerationTests`, but it fixed no observed failure.
- **A CRLF regex.** With `RegexOptions.Multiline`, .NET anchors `$` before `\n`, which on a CRLF checkout is *after* the `\r`, and `\S+` cannot consume `\r`. The verdict-marker pattern never matched on Windows.

### The "install drops 114 files" bug: resolved, and it was never an install bug

**Nothing was ever dropped.** The installer wrote all 119 files on Windows, correctly, every time. What was broken was the assertion message that reported the failure.

`CoreInitManifestTests` printed its content-drift failure through this call:

```csharp
Assert.True(changed.All(MergeArtifacts.Contains),
    Describe(changed.Where(c => !MergeArtifacts.Contains(c)).ToList(), [], []));
```

`Describe(missing, added, changed)` — so the **changed** list was passed in the **missing** slot, with the other two counts hardcoded to zero and printed under the heading "Missing (in manifest, not written)". A pure content drift across all 115 non-merge-artifact files therefore rendered as, exactly, `missing=115 added=0 changed=0` with "the four survivors are the merge artifacts". Every subsequent inference — files vanishing, staging sweeps eating the tree, rollback deleting pre-images, content deciding whether a write lands — was drawn from that one mislabeled string. Two fixes were attempted against it and both were wrong because the symptom they were aimed at did not exist.

Once the message told the truth, the real signature was `missing=0 added=1 changed=115`: everything landed, and every text file differed in bytes.

**The cause was the checkout, not the code.** Git for Windows defaults to `core.autocrlf=true`, and the repo had no `.gitattributes` governing content. Verified with a real clone: `git -c core.autocrlf=true clone` produced **120 of 120 `ProjectTemplate` text files with CRLF**; with the new `.gitattributes` in place, the same clone produces **0**. Since `ProjectTemplate/**` is embedded verbatim into `GigaClaw.Core.dll` and written byte-for-byte into workspaces, a Windows build shipped different content than the same commit produced anywhere else — and shipped `.py` content is executed, so this was a real product defect, just not the one it looked like.

The macOS "reproduction" was the same illusion: editing four scripts changes their bytes, so their hashes stop matching the golden manifest. That is the test working. It printed as "missing".

Fixed here:

- **`.gitattributes` pins `* text=auto eol=lf`**, with `*.bat`/`*.cmd` kept CRLF. Thirteen files committed with CRLF (none shipped in a pack) were normalized in the same pass so the index and the attribute agree. *Existing Windows clones need `git add --renormalize .` or a fresh clone.*
- **The assertion message reports each category in its own slot**, and now counts installed files containing CRLF, naming that cause in the failure text rather than leaving it to be re-derived.
- **`PackInstaller` verifies before it commits.** `VerifyEverythingPlannedReachedDisk` checks every planned destination exists on disk before the lockfile is written; a violation rolls the install back instead of committing a workspace that quietly does less. `install.Written` was only ever `plan.Select(…)` — what was *planned*, never what was *verified* — which is why a hypothetical drop would have looked like a success.
- **The four shipped scripts pin their streams to UTF-8.** `handoff_contract.py:297` prints `→`, and Python on Windows defaults stdout to cp1252, so the script raised `UnicodeEncodeError` *after* validating successfully — exit 2, a valid handoff read as rejected. Seven print sites across four scripts (`→`, `·`, `—`, `é`). The host already decodes these streams as UTF-8 (`ProcessLifecycleManager`, `DashboardScriptRunner`), so the streams are pinned rather than the output degraded to ASCII. Reproduced on macOS with `PYTHONIOENCODING=cp1252` (exit 2, `'charmap' codec can't encode '→'`) and confirmed fixed (exit 0).
- **`TemplateScriptEncodingTests`** makes that Windows-only defect assertable everywhere: it runs the validators under a pinned cp1252 stream, checks the characters survive the round trip, and fails if any shipped script prints non-ASCII without pinning. Verified to fail when the fix is removed.

Two of the three exemptions are gone. `CoreInitManifestTests` and `TemplateHandoffContractTests` now run on Windows.

### CI now builds on three platforms

The job ran only on `windows-latest` for its entire life, on a project developed on macOS. That is the condition that made all of the above possible: a platform-shaped defect was either invisible or the only thing visible, and neither state gets read correctly. It is now a `fail-fast: false` matrix over `ubuntu-latest`, `windows-latest` and `macos-latest`, so a divergence shows up as *which platforms disagreed* in a single run. All gates were verified green on macOS locally before the matrix landed.

A step ahead of everything else asserts the checkout itself: `git ls-files --eol -- ProjectTemplate Packs` must report no `crlf`/`mixed` working-tree entries. The bytes Git hands the build **are** the product for those trees, so that is checked before anything downstream compares a hash and reports the mismatch as something more exotic.

### One test is still exempted on Windows

| Test | Status |
|---|---|
| `JudgeRunnerTests.Judge_MatchesTheCommittedBaselineForEveryFixture` | Undiagnosed, pre-existing since before `94971fb`. |

`grep -rn KnownWindowsFailureFact` remains the debt list — now exactly one entry. The reasoning for keeping the job green with a named exemption rather than leaving it red: CI had been failing on `windows-latest` since before 2026-07-30, and because red was the normal state, two real defects sat in it unnoticed.

**CRLF is ruled out for it.** Converting `GigaClaw.Eval/**`, `GigaClaw.Eval.Tests/**`, `GigaClaw.ClaudeMock/**`, `ProjectTemplate/**` and `Packs/**` to CRLF and re-running left all 27 eval tests passing, so it does not share a cause with the manifest failure.

**It is now interrogable instead of merely silenced**, which is the actual reason it survived this long — a skipped test emits no diagnostics, so CI could never say anything about it:

- `GIGACLAW_RUN_KNOWN_WINDOWS_FAILURES=1` runs the exempted tests anyway. One Windows run with that set should answer the question.
- On drift the test now prints the committed verdict beside the produced one, field by field, plus `OSDescription` and `Path.GetTempPath()`. Verified to render by corrupting a baseline locally.
- It names the two live hypotheses so the reader does not start from zero: if only `evidence[].ref`/`inputDigest` moved, the normalized replay stream differs and `ReplayRunner.Normalize`'s workspace scrubbing is the suspect (a plain string `Replace` is defeated by both Windows 8.3 short paths — GitHub runners expose `RUNNER~1`-style temp dirs — and symlinked temp dirs); if a `notes` character count moved, the scored text itself differs and the mock CLI's output is the place to look.

**No speculative fix was applied.** Hardening was written for the 8.3 theory and deliberately discarded: it could not be verified without a Windows machine, and this session already paid for shipping exactly that (the separator normalization, declared a fix, which fixed nothing). Diagnose it first.

## Hard-won lessons worth not relearning

**A diagnostic that misattributes is worse than none.** The "installer silently drops 114 files" bug did not exist. One assertion passed its `changed` list in the `missing` parameter and hardcoded the other counts to zero, and that single mislabeled string cost two wrong fixes, a Windows CI round-trip, and a written-up theory that file *content* decides whether a file gets written. Nobody re-read the `Describe` call, because the message was specific enough to be believed. When evidence forces an implausible mechanism, suspect the instrument before the machine — and check that a diagnostic's arguments are in the order its signature expects.

**Read the tree, not the report.** Three tasks came back reported complete and were not. Every automated check was green each time.

- **G2** covered five reviewers but there were six pipelines.
- **G4** hit its size target by *deleting* three agents' operating procedures rather than moving them. The audit listed which sections moved, which is not the same as verifying nothing was dropped. Assert conservation mechanically — non-whitespace character counts survive reformatting; heading counts don't.
- A new fixture was parked in a subdirectory no test enumerates, so it validated only by hand. Test counts that don't move are evidence.

**Watch for globbing the working directory instead of git.** Three separate defects this session, all the same shape: `__pycache__` (fixed earlier), ten template files committed but never embedded, and `.DS_Store` embedded and shipped. That last one failed *only* on machines where someone had opened the folder in Finder — green in a fresh worktree, red locally.

**Check what a subagent branched from.** Two of three agents were cut from a stale `main` and reported "the spec is wrong" findings that were accurate about their own tree and misattributed. `git merge-base main <branch>` before trusting a baseline number.

**A clean textual merge is not a clean merge.** Two T6 lanes each branched on whether `ProjectTemplate/pack.json` exists; neither could see the other's half. Zero conflicts, four failing tests, both gates red.

**Beware `git add -A`.** It swept 576 files / 32 MB of `graphify-out/` into a commit. That output is now deliberately tracked (owner's call), but the lesson stands: stage explicitly.

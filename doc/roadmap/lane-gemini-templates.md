# Lane GM — Gemini Volume (templates, markdown, high-volume authoring)

**Tool:** Gemini CLI. **Branch:** `lane/gemini-vol` (own worktree).
**File scope (single writer):** `ProjectTemplate/**` markdown (SKILL.md, memory stubs, preamble/reference files), `models.json`, pack content directories once O7 lands. Exception by agreement: G1 touches `AgentTeamService.cs` + one test (tiny, reviewed by CL before merge).
**Out of scope:** all C# beyond G1's one-line membership fix, `automations.json`/`contracts.json` (CL), schemas (CL — GM consumes them).

All GM output merges only after CL review (Gemini's analysis graded D in the reconciliation; speed is the strength here, review is the safety net). English everywhere; `ProjectTemplate/**` stays generic per repo convention.

---

## Task G1: The two cheap defects (T17 + P22) — hours, do first

**Description:** (a) Add `content-writer` to the Content Engine team in `AgentTeamService.cs` and add a test asserting every template agent belongs to ≥1 specialty team. (b) Complete `models.json` to cover all 33 agents with a stated criterion per assignment (documented in the file's `_comment`: e.g. haiku = mechanical/low-ambiguity, sonnet = drafting/tests, opus = judgment/gates), consistent with the existing 12.

**Acceptance criteria:**
- [ ] Team-membership test fails if any agent (current or future) is teamless; passes after the fix
- [ ] `models.json` covers 33/33 with the tier criteria written down; catalog (CX-T T1) reports zero unbound models
- [ ] wshobson caveat honored: no Fable tier for security-analysis-type agents

**Dependencies:** none. **Size:** XS+S.

## Task G2: Reviewer SKILL.md rewrites to typed verdicts (A11) — five agents

**Description:** Rewrite the output sections of `blog-reviewer`, `ui-auditor`, `qa-tester`, `local-media-reviewer`, `evaluator` to emit schema-valid JSON verdicts (CL's frozen v1) instead of prose rubrics: per-category scores, hard veto items, evidence references, `inputDigest`. Keep each agent's existing category rubric content — this is a format conversion plus veto-item hardening, not a re-judging philosophy. One PR per agent so CL can review incrementally and C2 can wire gates as they land.

**Acceptance criteria (per agent):**
- [ ] Worked example verdict in the SKILL.md validates against `verdict.schema.json` via `content_contract.py`
- [ ] Veto items are concrete and machine-checkable (e.g. qa-tester: "any failing E2E scenario ⇒ BLOCK")
- [ ] Debug-instance replay: mock scenario produces a valid verdict end-to-end through C2's gate

**Dependencies:** C1 schema frozen. **Size:** S ×5.

## Task G3: Handoff artifact templates (P9)

**Description:** Once CL freezes the handoff schema (C6), update `preamble.md` and each agent family's SKILL.md guidance to produce/consume handoff artifacts: what to put in `ownedFiles`, `assumptions`, `openLoops`, `nextRole` per pipeline position. Roughly 33 small edits + one shared reference file `ProjectTemplate/Agents/handoff.md`.

**Acceptance criteria:**
- [ ] Shared reference ≤150 lines (P21 discipline from day one); per-agent guidance ≤10 lines each
- [ ] Two-hop debug-instance chain (writer→reviewer) shows populated, schema-valid handoffs
- [ ] No agent instructed to write outside its glob scope in `ownedFiles` guidance

**Dependencies:** C6. **Size:** M (many files, small edits).

## Task G4: Progressive disclosure on the seven largest skills (P21)

**Description:** Identify the seven largest SKILL.md files by size; split each into a compact quick-start SKILL.md (≤150 lines / ~8 KB) plus `references/` overflow files loaded on demand. Content is moved, not rewritten; links must be explicit.

**Acceptance criteria:**
- [ ] All seven under the cap; zero content loss (diff audit: every removed line reappears in a reference file)
- [ ] Debug-instance replay for at least two of the seven shows unchanged behavior on canned scenarios
- [ ] A documented size-cap note added to `ProjectTemplate` authoring guidance so new skills follow it

**Dependencies:** none (sequence after G2 to avoid touching the same reviewer files twice). **Size:** M.

## Task G5: Team-preset agent prose (T5/T6 support)

**Description:** Author SKILL.md + memory stubs for the parallel-review lane roles (performance-reviewer, architecture-reviewer, accessibility-reviewer, coverage-reviewer — security lane ships with the Security pack) and the hypothesis-debug roles (hypothesis-investigator, debug-lead). Verdict-emitting from birth; every agent fully bound per the pack rule (CL adds contracts/automations, GM authors prose + proposes model tier).

**Acceptance criteria:**
- [ ] Each new agent: SKILL.md ≤150 lines, memory stub, worked verdict example, proposed model tier with criterion
- [ ] Investigator prose enforces "one hypothesis, evidence before verdict" discipline; lead prose requires citing evidence when arbitrating
- [ ] Catalog + eval static layer pass for every new agent before merge

**Dependencies:** C1 schema; C8 lands the wiring. **Size:** M.

## Task G6: Pack content authoring (Phase 4 — the volume phase)

**Description:** Under O7 pack structure, author the agent content for each approved pack in priority order (see [packs-and-later.md](packs-and-later.md)): Security Assurance → Incident & Debug → Architecture & Data → Language specialists → Marketing (email, launch, social, SEO/GEO). Port shape and rubrics from wshobson/aaron sources; never copy text wholesale; adapt to board-native lifecycle (columns, verdicts, handoffs, registries). One PR per pack.

**Acceptance criteria (per pack):**
- [ ] Every agent fully bound (contract fragment, model + criterion, team, dispatching automation spec for CL, eval fixture)
- [ ] Reviewer-class agents emit typed verdicts; outbound-capable agents documented as dry-run-by-default (U17)
- [ ] Catalog, eval static, and pack-composition CI all green on the debug instance

**Dependencies:** O7 (C9 + CX-T T6); P7 registries before the marketing pack. **Size:** M–L per pack, embarrassingly parallel across Gemini sessions.

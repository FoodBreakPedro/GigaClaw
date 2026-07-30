## AD-7 Review Protocol (content-pipeline drafts)

Triggered by `content-reviewer-on-review` when a `content-writer` ticket moves
`InProgress → Review`. The draft is the ticket **description** — you never write to it. Your only
output is a **comment**, plus a status/label move.

1. Fetch the ticket. Parse the description the same way `DraftFrontmatter.TryParse` does: an
   opening `---` fence, then flat `key: value` lines (one nested block, `seo:`, one level deep),
   then a closing `---` fence; `title` is required, everything else optional but expected. If it
   fails to parse — missing fence, missing `title` — this is not a reviewable draft: comment
   exactly what's wrong and move the ticket to `Blocked`. Do not guess a verdict on unparseable
   input, and do not attempt to fix the frontmatter yourself.
2. Assess the parsed draft against the same quality bar as the 100-point rubric below: coverage
   and pacing, heading structure, no fabricated or unsourced statistics, no banned phrases from
   `.agents/VOICE.md`, the three `seo.*` fields present and sane (title length, meta-description
   length, a real primary keyword — not the empty string), and `imagePrompt` present per AD-8. You
   read the body straight from the parsed description — the `lint_prose.py`/`cognitive_load.py`/
   `ai_citation_score.py` scripts expect a file path, so they don't apply here; use their scoring
   *categories* as your checklist, not the scripts themselves.
3. Count prior `CONTENT-REVIEW REJECT cycle N/2` markers in the comment trail (own marker prefix —
   this counter never shares state with the legacy protocol's `BLOG-REVIEW REJECT` markers, even
   on the same ticket history). The next cycle number is one greater than the highest found; never
   infer it from memory.
4. Compute `artifact-sha256:<digest>` over the **exact current description text** (same digest
   algorithm as `agent_ticket.py digest`, applied to the description string rather than a file).
   Before acting, check whether a verdict comment already carries this exact digest — if so, a
   prior turn already completed this review; exit without a second comment or move.

**PASS** (meets the quality bar, no fabricated claims, no banned phrases) →
- Post a comment whose first line is the literal token `APPROVE` (e.g. `APPROVE — content-pipeline draft`), summarizing why, including legacy receipt `CONTENT-REVIEW APPROVE v1 artifact-sha256:<digest>`, and typed verdict `GIGACLAW-VERDICT v1 blog-reviewer SHIP artifact-sha256:<digest>` with fenced JSON (`verdict: "SHIP"`, `evidence`: `[{ "kind": "hash", "ref": "sha256:<digest>", "note": "ticket description draft snapshot" }]` — no `path` evidence).
- Resolve (creating if missing, via `POST .../labels`) the `ready-for-cms` label id, then add it with the atomic `PATCH .../tickets/{id}/labels` endpoint — `{"author":"blog-reviewer","addLabelIds":[<id>],"removeLabelIds":[]}`.
- Move the ticket to `Done`.
- Leave `assignedTo` unchanged — it stays `content-writer`; this is a record of who wrote it, not a live assignment.

**FAIL, next cycle <= 2** →
- Post a comment whose first line is the literal token `REJECT`, a specific, actionable fix list, legacy receipt `CONTENT-REVIEW REJECT cycle N/2 artifact-sha256:<digest>`, and typed verdict `GIGACLAW-VERDICT v1 blog-reviewer FIX artifact-sha256:<digest>` with fenced JSON (`verdict: "FIX"`, `evidence`: `[{ "kind": "hash", "ref": "sha256:<digest>", "note": "ticket description draft snapshot" }]` — no `path` evidence).
- Move the ticket back to `InProgress` — **not** `Todo`. `content-writer` stays assigned; the `content-writer-resume` automation redispatches it there to act on your critique.

**FAIL, cycle would exceed 2/2** →
- Post the same `REJECT`-shaped comment (still with the cycle marker, so the count is auditable) and typed verdict `GIGACLAW-VERDICT v1 blog-reviewer BLOCK artifact-sha256:<digest>` with fenced JSON (`verdict: "BLOCK"`), then move the ticket to `Blocked` instead of `InProgress`, stating plainly that the two-revision budget is spent and an owner needs to intervene. Do not start a third writer/reviewer loop.

Use the same status-checked, author-stamped write pattern as everywhere else in this file (verify
the HTTP status of every PATCH/POST; write scratch JSON to a workspace file, never `/tmp`; delete
scratch files before exiting). Every AD-7 turn ends in exactly one of three states you moved it to
yourself — `Done` (approved), `InProgress` (revision requested — this is a deliberate hand-off to
`content-writer`, not an accident), or `Blocked` (unreadable draft or budget exhausted). Never
leave the ticket sitting in `Review` untouched at the end of your turn.

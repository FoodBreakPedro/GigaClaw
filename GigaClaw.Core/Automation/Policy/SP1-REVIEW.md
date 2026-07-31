# SP-1 Policy Enforcement Review Sheet & Inventory

**Date**: 2026-07-30  
**Status**: Signed off, and **enforced as of R3**. Fixtures executed (33/33 agents exercised); 31 agents run in `block`, and `programmer` and `code-janitor` are held in `warn` per the recommendations below. Enforcement lives in `ProjectTemplate/Agents/contracts.json` as `enforcement: "warn" | "block"` — a manifest-wide `defaults` floor with a per-agent override — and is re-read on every dispatch, so a flip is a config edit rather than a redeploy. `TemplateEnforcementStateTests` walks all 33 agents and fails if this document and the manifest disagree.

---

## Update 2026-07-31 — R3 outbound criterion enforced

The last open R3 criterion — host-side `httpRequest` governed by trusted owner approval — is now
enforced. `ActionExecutor.ExecuteHttpRequestAsync` calls `OutboundApprovalGate.Evaluate` on the
rendered URL before any request is built; `AutomationEngine` constructs the gate over
`AppSettingsService.GetApprovedOutboundHosts()`, whose trust anchor is the owner's app-level
`%APPDATA%/GigaClaw/settings.json` (`ApprovedOutboundHosts`) — outside every workspace, so no
agent-mutable label or file can grant approval. The list is re-read from disk on every execution
(hot-reloadable, never cached at engine start), and an `ActionExecutor` constructed without a gate
denies all outbound (fail closed). Without approval the action is a dry run: nothing is sent, the
denial is logged, and a queryable `outbound-denial/v1` ticket-comment receipt names agent (the
automation id, or the preceding run's agent for post-run actions), action, target, host, rule
(`outbound-approval`), and reason — the same "denials produce receipts just like warnings" contract
as the `policy-violation/v1` run events above. A dry run honors `abortOnFailure` so downstream
actions cannot act on a phantom success, but never fires the spec's `FailureComment`/`FailureStatus`
(an unapproved host is configured behavior, not a dispatch failure). Proven by
`ActionExecutorOutboundApprovalTests` (dry-run/send boundary, per-execution hot reload, receipt
shape, and the shipped `cms-dispatch-on-done` automation end-to-end with aligned labels) on top of
the 16 `OutboundApprovalGateTests`.

---

## Executive Summary & Principles

1. **Fail-Closed Policy Framework**: The runtime `ContractPolicy` engine evaluates tool calls against agent risk classes, write globs (`allowedWriteGlobs`), and ticket exit states (`ticketExit`).
2. **Current Enforcement State**: **`warn`** across all agents. Policy violations emit structured `policy-violation/v1` run events without blocking Claude executions.
3. **Zero Fabricated Outcomes**: All 33 template agents have been exercised against synthetic in-glob and out-of-glob tool operations.
4. **Sign-off Rule**: Flipping an agent's policy enforcement from `warn` to `block` requires human review of this document.
5. **What blocking means**: in `block` the PreToolUse hook answers Claude with a `permissionDecision: "deny"` and the tool call never runs; in `warn` the identical violation is recorded as a `policy-violation/v1` run event and the call proceeds, so shadow mode stays a true no-op and this inventory keeps filling for agents not yet flipped. A contract that cannot be parsed enforces regardless of what it asked for.

---

## 1. Special Attention & Questionable Agents

Review these agents carefully before flipping to `block`.

### 1. `programmer`
- **Risk Class**: `code-write`
- **Declared Write Globs**: `["**"]`
- **Ticket Exits**: `["Review", "Todo", "Blocked"]`
- **Fixture Result**: `0` out-of-glob violations observed because wildcard `**` matches any workspace path.
- **Recommendation**: **Do Not Flip / Keep Scoped**. `programmer` requires repository-wide write access for code changes. If narrower file safety is desired in future releases, refine globs prior to flipping enforcement.

### 2. `code-janitor`
- **Risk Class**: `code-write`
- **Declared Write Globs**: `["**"]`
- **Ticket Exits**: `[]`
- **Fixture Result**: `0` out-of-glob violations observed due to `**` wildcard glob.
- **Recommendation**: **Do Not Flip / Keep Scoped**. Nightly refactoring requires repo-wide write access.

### 3. `approval-gatekeeper`
- **Risk Class**: `approval` (board-write capability only)
- **Declared Write Globs**: `[]`
- **Ticket Exits**: `["Review"]`
- **Fixture Result**: `1` violation observed on out-of-glob file write (`design/specs/approval.md`).
- **Recommendation**: **Flip to Block**. `approval-gatekeeper` should only perform board state transitions and must never create or edit local workspace files.

### 4. `content-writer`
- **Risk Class**: `content-write`
- **Declared Write Globs**: `[]`
- **Ticket Exits**: `["Review", "Blocked"]`
- **Fixture Result**: `1` violation observed on file write (`content/posts/draft.md`).
- **Recommendation**: **Flip to Block**. Under AD-7 protocol, `content-writer` edits ticket descriptions directly; file writes are disallowed.

### 5. `groomer`
- **Risk Class**: `board-write`
- **Declared Write Globs**: `[]`
- **Ticket Exits**: `["Backlog", "Todo"]`
- **Fixture Result**: `1` violation observed on file write attempt.
- **Recommendation**: **Flip to Block**. `groomer` only manages board tickets and should not modify local files.

### 6. `producer`
- **Risk Class**: `board-write`
- **Declared Write Globs**: `[]`
- **Ticket Exits**: `["InProgress", "Review", "Todo", "Blocked"]`
- **Fixture Result**: `1` violation observed on file write attempt.
- **Recommendation**: **Flip to Block**. `producer` manages sub-ticket dispatches on the board.

---

## 2. Scoped-Glob Agents (31 Agents)

The following 27 agents have tightly defined write globs that cleanly discriminate between authorized workspace paths and unauthorized file writes.

### 7. `blog-researcher`
- **Risk Class**: `content-write` | **Globs**: `["content/briefs/**"]`
- **Fixture Result**: In-glob write `content/briefs/ai.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 8. `blog-reviewer`
- **Risk Class**: `content-review` | **Globs**: `["content/reviews/**"]`
- **Fixture Result**: In-glob write `content/reviews/rv.md` (Allow); out-of-glob `content/posts/p.md` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 9. `blog-seo`
- **Risk Class**: `content-write` | **Globs**: `["content/posts/**"]`
- **Fixture Result**: In-glob write `content/posts/p.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 10. `blog-translator`
- **Risk Class**: `content-write` | **Globs**: `["content/posts/**"]`
- **Fixture Result**: In-glob write `content/posts/es/p.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 11. `blog-writer`
- **Risk Class**: `content-write` | **Globs**: `["content/posts/**"]`
- **Fixture Result**: In-glob write `content/posts/draft.md` (Allow); out-of-glob `design/spec.md` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 12. `committer`
- **Risk Class**: `git-write` | **Globs**: `[".git/**"]`
- **Fixture Result**: In-glob write `.git/COMMIT_EDITMSG` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 13. `competitive-analyst`
- **Risk Class**: `research` | **Globs**: `["research/**"]`
- **Fixture Result**: In-glob write `research/market.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 14. `content-series-planner`
- **Risk Class**: `content-write` | **Globs**: `["content/series/**"]`
- **Fixture Result**: In-glob write `content/series/plan.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 15. `data-analyst`
- **Risk Class**: `read-only-data` | **Globs**: `["data/reports/**"]`
- **Fixture Result**: In-glob write `data/reports/q3.csv` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 16. `decision-engine`
- **Risk Class**: `approval` / `board-write` | **Globs**: `["doc/decisions/**", "doc/index.md", ".git/**"]`
- **Fixture Result**: In-glob write `doc/decisions/001.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 17. `design-researcher`
- **Risk Class**: `design-write` | **Globs**: `["design/specs/**"]`
- **Fixture Result**: In-glob write `design/specs/tokens.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 18. `documentalist`
- **Risk Class**: `docs-git-write` | **Globs**: `["README.md", "CLAUDE.md", "doc/**", ".agents/documentalist/memory/state.json"]`
- **Fixture Result**: In-glob write `doc/guide.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 19. `email-copywriter`
- **Risk Class**: `content-write` | **Globs**: `["content/emails/**"]`
- **Fixture Result**: In-glob write `content/emails/drip.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 20. `evaluator`
- **Risk Class**: `memory-write` | **Globs**: `[".agents/*/memory/MEMORY.md", ".agents/evaluator/memory/scores.json"]`
- **Fixture Result**: In-glob write `.agents/evaluator/memory/scores.json` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 21. `growth-writer`
- **Risk Class**: `content-write` | **Globs**: `["content/social/**"]`
- **Fixture Result**: In-glob write `content/social/post.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 22. `lead-magnet-creator`
- **Risk Class**: `content-write` | **Globs**: `["content/leadmagnets/**", "content/social/**"]`
- **Fixture Result**: In-glob write `content/leadmagnets/ebook.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 23. `local-image-artist`
- **Risk Class**: `media-direction` / `local-media-execution` | **Globs**: `["media/specs/**", "media/renders/**", "media/receipts/**"]`
- **Fixture Result**: In-glob write `media/renders/hero.png` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 24. `local-media-compositor`
- **Risk Class**: `local-media-composition` | **Globs**: `["media/manifests/**", "media/renders/**", "media/receipts/**"]`
- **Fixture Result**: In-glob write `media/renders/final.mp4` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 25. `local-media-director`
- **Risk Class**: `media-direction` | **Globs**: `["media/specs/**", "media/manifests/**"]`
- **Fixture Result**: In-glob write `media/specs/scene.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 26. `local-media-reviewer`
- **Risk Class**: `local-media-review` | **Globs**: `["media/reviews/**"]`
- **Fixture Result**: In-glob write `media/reviews/rev.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 27. `local-motion-artist`
- **Risk Class**: `local-media-execution` | **Globs**: `["media/specs/**", "media/renders/**", "media/receipts/**"]`
- **Fixture Result**: In-glob write `media/renders/motion.mp4` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 28. `qa-tester`
- **Risk Class**: `test-write` | **Globs**: `["**/*Tests*", "**/tests/**"]`
- **Fixture Result**: In-glob write `GigaClaw.Core.Tests/FooTests.cs` (Allow); out-of-glob `GigaClaw.Core/Bar.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 29. `system-watchdog`
- **Risk Class**: `monitoring` | **Globs**: `[".agents/system-watchdog/memory/probes.json"]`
- **Fixture Result**: In-glob write `.agents/system-watchdog/memory/probes.json` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 30. `trend-researcher`
- **Risk Class**: `research` | **Globs**: `["content/research/**"]`
- **Fixture Result**: In-glob write `content/research/trend.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 31. `ui-auditor`
- **Risk Class**: `design-review` | **Globs**: `["design/audits/**"]`
- **Fixture Result**: In-glob write `design/audits/report.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 32. `ui-designer`
- **Risk Class**: `design-write` | **Globs**: `["design/**"]`
- **Fixture Result**: In-glob write `design/specs/layout.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

### 33. `wellness-coach`
- **Risk Class**: `health-content` | **Globs**: `["content/health/**"]`
- **Fixture Result**: In-glob write `content/health/plan.md` (Allow); out-of-glob `src/app.cs` (Warn, 1 violation).
- **Recommendation**: **Flip to Block**.

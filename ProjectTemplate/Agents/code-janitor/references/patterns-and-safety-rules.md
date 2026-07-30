## Template-owned files: do not file per-project tickets

Everything under `.agents/` that ships from the GigaClaw ProjectTemplate — in
particular `.agents/scripts/*`, the per-agent `SKILL.md` files, `preamble.md`,
and `automations.json` — is **template-owned**: identical copies exist in every
project's workspace, and the canonical source lives in the GigaClaw repo's
`ProjectTemplate/Agents/`. Findings in these files are never project-specific.

- **Do NOT file a ticket in this project** for a finding whose subject is a
  template-owned file. A per-project ticket would be duplicated on every other
  board by the other janitors, and a per-workspace fix would drift from the
  template.
- Instead, file it ONCE on the cross-venture system board: create a ticket on
  the `gigaclaw-system` project (`POST {base}/api/projects/gigaclaw-system/tickets`,
  same API you use for local tickets), title prefixed `[template]`, body noting
  which workspace you observed it in. **Dedup first**: `GET` the system
  project's tickets and skip creation if an open ticket already covers the same
  file/finding (match on the file path + the gist of the title — other
  janitors scan the same template nightly). If the `gigaclaw-system` project
  does not exist or the API call fails, fall back to mentioning the finding in
  your run summary prefixed `[template]`. Template fixes are made in the
  GigaClaw repo's `ProjectTemplate/` and re-synced to workspaces.
- Files created locally in this workspace that do NOT ship from the template
  (venture content, project-specific scripts, code in the project's own
  repositories) are in scope as normal — file tickets for those as usual.

## What you do (by priority)

### 1. Health report (always first)

Maintain `.agents/code-janitor/health.md`. It is **your** file and it ships with your own commit — include it in the pathspec of every janitor commit (step 7), so the report never drifts from the edits it describes:

```markdown
# Code Health
> Last updated: YYYY-MM-DD

## Summary
| Metric | Value | Trend |
|--------|-------|-------|
| Source files analyzed | X | — |
| TODO / HACK count | X | — |
| Build warnings | X | — |
| Files > 300 lines | X | — |
| Cleanliness score | X% | — |

## Risky patterns
| Pattern | Files | Severity |
|---------|-------|----------|
| … | … | … |

## Priority files to visit
```

### 2. Patterns to detect (signal only, do not fix)

**High:**
- Empty catch / swallowed exceptions.
- Blocking calls on async code paths.
- Obvious concurrency hazards (shared mutable state without synchronization).

**Medium:**
- `TODO` / `HACK` / `FIXME` in code.
- Magic strings / literals that should be constants.
- Methods > 50 lines.

**Low:**
- Unused imports / using directives.
- Files > 300 lines (candidates for splitting).
- Unused variables.

### 3. What you CAN fix directly

- Remove imports/usings that the project's compiler or configured linter reports as unused.
- Remove private dead code only when the compiler/static analyzer confirms it is unreachable or
  unused and repository-wide search finds no reflection, serialization, source-generation, markup, or
  configuration reference.
- Fix typos in comments. A typo in a user-visible string is observable behavior: file a ticket unless
  an exact test/spec establishes the intended text.
- Add missing doc comments on public members.

Every direct edit requires the project's prescribed formatter/linter plus build and relevant tests to
pass. “Zero call sites found by grep” alone is not proof that code is dead.

### 4. What you NEVER do

- Change a function/method signature or type name.
- Modify logic, even "obvious" logic.
- Drop database tables, migrations, or persisted schemas.
- Touch this project's `.agents/` folder — the only exceptions are your own
  `.agents/code-janitor/health.md` and `.agents/code-janitor/memory/state.json`.
- Touch any file belonging to another project (anything outside this workspace).

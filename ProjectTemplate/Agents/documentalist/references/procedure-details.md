## Procedure

### 1. Determine which commits to read

Your prompt does **not** hand you a commit range — you derive it yourself. Your working directory
already *is* the workspace, so plain `git` commands are enough (no `-C`, no `cd`). Capture `sourceHead`
once at the start; commits arriving later belong to a later run.

```bash
git rev-parse HEAD
cat .agents/documentalist/memory/state.json 2>/dev/null
```

Cursor schema:

```json
{
  "schemaVersion": 1,
  "lastProcessedSourceCommit": "<full SHA>",
  "processedAt": "2026-04-19T15:00:00Z"
}
```

Determine the range as follows:

1. If the cursor is valid and `lastProcessedSourceCommit` is an ancestor of `sourceHead`, use
   `git rev-list --reverse <cursor>..<sourceHead>`.
2. If the cursor is absent (first upgraded run), find the most recent commit anywhere in the current
   branch authored by `documentalist@gigaclaw.local`. If found, process every commit after it through
   `sourceHead`; otherwise process only `sourceHead` as the explicit bootstrap baseline.
3. If the cursor SHA is missing, malformed, or no longer an ancestor (history rewrite), do **not**
   silently fall back to the last 20 or to HEAD. If a merge base exists, process every commit from that
   merge base through `sourceHead` and note the recovery in the run summary. If no safe base exists,
   stop without advancing the cursor and report the exact problem.

Never use a fixed log window: a missed or delayed automation must not drop older commits. In the
derived range, skip commits authored by `documentalist@gigaclaw.local`,
`code-janitor@gigaclaw.local`, `memory@gigaclaw.local`, or `noreply@anthropic.com`; they are
maintenance outputs already excluded by the trigger, not source changes. Do not skip other commits
merely because they are old.

Then, for each source commit in that range, oldest first:

```bash
git show --stat <sha>
git show <sha> -- "<path>"   # diff for a specific file
```

Read titles, file lists, and diffs. Identify what *behavior* or *structure* changed — not just the file names.

### 2. Match changes to docs

For each affected area, decide whether existing docs cover it:

```bash
# Search the candidate doc files for keywords from the commit
grep -rn "KeywordFromCommit" README.md CLAUDE.md doc/ 2>/dev/null
```

Start by reading `doc/index.md` (if it exists) to find which feature file owns the affected area. Edit only that file's affected sections. Preserve unrelated content, frontmatter, headings, and cross-links. If you change a feature's name or scope, update `doc/index.md` accordingly.

### 3. Update — be concise and factual

- Replace outdated parameters / paths / endpoint names with what the code now says.
- Cite the new value. Do not editorialize.
- If a doc has a "Last updated" frontmatter field, update it to today.

### 4. Create — only when warranted

Open a fresh feature doc only when:
- A commit adds a **distinct new system** (a new project, a new module with public API, a new automation type, a new background service) **and**
- No existing feature file in `doc/` covers it.

Do **not** create new files for:
- Bug fixes, refactors, internal helpers, dependency bumps, formatting.
- Sub-aspects of an already-documented feature — extend the existing feature file instead.

**Where:** always under `doc/<feature>.md` (create the `doc/` folder and `doc/index.md` on first use). Do not invent alternate locations like `docs/`, `doc/specs/`, or scattered top-level markdown files.

**One sanctioned exception:** `doc/decisions/` holds ADR receipts (`ADR-<num>-<title>.md`) and is owned by the `decision-engine` agent. List it in `doc/index.md` as a single line and leave it alone — never flag it as a stray location, never restructure or rewrite its contents.

**Template** for a new feature file — keep it short and factual:

```markdown
# <Feature name>

## Purpose
One paragraph: what this feature does and why it exists.

## Key components
- `path/to/File.cs` — role
- `path/to/Service.cs` — role

## Entry points
- HTTP / CLI / UI action / automation trigger that invokes it.

## External dependencies
- Other features it relies on (link via `./<other-feature>.md`).
- Libraries, databases, external services.
```

**After creating a feature file, update `doc/index.md`** to add a one-line entry pointing at it. If `doc/index.md` does not exist yet, create it with this skeleton:

```markdown
# Architecture documentation

This folder documents how the project is structured, one file per feature.
Each feature page covers its purpose, key components, entry points, and external dependencies.
Concepts are explained in exactly one place — other pages cross-link via relative links.

## Features
- [<Feature name>](./<feature>.md) — one-line summary.
```

### 5. Flag obsolete docs

After updates and creations, do a quick health check — **scoped to the docs that touch the areas changed by the commits you identified in step 1**. A full-corpus sweep of every doc happens at most **once a day**: if `doc/` was already fully swept in the last 24 h (check the dates in your own recent commits with `git log --author=documentalist@gigaclaw.local -n 5 --format='%ad %s'`), stay scoped.

Within that scope:
- For each doc, verify the system or file paths it references still exist.
- If the subject was **entirely removed** from code, prepend a notice instead of deleting:

  ```markdown
  > ⚠️ **OBSOLETE** — the system documented here no longer exists in the code.
  > This file can be removed by the owner.
  ```

- If two docs cover the same subject (overlap), merge content into the more complete one, replace the duplicated explanation with a relative link, and flag the now-empty file as obsolete. **No concept should be explained in two places.**
- Verify every link in `doc/index.md` still resolves; remove entries pointing at obsolete files.

**Never delete a documentation file yourself.** Only flag.

### 6. Commit your changes

If, and only if, you actually modified or created files in this run, finish with a **single commit**
that contains nothing but doc changes. Your working directory already is the workspace — no `cd`, no
`git -C`.

```bash
# 1) List the exact doc files you actually edited this run — and only those.
paths=(README.md doc/index.md doc/automation-engine.md)  # <- replace with exact files

# 2) Keep only paths that exist. A path missing here is a bug in your own list:
#    if your changes need `doc/` or the mandatory `doc/index.md`, CREATE them
#    (see step 4) instead of letting the path silently drop out.
existing=()
for p in "${paths[@]}"; do [ -e "$p" ] && existing+=("$p"); done
if [ ${#existing[@]} -eq 0 ]; then echo "no doc paths to stage"; exit 0; fi

# 3) Stage them and CHECK THE EXIT CODE — a failed `git add` must never be
#    mistaken for "nothing changed".
git add -- "${existing[@]}" || { echo "git add failed - aborting, nothing committed"; exit 1; }

# 4) Only now, decide whether there is anything to commit.
git diff --cached --quiet -- "${existing[@]}" && { echo "docs unchanged, nothing to commit"; exit 0; }

# 5) Commit with a dedicated identity so the documentalist-on-commit trigger
#    can ignore these commits (ignoreAuthors filters by author email), and with
#    an explicit pathspec so nothing another process staged rides along.
git -c user.name="documentalist" \
    -c user.email="documentalist@gigaclaw.local" \
    commit -m "docs: <one-line summary of what changed>

<optional 1-3 line body listing the affected docs>" -- "${existing[@]}"
```

Rules:
- **One commit per run** — squash all doc edits into a single commit. Do not split.
- **Stage explicitly.** Only the exact doc files you touched. Never pass the `doc/` directory itself,
  `-A`, `-a`, or a wildcard: another process may have changed a different doc.
- **Always pass the pathspec** (`-- <paths>`) on the commit. You only ever edit whole doc files, so recording their working-tree content is exactly right — and it keeps anything the orchestrator staged out of your commit.
- **Don't push.** The owner controls when changes leave the local repo.
- **Don't `--amend`** — you are reacting to a commit that may have already been pushed; amending would rewrite history.
- **No `Co-Authored-By` trailer.** The dedicated `documentalist@gigaclaw.local` author is what keeps the trigger from looping on your own commits.

### 7. Advance the durable cursor

Advance `lastProcessedSourceCommit` to the captured `sourceHead` only after:

- the docs commit succeeded and `git show --name-only` confirms it contains only the intended docs; or
- the complete derived range was inspected and required no doc changes.

Write a complete `state.json` to a same-directory temporary file, parse it back, and atomically rename
it into place. Never advance on an incomplete scan, failed edit, failed validation, failed staging, or
failed commit. If a crash happens after the docs commit but before the cursor advances, the next run
will safely inspect the same source range again and should find no additional doc diff.

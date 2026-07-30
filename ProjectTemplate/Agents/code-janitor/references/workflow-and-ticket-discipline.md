## Workflow

```
1. Read `.agents/code-janitor/health.md` and `.agents/code-janitor/memory/state.json` (previous-run context). Capture
   `scanHead = git rev-parse HEAD`, current staged/unstaged paths, and the baseline build result.
   Never edit a path that was already modified or staged when the run began.
2. Derive the catch-up range:
   - valid ancestor cursor → `<lastSuccessfulScanHead>..<scanHead>`;
   - no cursor → bootstrap at `scanHead`;
   - missing/non-ancestor cursor → stop and report; never guess with `git log -n N`.
   Always prioritize files changed anywhere in the complete range, so a skipped nightly run cannot
   lose its work.
3. Update the health report:
   - count source files
   - grep for TODO/HACK/FIXME
   - consult the project's build output for warnings (see preamble Build block)
   Use the same documented exclusions each run (generated, vendored, build-output, and dependency
   directories), and record the commands/exclusions in the report so trends compare like with like.
4. Pick ~20-30 files to analyze (priority: most violations, oldest, files changed since
   your last pass). You run once a day, not every few hours — one pass is the whole
   day's hygiene budget, so cover meaningfully more ground than a quick scan while
   staying inside your turn budget. Fewer, deeper files beats a rushed sweep.
5. For each file:
   a. Read the file.
   b. Analyze: dead code, conventions, TODO, duplication.
   c. Apply safe changes only.
   d. Verify with the project-prescribed formatter/linter, build, and relevant tests. Compare against
      the baseline so pre-existing failures are not attributed to your edit.
6. File Backlog tickets for anything needing judgment (see "Filing a ticket" below).
7. Update `.agents/code-janitor/health.md`.
8. Commit your own edits (see "Committing your own edits" below).
9. Only after the scan, validations, and optional commit all succeed, atomically advance
   `lastSuccessfulScanHead`. If the janitor created a verified commit whose history contains the
   captured `scanHead`, use that janitor commit SHA; otherwise use `scanHead`. Never use an unrelated
   commit that appeared during the run. If no edit was needed, a successful completed scan still
   advances the cursor.
```

### Filing a ticket

**Check for a duplicate first.** You run every day over the same codebase, so the same finding will resurface until someone acts on it — never file it twice:

```bash
curl -s "${GIGACLAW_API_URL}/api/projects/{project-slug}/tickets?status=Backlog"
# Also inspect Todo, InProgress, Blocked, Scheduled, and Review.
```

Derive a stable finding digest from the rule id plus normalized affected paths and include
`[code-janitor:v1 finding-sha256=<digest>]` in the description. If any non-`Done` ticket already has
that digest, **skip creation**. Do not rely on fuzzy title matching. Otherwise write the body to a
workspace file with the `Write` tool and POST it, checking the status:

```bash
# ./janitor-ticket.json -> {"title":"...","description":"...","createdBy":"code-janitor","status":"Backlog","priority":"NiceToHave"}
http=$(curl -s -o ./janitor-resp.json -w "%{http_code}" \
  -X POST ${GIGACLAW_API_URL}/api/projects/{project-slug}/tickets \
  -H "Content-Type: application/json" \
  -d @./janitor-ticket.json)
[[ "$http" =~ ^2 ]] || { echo "POST ticket failed http=$http"; cat ./janitor-resp.json; }
```

After a timeout or non-2xx, re-fetch and reconcile by finding digest before one retry. Never make more
than two create attempts for one finding in a run.

Delete `janitor-ticket.json` and `janitor-resp.json` before exiting, early exits included.

### Committing your own edits

Your hygiene edits are yours to land — leaving them uncommitted in the working tree would pollute the `committer`'s next ticket commit. If (and only if) you actually changed something this run, finish with **one** commit under your own identity:

```bash
# Stage the exact files you edited, plus your health report. Never `-A`, never `-a`.
git add <exact files you edited> .agents/code-janitor/health.md

git -c user.name="code-janitor" \
    -c user.email="code-janitor@gigaclaw.local" \
    commit -m "chore(janitor): <summary>" -- <the same exact paths>
```

- The pathspec is mandatory: another process may have left unrelated changes in the index.
- The `code-janitor@gigaclaw.local` identity is listed in the documentalist's `ignoreAuthors`, so your commit does **not** trigger a documentation run. Never use any other identity, and never add a `Co-Authored-By` trailer.
- The `git add` is what makes a brand-new `health.md` committable (a pathspec commit only sees tracked files).
- Some projects git-ignore `.agents/`. Check with `git check-ignore -q .agents/code-janitor/health.md`; if it is ignored, drop it from both the `git add` and the pathspec — never force-add an ignored path (and note that an ignored path makes the whole `git add` fail, taking your real edits with it). The report still lives on disk for your next run.
- If nothing changed, commit nothing — an empty run is a valid run.
- **Never push.**

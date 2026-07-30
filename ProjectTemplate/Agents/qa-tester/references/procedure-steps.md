## Procedure

### 1. Read the ticket

```bash
curl -s ${GIGACLAW_API_URL}/api/projects/{project-slug}/tickets/{id}
```

Read: description, acceptance criteria, all comments (especially programmer's delivery comment listing modified files).

### 2. Inspect the code

Use the file list from the programmer's delivery comment. Read each file via `Read`. Do not rely on `git diff HEAD~1` (fragile — many tickets may share the last commit, or nothing is committed yet).

### 3. Verify — by actually running the change

Static review is a starting point, not a verdict. You must **execute** the code path the ticket changed and record the observed result. Pick the cheapest level of execution that genuinely exercises the change:

**API-first heuristic** (when using a scenario runner or HTTP-based harness): Do all setup via API calls; reserve browser/UI interactions for the visual assertion only. API calls are instant and deterministic — they never break on a disabled button or a render race. Only reach for UI clicks when the thing you need to verify *is* the UI interaction itself.

| Change type | Minimum execution required |
|---|---|
| Pure function / business rule | Run the project's unit-test suite (and add a test if none covers the change) |
| API endpoint / handler | Hit it for real (`curl`, `Invoke-RestMethod`, the test harness) and observe response, status code, side-effects |
| UI / Blazor / front-end | Drive it via a headless browser (Playwright, Puppeteer) or document why that's impossible |
| CLI / script | Invoke it with realistic args and capture stdout/stderr/exit code |
| Background service / job | Trigger it via its real entry point (timer, message, signal) |

For each acceptance criterion, your report must cite a **concrete observation** ("called `GET /api/.../tickets/42`, got 200 with `{...}`", "ran `dotnet test`, 47 passing"), not a deduction from reading the code.

**If the tooling needed to run the change is missing or broken** (no test runner installed, no fixtures, no way to launch the app, port already taken, missing env var, …):
1. First try to set it up yourself — install the package, write the missing fixture, start the watcher, configure the env var. This is in scope.
2. If you cannot fix it in this run, **block the ticket** (move to `Blocked`, comment with what's missing and what you tried). Do NOT issue a PASS verdict on visual code review alone.

Then check:
- **Build**: trust the project's background build/check tool (see the preamble). Only hard compile errors are failures; transient lock/rebuild warnings are not.
- **Acceptance criteria**: each one tied to an observation from the run above.
- **Edge cases**: null values, empty lists, unauthenticated user, malformed input — exercise them, don't just imagine them.
- **Regressions**: do adjacent features still look intact? Re-run their tests / hit their endpoints, not just read the call sites.
- **Conventions**: the edit follows the codebase's existing patterns — no magic strings, no leftover debug prints, no deviation from nearby file style.

### 3b. Try to break it — the sneaky scenarios

After the nominal path passes, spend the bulk of your effort attacking the change. Do not just *imagine* these cases — actually feed them in and record what happens. The goal is to find the input or sequence that makes the feature misbehave, throw an unhandled error, corrupt state, or silently do the wrong thing.

Run through this attack checklist and pick the ones relevant to the change:

- **Boundary & extreme values**: 0, -1, empty string, a single char, a 10k-char string, max-int, very large lists, dates far in the past/future, unicode/emoji, leading/trailing whitespace.
- **Malformed & hostile input**: wrong types, missing required fields, extra unexpected fields, null where an object is expected, deeply nested JSON, injection-style strings (`'; DROP`, `<script>`, `../../`, `{{7*7}}`).
- **Out-of-order & concurrent actions**: do step 2 before step 1, double-submit the same request, fire the action twice rapidly, cancel mid-operation, act on a resource that was just deleted, trigger the same automation twice.
- **State & lifecycle abuse**: act on an already-completed/archived/closed entity, re-run an idempotent-looking operation and check it stays idempotent, refresh/reload mid-flow, navigate away and back.
- **Auth & ownership**: act as the wrong user, unauthenticated, or on a resource you don't own — confirm it's rejected, not silently allowed.
- **Empty & first-run state**: the very first item, an empty board/list, a project with no members, a freshly initialized workspace.
- **Resource & failure injection**: what if the dependency is slow/absent (port taken, file missing, env var unset)? Does the feature fail loudly and recoverably, or hang/crash/swallow the error?
- **UI-specific traps** (front-end changes): rapid double-clicks, clicking a disabled-looking control, very long text overflowing a container, narrow viewport, keyboard-only navigation, browser back button mid-flow.

For at least the highest-risk handful of these, cite the **concrete observation** in your report (input you sent + what actually happened). A graceful, expected rejection is a PASS-worthy result; an unhandled exception, a 500, a corrupted record, or a silent wrong answer is a **FAIL** — even if the nominal scenario worked perfectly.

If you genuinely cannot find a way to break it after a real attempt, say so explicitly in the report (list the adversarial cases you tried) — that is far stronger evidence than only showing the happy path.

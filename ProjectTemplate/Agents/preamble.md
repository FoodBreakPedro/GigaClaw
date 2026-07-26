## Memory

Your memory lives under `.agents/{agent}/memory/`:

- **`MEMORY.md`** — an **index**, injected automatically into this run. One scored line per topic, each a markdown link to its topic file: `[N] [title](topic-file.md) — hook`. `[N]` is the relevance score; higher = more proven.
- **`<topic>.md`** — the actual lessons, grouped by subject, each with a YAML frontmatter header. These are **not** injected automatically.

So: read the index now, and **before acting, `Read` the topic file under `.agents/{agent}/memory/` for any index entry whose hook looks relevant to your task.** The index is a table of contents — the lessons that save you are in the topic files.

Do not write to memory during a normal run — a separate consolidation pass curates it after you finish.

(Backward compat: if you only see a flat `.agents/{agent}/memory.md` instead of a `memory/` folder, that's the old format injected whole — just apply its lessons; the consolidation pass will migrate it.)

## Language

All content you produce — commit messages, memory updates, agent-to-agent notes — MUST be written in **English**. This includes any text in `.agents/**` and git commit messages.

## Git commits — no attribution trailers

Never add `Co-Authored-By`, `Generated-By`, or any AI-attribution trailer to commit messages. Clean commits only.

## GigaClaw API

The full and up-to-date API documentation is available at:
${GIGACLAW_API_URL}/api/docs

Consult it before interacting with the API.

**Always reference the API as `${GIGACLAW_API_URL}`** in your bash invocations — never hardcode `http://localhost:5230`. The orchestrator injects `GIGACLAW_API_URL` to point at the *current* host instance, which may not be on the default port (e.g. when running inside an isolated test instance spawned by a QA tool).

For convenience, define a local at the start of any block that does several calls:

```bash
api="${GIGACLAW_API_URL}"
curl -s "$api/api/projects/{project-slug}/tickets/{id}"
```

**Always check the HTTP status of write calls** (`POST`, `PATCH`, `PUT`, `DELETE`). `curl -s` swallows errors silently — a 4xx/5xx looks identical to success. Use `-w "\n%{http_code}"` (or `--fail-with-body`) and verify the code is 2xx before relying on the result. If a write fails, do not act as if it succeeded.

## Board discipline — end of turn

If your run is tied to a ticket sitting in `InProgress`, you MUST move it out of `InProgress` before your turn ends — your SKILL says where (typically `Review` when delivered, `Todo` reassigned to `owner` when unclear, `Blocked` when stuck). A ticket left in `InProgress` re-dispatches you every ~30 s at a large turn budget, burning tokens doing nothing. If your SKILL gives no specific rule: `Review` on success, `Blocked` with an explanatory comment otherwise.

Every board write (comment `POST`, status or ticket `PATCH`) requires an `"author"` field set to your agent slug. Never author anything as `"owner"` — owner-authored comments re-dispatch assignees via the `owner-feedback` automation.

(Exception: memory-consolidation passes never touch the board or the API — when this section conflicts with consolidation instructions, the consolidation instructions win.)

## Helper scripts

Invoke bundled Python helpers as `python3 .agents/scripts/<tool>.py <args>`. If `python3` is not on PATH (common on Windows), use `python` or `py -3` instead.

## Cross-platform paths

Never use `/tmp` or other Linux-only filesystem paths — they do not exist on Windows. If you need a scratch file (patch, JSON body, …), write it in the current workspace (e.g. `body.json`, `full.patch`) and delete it once you are done.

## Project slug

Your API calls need the project slug. It is the name of the folder that hosts `.agents/` — your working directory. Use it in every `/api/projects/{project-slug}/...` endpoint.

## Build verification

The host project may run its build tool (dotnet watch, vite, cargo watch, etc.) in the background, keeping build artifacts locked. If you run a build manually and see file-lock errors, these are NOT compile errors — ignore them.

To check whether the code currently compiles:

1. **Trust hot reload**: if your edit applied without an error report from the watcher, it compiled.
2. **Look at the watcher log**: the running watcher's stdout is authoritative. Look for success markers or lines containing real compile errors.
3. **Run the build yourself** and treat file-lock / copy errors as non-blocking noise.

Do NOT kill the running watcher process to work around this — it is usually the live server serving the app.

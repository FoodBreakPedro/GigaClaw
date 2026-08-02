# GigaClaw — Claude guide

A Blazor Server + .NET 10 kanban that orchestrates agentic projects. Each project can have LLM members; a background `AutomationEngine` dispatches them as `claude` CLI subprocesses based on triggers (column changes, intervals, git commits, …).

## Run

```
cd GigaClaw.Web && dotnet watch --non-interactive
# → http://localhost:5230
dotnet test GigaClaw.Core.Tests
```

Keep the `dotnet watch` process running — it serves the UI and the automation engine. If `dotnet build` shows MSB3027 / MSB3021 file-lock errors, they are NOT compile errors; only `error CS####` matters.

### Debug instance (isolated)

To verify changes end-to-end without touching the main instance or spending tokens, use the `gigaclaw-web-debug` launch config (`.claude/launch.json`): port **5232**, isolated data dir `GigaClaw-debug` under the platform app-data root (`%APPDATA%` on Windows, `~/Library/Application Support` on macOS, `~/.config` on Linux — own registry/projects/runs), and the **mock claude CLI** (`GigaClaw.ClaudeMock`, built on start and injected via `GIGACLAW_CLAUDE_BIN`) so agent dispatches replay canned NDJSON scenarios instead of calling the real CLI. The entry runs `tools/run-debug-instance.cs` via `dotnet run` (a .NET 10 file-based app), so it works identically on Windows, macOS, and Linux; it can also be started manually from the repo root:

```
dotnet run tools/run-debug-instance.cs
``` Never verify against the main instance on :5230 — it runs the user's real projects and live agent runs. Note: `gigaclaw-web-devcheck` (:5231) shares the main data dir — its AutomationEngine dispatches REAL agents on real projects; prefer `gigaclaw-web-debug`.

## Repository layout

```
GigaClaw.Core/            Models, services, automation engine, embedded project template
GigaClaw.Core.Tests/      xUnit tests
GigaClaw.Web/             Blazor Server app + REST endpoints (Api/Endpoints.*.cs partials), components, CSS, JS
GigaClaw.QaRunner/        Isolated test-instance launcher (Playwright + scenario runner)
GigaClaw.ClaudeMock/      Mock claude CLI used by QaRunner for hermetic agent dispatch
ProjectTemplate/           Source of truth for new-project initialization. Embedded into
                           GigaClaw.Core.dll and copied into each workspace on Initialize.
  Agents/                    Skills, memory stubs, automations.json, preamble.md (written to <workspace>/.agents/ on Initialize).
  CLAUDE.md                  Workspace guide written to the workspace root.
tools/                     Repo helpers (publish-stable.ps1, …).
```

## Storage

- Project registry: `%APPDATA%/GigaClaw/registry.db` (SQLite).
- Per-project DB: `%APPDATA%/GigaClaw/projects/<slug>.db`.
- Run logs: `%APPDATA%/GigaClaw/runs/<run-id>/`.
- App settings (language, onboardingSeen): `%APPDATA%/GigaClaw/settings.json`.
- Agent memory and session state: `<workspace>/.agents/**`.

## Conventions

- **Inline SQLite migrations**: `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN` in try/catch. No EF Migrations.
- **DTOs** are `record` types.
- **Services** are singletons injected via DI in `GigaClaw.Web/Program.cs`.
- **Blazor components**: `@rendermode InteractiveServer`, `[Parameter]`, `StateHasChanged()`. Prefer direct service calls over HTTP self-calls.
- **CSS** lives in a single `GigaClaw.Web/wwwroot/app.css`. **JS** in `GigaClaw.Web/wwwroot/js/`.
- **English everywhere**: code comments, commit messages, ticket content, `ProjectTemplate/**`.

## Project template embedding

Files under `ProjectTemplate/` are the source of truth for new-project initialization:
- `ProjectTemplate/Agents/preamble.md`, `*/SKILL.md`, `*/memory/MEMORY.md`, `memory-consolidation.md`, `automations.json` are embedded with `LogicalName` `GigaClaw.Core.AgentsTemplate/…` and written to `<workspace>/.agents/` on Initialize. The source folder is `Agents/` (no leading dot) so the repo's `.agents` gitignore doesn't hide template files; only the destination at runtime is `.agents/`. Agent memory uses a per-topic layout: `memory/MEMORY.md` is a scored index (always injected), with one topic file per subject created at runtime (read on demand); the consolidation pass curates it.
- Everything else under `ProjectTemplate/` (e.g. `CLAUDE.md`) is embedded with `LogicalName` `GigaClaw.Core.AgentsTemplateRoot/…` and written to the workspace root.

`AgentsTemplateService` enumerates the embedded resources by these prefixes and copies them out via `InitializeAsync(workspace, overwrite)` (called by the project-creation flow). Keep `ProjectTemplate/**` **generic** (no GigaClaw-specific stack references) since the same files ship to every initialized project.

## Architecture docs

Per-feature architecture documentation lives under [`doc/`](doc/index.md) — start at `doc/index.md` and follow the relative links. Each feature page covers purpose, key components, entry points, and external dependencies. Each concept is explained in exactly one file.

## API

Auto-generated at runtime from the OpenAPI spec. Read it live — do not rely on any committed snapshot:

- `http://localhost:5230/api/docs` (Markdown)
- `http://localhost:5230/openapi/v1.json` (JSON)

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

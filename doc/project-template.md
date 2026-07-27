# Project template

## Purpose
Source of truth for new-project initialization. When a project is created, these files are copied into the workspace so that agents have skills, memories, and an automations config to run with.

## Key components
- `ProjectTemplate/Agents/` — per-agent folders (`SKILL.md`, a `memory/MEMORY.md` index skeleton), shared `preamble.md`, `memory-consolidation.md`, `automations.json`, and `contracts.json`. Embedded with `LogicalName` `GigaClaw.Core.AgentsTemplate/…` and written to `<workspace>/.agents/` on Initialize. Memory topic files are not shipped — the consolidation pass creates them at runtime beside the index.
- `ProjectTemplate/Agents/contracts.json` — machine-readable dispatch, terminal-state, write-scope, and retry expectations. The runner injects only the current agent's contract plus shared defaults; a malformed manifest fails closed.
- `ProjectTemplate/Agents/scripts/` — reusable deterministic helpers and `--self-test` contract checks for ticket handoffs and content artifacts.
- `ProjectTemplate/CLAUDE.md` (and any other root-level files) — embedded with `LogicalName` `GigaClaw.Core.AgentsTemplateRoot/…` and written to the workspace root.
- `GigaClaw.Core/Services/AgentsTemplateService.cs` — enumerates the embedded resources by prefix and copies them out via `InitializeAsync(workspace, overwrite)`.

## Notes
- Source folder is `Agents/` (no leading dot) so the repo's `.agents` gitignore does not hide the template files; only the destination at runtime is `.agents/`.
- Template files must stay **generic** — no GigaClaw-specific stack references — since the same files ship to every initialized project.
- Agent prose and `contracts.json` are complementary: the manifest makes critical limits discoverable at dispatch time, while each skill defines the domain workflow and validation evidence.

## Entry points
- Project creation flow (Home page → Create → Initialize).
- **Re-initialize agent template** action on the in-app Automations page.

## External dependencies
- [Storage](./storage.md) — the workspace path is recorded in the project registry so the engine knows where the `.agents/` folder lives.
- [Automation engine](./automation-engine.md) — consumes `automations.json` from the seeded workspace.

# Workspace guide

This workspace is orchestrated by the **GigaClaw** app (a kanban that dispatches agents against tickets).

## GigaClaw API

The full and up-to-date API documentation is available at:
`${GIGACLAW_API_URL}/api/docs`

(`GIGACLAW_API_URL` is injected into every agent run by the orchestrator — never hardcode a host or port; the default install serves on `http://localhost:5230`, but test instances use other ports.)

Consult it before interacting with the API. All ticket, comment, column, member, automation and run endpoints live there.

## Agents

Automated agents for this project live under `.agents/`:

- `.agents/preamble.md` — shared context injected into every agent run.
- `.agents/{agent}/SKILL.md` — per-agent instructions (editable).
- `.agents/{agent}/memory/` — per-agent persistent memory: `MEMORY.md` is a scored index (always injected), with one topic file per subject (read on demand). Grows over runs; curated by a consolidation pass.
- `.agents/automations.json` — trigger / condition / action pipelines.

The GigaClaw background engine reads `automations.json` and launches `claude` CLI subprocesses in this working directory. Agents interact with the board via the GigaClaw API above.

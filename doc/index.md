# Architecture documentation

This folder documents how GigaClaw is structured, one file per feature.
Each feature page covers its purpose, key components, entry points, and external dependencies.
Concepts are explained in exactly one place — other pages cross-link via relative links.

For a high-level overview see the root [README.md](../README.md). For repo layout and conventions see [CLAUDE.md](../CLAUDE.md). For the ecosystem-comparison improvement roadmap and parallel work lanes see [roadmap/index.md](./roadmap/index.md).

## Features

- [Onboarding](./onboarding.md) — step-by-step guide for first-time users: create a project, move tickets, and use agents.
- [Automation engine](./automation-engine.md) — triggers, conditions, and actions that dispatch agents.
- [Ticket scheduling](./ticket-scheduling.md) — park tickets with a future fire time; `ScheduledPromotionService` auto-promotes them when due.
- [Agent dispatch](./agent-dispatch.md) — running the `claude` CLI as a subprocess and streaming its output.
- [Project template](./project-template.md) — embedded `ProjectTemplate/` files copied into each workspace on Initialize.
- [Verdict contract](./verdict-contract.md) — the typed `SHIP`/`FIX`/`BLOCK` judgement every reviewer, gate and eval judge shares.
- [REST API](./rest-api.md) — OpenAPI-driven endpoints under `/api`, with auto-generated Markdown docs.
- [Storage](./storage.md) — SQLite registry, per-project DBs, run logs, and workspace-side agent state.
- [Kanban UI](./kanban-ui.md) — Blazor Server board, ticket panel, agent run drawer.
- [Dashboard](./dashboard.md) — free-form tile view backed by `.dashboard/` Markdown files with drag-and-drop layout.
- [Graphic charter](./graphic-charter.md) — palette, typography, spacing, form controls, button variants. Reference before adding any new UI.
- [Update check](./update-check.md) — background poll of GitHub Releases that surfaces a dismissible "new version available" banner in the app shell.
- [Telemetry](./telemetry.md) — anonymous daily heartbeat to Umami Cloud (instance id, version, OS); always on outside Development.
- [Per-ticket worktree workflow](./worktree-workflow.md) — opt-in pattern; helper scripts and `{ticketId}` placeholder ship, but agent SKILLs in `ProjectTemplate` do not use it by default.
- [Local models (Ollama)](./local-models.md) — dispatching agents to a local Ollama model via the Anthropic-compat endpoint.
- [Hermes Agent chat](./hermes-agent.md) — embedding a local Hermes API Server channel as a tool-capable chat target.
- [Local media creation](./local-media.md) — governed OpenMontage, ComfyUI, and Phosphene image/video jobs with durable execution and review gates.

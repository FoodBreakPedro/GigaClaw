# Hermes Agent chat

## Purpose

Adds a server-wide **Hermes** target to the existing GigaClaw chat drawer. Hermes runs
through its local API Server communication channel and can answer questions about the
current board, ticket, workspace, and repository using its normal file, terminal, search,
memory, and skill tools.

This is a direct server-to-server integration. The browser never calls Hermes and never
receives its bearer key.

## Configuration

1. In Hermes, enable the **API Server** communication channel and assign an API key.
   The default listener is `http://127.0.0.1:8642`.
2. In GigaClaw, open any project's **Settings** page.
3. Under **Hermes Agent**, enable the connection, enter the base URL and API key, then
   click **Save & test**.
4. Open the chat drawer and select **Hermes**.

The setting is server-wide even though it is edited from a project Settings page. For
headless deployments, these environment variables override the stored URL/key:

| Variable | Purpose |
|---|---|
| `GIGACLAW_HERMES_API_BASE_URL` | Hermes root URL; defaults to `http://127.0.0.1:8642` |
| `GIGACLAW_HERMES_API_KEY` | Hermes bearer key; supplying it also enables the target |

Keep Hermes bound to loopback when it runs on the same host. GigaClaw contacts it from
the ASP.NET server, so browser CORS is not required.

## Key components

- `GigaClaw.Web/Services/HermesAgentService.cs` — authenticated Hermes Runs API client,
  upstream SSE consumer, event mapper, stop/approval control, and health probe.
- `GigaClaw.Web/Api/Endpoints.Chat.cs` — advertises the `_hermes` target, supplies project
  and ticket context, and starts Hermes chat runs.
- `GigaClaw.Web/Api/Endpoints.Runs.cs` — routes stop and approval commands to Hermes while
  retaining the normal GigaClaw SSE endpoint used by the drawer.
- `GigaClaw.Web/Components/ClaudeChatDrawer.razor` — renders Hermes as a target, displays
  tool activity, and surfaces `once`, `session`, `always`, and `deny` approval choices.
- `GigaClaw.Core/Services/AppSettingsService.cs` — stores the server-wide connection
  locally. The key is read only by server-side services and is not returned by an API.

## Run and session flow

1. GigaClaw registers a local `AgentRun` immediately.
2. `HermesAgentService` submits `POST /v1/runs` with the user message, prior user/assistant
   history, a stable session id, and an additional GigaClaw system instruction.
3. The service keeps `GET /v1/runs/{id}/events` connected in the background and maps
   Hermes events into GigaClaw `StreamEvent` values.
4. The normal GigaClaw `/runs/{id}/stream` endpoint fans those events out to the drawer.
   Closing the drawer does not disconnect Hermes; reopening replays the local run buffer.
5. Hermes `approval.request` events become approval cards. The selected choice is sent to
   `POST /v1/runs/{id}/approval`.
6. Stop requests call Hermes's stop endpoint and cancel the local run.

Hermes's API Server Runs API does not currently provide mid-run steering, so GigaClaw
hides the Inject action for Hermes runs. A new message can be sent after the turn finishes.

## Workspace access

Each turn tells Hermes the absolute workspace configured for the current GigaClaw project
and the local REST API URL. For best automatic project-context loading, also configure
Hermes's `terminal.cwd` to the repository it should inspect. Hermes recognizes project
context files such as `.hermes.md`, `AGENTS.md`, and `CLAUDE.md`.

The injected instructions default to read-oriented behavior: Hermes must not modify files,
run destructive commands, or mutate the board unless the owner explicitly asks.

## External dependencies

- A running Hermes Agent gateway with its API Server channel enabled.
- Hermes API Server support for `/v1/capabilities`, `/v1/runs`, run event SSE, stop, and
  approval endpoints.
- [REST API](./rest-api.md) for board inspection and explicitly requested mutations.
- [Storage](./storage.md) for persisted GigaClaw chat messages and run snapshots.

# Getting started with KittyClaw

This guide picks up after installation and walks you through your first project end to end.

## First launch

Open the app at **http://localhost:5230**. If `claude` or `git` are missing from your PATH, a popup will warn you. You can close it and continue — the board works, but agent dispatch and auto-commits will fail until those tools are installed.

## Create your first project

1. On the home page, type a project name and click **Create**.
2. In the popup that appears, enter an absolute path for the workspace folder (an existing repo or a new folder).
3. Click **Initialize**. KittyClaw will:
   - Create the project database.
   - Copy the agent template into `<workspace>/.agents/` (skills, memory, automations) and `CLAUDE.md` into the workspace root.
   - Run `git init` if the folder is not already a repo.
   - Add one board member per built-in agent role.

The workspace folder is never deleted by KittyClaw; deleting a project only removes its database entry.

## The kanban board

Your board has seven default columns: **Backlog → Todo → InProgress → Blocked → Scheduled → Review → Done**.

The **Scheduled** column holds tickets that have a future dispatch time set. The automation engine moves them to their target column automatically when that time arrives.

- **Create a ticket** with the `+` button on any column header. Give it a title, description, and optionally a priority and assignee.
- **Move tickets** by dragging them between columns, or by opening the ticket panel and changing the status there.
- **Assign a ticket** to an agent member to let the automation engine dispatch it automatically.

## Agents and automation

KittyClaw ships with a set of pre-configured agent roles: `programmer`, `groomer`, `producer`, `qa-tester`, `committer`, `code-janitor`, `evaluator`, and `documentalist`.

To have an agent work a ticket:
1. Assign the ticket to an agent (e.g. `programmer`) and move it to **Todo**.
2. The automation engine picks it up within seconds, moves it to **InProgress**, and launches a `claude` subprocess.
3. A drawer slides in with the live output stream. Use **Steer** to send a mid-run instruction or **Stop** to abort.
4. The agent moves the ticket to **Review** when done (or to **Blocked** / back to **Todo** if it hits a problem).

You review the work in **Review**, then drag the ticket to **Done** (or back to **Todo** with a comment for a fix cycle).

## Automations page

Each project has an **Automations** tab that lists all trigger/condition/action rules loaded from `<workspace>/.agents/automations.json`. You can:
- Enable or disable individual rules without editing the file.
- Edit triggers, conditions, and actions in the UI.
- Click **Reload from disk** to pull in changes you made directly to `automations.json`.
- Click **Re-initialize template** to reset the agent files to the project template defaults.

## Dashboard

The **Dashboard** tab gives you a freeform tile canvas. Click the chat bubble to describe a tile in plain language; the agent picks a template, writes `output.json`, and adds it to the board. Tiles auto-refresh on a schedule you configure in `tile.yaml`.

## Sending an ad-hoc instruction

Open any ticket and click **New instruction** to open a chat drawer. Type a prompt — it is dispatched as a one-shot run to the ticket's current assignee.

## Useful shortcuts

| Action | Where |
|---|---|
| Search tickets | Search bar — supports `#42`, `@owner`, `priority:critical`, `label:bug`, `>2024-01-01` |
| Create a sub-ticket | Ticket panel → **Add sub-ticket** |
| Upload an image | Paste or drag into any description or comment field |
| Manage columns | Board header → column menu |
| Manage labels and members | Project settings sidebar |

## Next steps

- Read the [automation engine](./automation-engine.md) doc to understand triggers, conditions, and actions.
- See [agent dispatch](./agent-dispatch.md) for how `claude` runs are launched and streamed.
- Check [local models](./local-models.md) if you want to route agent calls to a local Ollama instance instead of the Anthropic cloud.

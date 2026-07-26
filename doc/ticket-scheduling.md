# Ticket scheduling

## Purpose
Allows a ticket to be parked with a future fire time and a target column, then auto-promoted when that time arrives. Replaces the workaround of using "Blocked" for calendar-dated items (future posts, dated gates, time-gated reviews). Promoted tickets fire a `TicketStatusChanged` event so existing automations react as if the ticket were moved manually.

## Key components
- `GigaClaw.Core/Models/Ticket.cs` — `FireAt` (`DateTime?`) and `ScheduleTarget` (`string?`) fields. Moving a ticket out of the Scheduled column clears both fields so no stale countdown survives an accidental column move.
- `GigaClaw.Core/Services/TicketService.cs` — `ScheduleTicketAsync`, `ListDueScheduledTicketIdsAsync`, `PromoteScheduledAsync`; inline SQLite migrations adding the two columns.
- `GigaClaw.Core/Services/ColumnService.cs` — seeds the "Scheduled" column between "Blocked" and "Review" on new boards, and idempotently back-fills it on existing boards.
- `GigaClaw.Core/Services/ScheduledPromotionService.cs` — hosted `BackgroundService` polling every 30s. Calls `ListDueScheduledTicketIdsAsync`, then `PromoteScheduledAsync` for each due ticket, and fires `TicketStatusChanged` (via `ProjectRuntimeManager`) so any matching [automation engine](./automation-engine.md) rules react to the promotion as a regular status change.

## Entry points
- `PATCH /api/projects/{slug}/tickets/{id}/schedule` — sets or clears `FireAt` + `ScheduleTarget` (body: `{ "fireAt": "2026-08-01T09:00:00Z", "target": "Review" }`; absent or null fields clear the schedule).
- Ticket panel UI (see [Kanban UI](./kanban-ui.md)) — shows the scheduled date/time in local time and the target column; editable via a datetime-local input and a column select. Non-scheduled tickets show a **Schedule…** button when the Scheduled column exists on the board.
- `ScheduledPromotionService` — background polling; fires automatically, no UI or HTTP trigger.

## External dependencies
- [Kanban UI](./kanban-ui.md) — board cards in the Scheduled column show a `J-N` countdown badge and are sorted soonest-first; ticket panel renders the schedule editor.
- [Storage](./storage.md) — `FireAt` and `ScheduleTarget` persisted in the per-project SQLite DB.
- [Automation engine](./automation-engine.md) — `TicketStatusChanged` fired on promotion lets the engine react with the same rules used for manual column moves.

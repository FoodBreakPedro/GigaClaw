# Per-project drift check

## Purpose

`dotnet run --project GigaClaw.Catalog -- check --project <workspacePath>` guards against
**unintended drift** between an initialized workspace's `.agents/**` (and workspace-root template
files) and the current template `AgentsTemplateService.InitializeAsync` would write today. It is a
safeguard for risk **AD-4** in the GigaClaw plan: a project or venture initialized weeks or months
ago gradually diverging from the template without consensus or tracking.

This subcommand retired `tools/check-automation-drift.sh` (2026-08-01). The catalog's own
`check --strict` / `--strict-packs` gates validate the **template itself** (`ProjectTemplate/Agents/**`
against `catalog.json`/`doc/catalog.md`); they say nothing about any already-initialized workspace.
This subcommand is the other half: it compares a workspace against the template, never the other
way around, and it never modifies anything it checks — re-running
`AgentsTemplateService.InitializeAsync(workspace, overwrite: true)` (surfaced today via the
project-initialize flow) is the only thing that refreshes a workspace.

## What It Checks

For one workspace, the checker compares every file `AgentsTemplateService` enumerates — every
`.agents/**` template path plus the workspace-root files (`CLAUDE.md`, `.gitignore`,
`.dashboard/**`) — against the current embedded template, and reports:

1. **MISSING** — a path (or, within `automations.json`, an automation `id`) the template ships that
   the workspace does not have.
2. **MODIFIED** — a path present on both sides with different content. For the four pack *merge
   artifacts* (`automations.json`, `contracts.json`, `models.json`, `teams.json`) the comparison is
   semantic (per automation id / per top-level key / per team), matching how
   `PackInstaller` re-serializes those files rather than copying them verbatim — a byte comparison
   would falsely flag a freshly initialized, untouched workspace. `automations.json` additionally
   names *which* fields on the drifted automation changed (e.g. `enabled`, `actions`), which the
   retired script never reported — it only ever said an id was `CHANGED`.
3. **EXTRA** — reported **only for `automations.json`** (the one file the retired script actually
   compared), for an automation `id` present in the workspace but absent from the template. This is
   deliberate: `.agents/` legitimately accumulates files the template never shipped (per-topic
   memory notes, `packs.lock.json`, the allowlist file itself), so generalizing "extra" to every
   file would just be noise the script never produced either.

Every file drift line also names the **section** it belongs to (an agent slug, "shared scripts",
"shared template", "workspace root", or a dashboard tile), and the report header states which
**template version** (the `core` pack's `pack.json` version) it compared against.

## Allowlist: automation-overrides.json

Exactly as before: an array of automation `id` strings in `<workspace>/.agents/automation-overrides.json`
exempts those ids from being counted as drift (missing, extra, or changed), reported separately as
`ALLOWLISTED`.

```json
[
  "approval-clear-on-revision",
  "custom-automation-x",
  "trend-brief-weekly"
]
```

Use it for per-venture customizations, experimental automations, or temporary, ticketed
workarounds — same guidance as before.

## Usage

```bash
dotnet run --project GigaClaw.Catalog -- check --project <workspacePath>
dotnet run --project GigaClaw.Catalog -- check --projects <root>
```

`--project` checks exactly one workspace. `--projects` checks every immediate subdirectory of
`<root>` that has a `.agents/` folder — the multi-venture equivalent of passing several directories
to the retired script.

### Example

```bash
cd /path/to/GigaClaw
dotnet run --project GigaClaw.Catalog -- check --project .
```

```bash
dotnet run --project GigaClaw.Catalog -- check --projects ~/ventures
```

## Output Format

```
=== <workspace name> ===
template: core v1.0.0
ALLOWLISTED (intentional overrides):
  - <id> (ok)
MISSING .agents/<path>: <detail>
MODIFIED .agents/<path>: <detail>
EXTRA .agents/automations.json: automation '<id>' present in workspace, not in template

DRIFT: missing=<n> modified=<n> extra=<n> allowlisted=<n>
```

`No drift.` is printed in place of the per-file lines when a workspace has none. The trailing
`DRIFT:` line sums every checked workspace and is safe to grep in CI or a hook.

## Exit Codes

- **0** — no unallowlisted drift in any checked workspace.
- **1** — drift detected in at least one workspace, or a workspace/`--projects` root could not be
  found.

```bash
dotnet run --project GigaClaw.Catalog -- check --project ./ventures/x || {
    echo "Template drift detected. Review the report and either:"
    echo "  1. Re-run Initialize (overwrite) to pick up the template change, or"
    echo "  2. Add an allowlist entry to .agents/automation-overrides.json with justification in a ticket."
    exit 1
}
```

## When to Run

1. **After a template change**: whenever `ProjectTemplate/**` is updated (a new agent onboarded, an
   automation refined, a workspace-root file edited), run this against every active venture to see
   what needs to be merged or allowlisted.
2. **Optional in CI / a pre-push hook**: the same way the retired script was wired in.
3. **Audit and maintenance**: run periodically to catch stale allowlist entries or untracked
   customizations.

## Related

- `GigaClaw.Catalog/WorkspaceDriftChecker.cs` — the implementation.
- `GigaClaw.Core.Tests/Services/WorkspaceDriftCheckerTests.cs` — the parity tests (fresh workspace =
  zero drift; missing/modified/extra/allowlisted each covered).
- `doc/catalog.md` / `dotnet run --project GigaClaw.Catalog -- check --strict` — the template-side
  gate this subcommand complements, not replaces.
- **GigaClaw plan**: Task 12 (automation drift safeguard) and risk AD-4 (divergence of automation
  definitions).

# Automation Drift Check

## Purpose

The `check-automation-drift.sh` script guards against **unintended drift** between per-venture copies of `automations.json` and the authoritative template at `ProjectTemplate/Agents/automations.json`. It is a safeguard for risk **AD-4** in the GigaClaw plan: automation definitions gradually diverging across workspaces without consensus or tracking.

## What It Checks

The script compares each project's `.agents/automations.json` against the template and reports three categories of drift:

1. **MISSING**: Automations present in the template but absent from the project — suggests the project has an outdated or incomplete automation setup.
2. **EXTRA**: Automations present in the project but not in the template — suggests the project has added custom automations that are not shared or approved.
3. **CHANGED**: Automations with the same `id` in both files but different content (triggers, conditions, actions, etc.) — suggests the project has locally modified a template automation.

The script matches automations by their **`id` field** (e.g., `"assignee-dispatch"`, `"blog-reviewer-on-review"`), which is the stable, unique identifier for each automation across versions.

## Usage

```bash
./tools/check-automation-drift.sh <project-agents-dir> [more dirs...]
```

Each `<project-agents-dir>` should contain an `automations.json` file and optionally an `automation-overrides.json` file (see **Allowlist** section below).

### Example

Check the repo-root `.agents` directory against the template:

```bash
cd /path/to/GigaClaw
./tools/check-automation-drift.sh ./.agents
```

Check multiple project directories:

```bash
./tools/check-automation-drift.sh ~/ventures/venture-a/.agents ~/ventures/venture-b/.agents
```

## Allowlist: automation-overrides.json

To intentionally allow specific automations to differ or be absent in a particular project, create an `automation-overrides.json` file in that project's `.agents/` directory:

```json
[
  "approval-clear-on-revision",
  "custom-automation-x",
  "trend-brief-weekly"
]
```

The allowlist is an array of automation `id` strings. Any automation whose `id` appears in this array will be reported as "allowlisted (ok)" instead of contributing to the drift count. This signals that the difference is **intentional and tracked**.

### When to Use the Allowlist

- **Per-venture customizations**: If a project legitimately needs a different automation setup (e.g., a venture does not use the blog pipeline, so `blog-reviewer-on-review` is not needed).
- **Experimental features**: If a project is testing a custom automation that should not yet ship to all ventures.
- **Temporary workarounds**: Document in a ticket that the allowlist is in place and why, with a target removal date.

## Output Format

The script prints a human-readable report for each project, followed by a **machine-parsable summary line**:

```
DRIFT: missing=<n> extra=<n> changed=<n> allowlisted=<n>
```

Where:
- `missing` — count of unallowlisted automations in the template but not in the project
- `extra` — count of unallowlisted automations in the project but not in the template
- `changed` — count of unallowlisted automations with different content
- `allowlisted` — count of intentional overrides (from `automation-overrides.json`)

## Exit Codes

- **0** — No unallowlisted drift detected; all automations are in sync with the template (or intentionally allowlisted).
- **1** — Drift detected; one or more automations differ from the template without an allowlist entry.

Use the exit code in CI pipelines or hooks to enforce synchronization:

```bash
./tools/check-automation-drift.sh ./.agents || {
    echo "Automation drift detected. Review the report and either:"
    echo "  1. Merge template changes into the project's automations.json, or"
    echo "  2. Add an allowlist entry in .agents/automation-overrides.json with justification in a ticket."
    exit 1
}
```

## When to Run

1. **After an owner stream change to automations**: Whenever `ProjectTemplate/Agents/automations.json` is updated (e.g., a new agent is onboarded or an automation is refined), run this check on all active venture `.agents/` directories to identify what needs to be merged or allowlisted.

2. **Optional in CI**: Add this check to a pre-commit hook or CI pipeline to catch drift before it compounds:
   - In `.claude/settings.json` hook: `"onBeforePush": "tools/check-automation-drift.sh ./.agents"` (or configure a similar hook to scan all ventures).

3. **Audit and maintenance**: Run periodically (e.g., monthly) to identify stale allowlist entries or untracked customizations.

## Implementation Details

- **Matching key**: The script uses the `id` field to match automations between the project and template. This field is stable and unique.
- **Content comparison**: Automations are compared as JSON objects after normalizing key order (`jq -S`) so that superficial differences (whitespace, key order) do not trigger false positives.
- **Allowlist precedence**: If an automation's `id` is in the allowlist, it is not reported as drift, regardless of whether it is missing, extra, or changed.
- **Script location**: The script locates the template by resolving `ProjectTemplate/Agents/automations.json` relative to its own directory, so it works from any working directory.

## Common Scenarios

### Scenario: Venture X needs to skip blog approvals

The template includes `blog-reviewer-on-review` because the main brand publishes blog content. Venture X does not. Instead of manually removing it, add to `ventures/x/.agents/automation-overrides.json`:

```json
[
  "blog-reviewer-on-review",
  "blog-translation-on-seo-review"
]
```

Then run the check:

```bash
./tools/check-automation-drift.sh ./ventures/x/.agents
```

Output:

```
ALLOWLISTED (intentional overrides):
  - blog-reviewer-on-review (ok)
  - blog-translation-on-seo-review (ok)

✓ No unallowlisted drift
DRIFT: missing=2 extra=0 changed=0 allowlisted=2
```

### Scenario: A change to the template is ready to roll out

The template's `assignee-dispatch` automation is updated with a new field. The check detects this:

```
CHANGED (in both but with different content):
  - assignee-dispatch: Dispatch: Todo ticket -> InProgress + run assigned agent
```

The venture owner can now:
1. Update their `.agents/automations.json` to match the template (accepting the change), or
2. Add `assignee-dispatch` to their allowlist if they intentionally want to keep the old version.

## Related

- **GigaClaw plan**: Task 12 (automation drift safeguard) and risk AD-4 (divergence of automation definitions).
- **ProjectTemplate/Agents/automations.json**: The authoritative source of all automations.
- **`<venture>/.agents/automations.json`**: Per-project copy that should stay synchronized with the template unless allowlisted.

#!/usr/bin/env bash

# ============================================================================
# backfill-archive.sh - Task 16 backfill: archive tickets dispatched to the
# CMS before draft archival landed.
# ============================================================================
# Usage: backfill-archive.sh <project-slug> [--base-url http://localhost:5230] [--dry-run]
#
# Lists every ticket in <project-slug> carrying the `dispatched` label and runs the exact
# same archive-draft.ps1 script the `cms-dispatch-on-done` automation uses for new dispatches,
# so drafts that shipped before Task 16 landed get archived too.
#
# Idempotent: the target path is derived from the project slug + ticket's frontmatter slug
# (or ticket id) + the month, so re-running overwrites the same file rather than duplicating it.
#
# Requires: jq, curl, and pwsh (PowerShell 7+; `powershell` also accepted on Windows) on PATH,
# plus GIGACLAW_ARCHIVE_ROOT set in this shell's environment — the same variable
# archive-draft.ps1 reads when invoked from the automation. GIGACLAW_API_URL defaults to
# http://localhost:5230 like every other GigaClaw script/skill; --base-url overrides it.
#
# See doc/automation-engine.md for the archival design (Task 16 / AD-7).
# ============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARCHIVE_SCRIPT="${SCRIPT_DIR}/../ProjectTemplate/Agents/scripts/archive-draft.ps1"

BASE_URL="${GIGACLAW_API_URL:-http://localhost:5230}"
DRY_RUN=false

if [[ $# -lt 1 ]]; then
    echo "Usage: $(basename "$0") <project-slug> [--base-url <url>] [--dry-run]"
    echo ""
    echo "Archives every ticket in <project-slug> carrying the 'dispatched' label."
    echo "Requires GIGACLAW_ARCHIVE_ROOT to be set in the environment."
    exit 1
fi

SLUG="$1"
shift || true

while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url)
            BASE_URL="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        *)
            echo "Error: Unknown argument '$1'"
            exit 1
            ;;
    esac
done

# ============================================================================
# Preflight
# ============================================================================
for bin in jq curl; do
    if ! command -v "$bin" &> /dev/null; then
        echo "Error: '$bin' is required but not found on PATH"
        exit 1
    fi
done

PWSH_BIN=""
for candidate in pwsh powershell; do
    if command -v "$candidate" &> /dev/null; then
        PWSH_BIN="$candidate"
        break
    fi
done
if [[ -z "$PWSH_BIN" ]]; then
    echo "Error: pwsh (PowerShell 7+) is required but not found on PATH"
    exit 1
fi

if [[ ! -f "$ARCHIVE_SCRIPT" ]]; then
    echo "Error: archive-draft.ps1 not found at $ARCHIVE_SCRIPT"
    exit 1
fi

if [[ -z "${GIGACLAW_ARCHIVE_ROOT:-}" ]]; then
    echo "Error: GIGACLAW_ARCHIVE_ROOT is not set."
    echo "Set it to the Obsidian vault (or any folder) archive-draft.ps1 should write into, e.g.:"
    echo "  GIGACLAW_ARCHIVE_ROOT=/path/to/vault $(basename "$0") $SLUG"
    exit 1
fi

# archive-draft.ps1 reads GIGACLAW_API_URL itself (same variable the live automation relies on)
# and defaults to localhost:5230 when unset — export our resolved BASE_URL so --base-url and
# a pre-set GIGACLAW_API_URL both actually reach the script instead of it silently reverting
# to the default port.
export GIGACLAW_API_URL="$BASE_URL"

echo "Backfilling archive for project '${SLUG}' from ${BASE_URL} into ${GIGACLAW_ARCHIVE_ROOT}..."
[[ "$DRY_RUN" == true ]] && echo "(dry run — no files will be written)"
echo ""

# ============================================================================
# Find dispatched tickets
# ============================================================================
if ! tickets_json=$(curl -sf "${BASE_URL}/api/projects/${SLUG}/tickets"); then
    echo "Error: failed to list tickets for project '${SLUG}' at ${BASE_URL}"
    echo "Ensure GigaClaw is running and the project slug is correct."
    exit 1
fi

ticket_ids=$(echo "$tickets_json" | jq -r '.[] | select(any(.labels[]?; .name == "dispatched")) | .id')

if [[ -z "$ticket_ids" ]]; then
    echo "No tickets carrying the 'dispatched' label found in '${SLUG}'. Nothing to backfill."
    exit 0
fi

# ============================================================================
# Archive each one, reusing the exact script the live automation runs
# ============================================================================
count=0
failed=0

while IFS= read -r ticket_id; do
    [[ -z "$ticket_id" ]] && continue
    count=$((count + 1))

    # Best-effort: recover the CMS admin URL from the existing dispatch receipt comment
    # (posted by cms-dispatch-on-done's addComment: "Dispatched to CMS: <url> (id ... slug ...)")
    # so the backfilled archive carries the same provenance a fresh archival would.
    admin_url=$(curl -sf "${BASE_URL}/api/projects/${SLUG}/tickets/${ticket_id}" \
        | jq -r '[.comments[]?.content | select(test("Dispatched to CMS: "))][0] // ""' \
        | sed -n 's/.*Dispatched to CMS: \([^ ]*\).*/\1/p' || true)

    echo "  Ticket #${ticket_id}${admin_url:+ (adminUrl: ${admin_url})}"

    if [[ "$DRY_RUN" == true ]]; then
        continue
    fi

    if ! "$PWSH_BIN" -NonInteractive -NoProfile -File "$ARCHIVE_SCRIPT" \
        -TicketId "$ticket_id" -ProjectSlug "$SLUG" -AdminUrl "$admin_url"; then
        echo "    Warning: archive-draft.ps1 process exited non-zero for ticket #${ticket_id}"
        echo "    (the script itself always exits 0 on its own best-effort failures — a"
        echo "     non-zero here means pwsh/the process could not run at all; see stderr above)"
        failed=$((failed + 1))
    fi
done <<< "$ticket_ids"

echo ""
if [[ "$DRY_RUN" == true ]]; then
    echo "Dry run: would have archived ${count} ticket(s)."
else
    echo "Backfill complete: processed ${count} ticket(s), ${failed} process-level failure(s)."
fi

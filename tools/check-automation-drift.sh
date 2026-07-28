#!/bin/bash
#
# check-automation-drift.sh - Detect drift between project automations.json and the template.
#
# Usage: check-automation-drift.sh <project-agents-dir> [more dirs...]
#
# Compares each <project-agents-dir>/automations.json against ProjectTemplate/Agents/automations.json
# (located relative to this script), reporting missing, extra, and changed automations.
#
# Exit codes:
#   0 - No unallowlisted drift found
#   1 - Drift detected (missing, extra, or changed automations not in allowlist)
#

set -e

# Helper function to print to stderr
log_error() {
    echo "$@" >&2
}

# Helper function to print indented output
indent() {
    sed 's/^/  /'
}

# Get the directory where this script lives
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
TEMPLATE_FILE="$REPO_ROOT/ProjectTemplate/Agents/automations.json"

# Check that jq is available
if ! command -v jq &> /dev/null; then
    log_error "ERROR: jq is required but not installed."
    exit 1
fi

# Check that the template file exists
if [[ ! -f "$TEMPLATE_FILE" ]]; then
    log_error "ERROR: Template file not found at $TEMPLATE_FILE"
    exit 1
fi

# If no project directories provided, print usage
if [[ $# -eq 0 ]]; then
    log_error "Usage: $0 <project-agents-dir> [more dirs...]"
    log_error ""
    log_error "Each directory should contain an automations.json file."
    exit 1
fi

# Parse the template once
TEMPLATE_AUTOMATIONS=$(jq -S '.automations // []' "$TEMPLATE_FILE")
TEMPLATE_IDS=$(echo "$TEMPLATE_AUTOMATIONS" | jq -r '.[].id' | sort)

# Counters for overall report
TOTAL_MISSING=0
TOTAL_EXTRA=0
TOTAL_CHANGED=0
TOTAL_ALLOWLISTED=0
ANY_DRIFT=false

# Process each project directory
for PROJECT_DIR in "$@"; do
    PROJECT_FILE="$PROJECT_DIR/automations.json"
    OVERRIDES_FILE="$PROJECT_DIR/automation-overrides.json"

    # Extract project name for reporting
    PROJECT_NAME=$(basename "$PROJECT_DIR")

    # Check that the project file exists
    if [[ ! -f "$PROJECT_FILE" ]]; then
        log_error "ERROR: Project file not found at $PROJECT_FILE"
        exit 1
    fi

    echo "=== $PROJECT_NAME ==="

    # Parse the project automations
    PROJECT_AUTOMATIONS=$(jq -S '.automations // []' "$PROJECT_FILE")
    PROJECT_IDS=$(echo "$PROJECT_AUTOMATIONS" | jq -r '.[].id' | sort)

    # Parse the overrides (allowlist) if it exists
    ALLOWED_IDS=()
    if [[ -f "$OVERRIDES_FILE" ]]; then
        ALLOWED_IDS=($(jq -r '.[] // empty' "$OVERRIDES_FILE" 2>/dev/null | sort -u))
    fi

    # Find missing automations (in template but not in project)
    MISSING_IDS=()
    for id in $TEMPLATE_IDS; do
        if ! echo "$PROJECT_IDS" | grep -q "^${id}$"; then
            MISSING_IDS+=("$id")
        fi
    done

    # Find extra automations (in project but not in template)
    EXTRA_IDS=()
    for id in $PROJECT_IDS; do
        if ! echo "$TEMPLATE_IDS" | grep -q "^${id}$"; then
            EXTRA_IDS+=("$id")
        fi
    done

    # Find changed automations (in both but with different content)
    CHANGED_IDS=()
    for id in $PROJECT_IDS; do
        if echo "$TEMPLATE_IDS" | grep -q "^${id}$"; then
            # Both have this ID, check if content differs
            TEMPLATE_OBJ=$(echo "$TEMPLATE_AUTOMATIONS" | jq -S "map(select(.id == \"$id\")) | .[0]")
            PROJECT_OBJ=$(echo "$PROJECT_AUTOMATIONS" | jq -S "map(select(.id == \"$id\")) | .[0]")

            if [[ "$TEMPLATE_OBJ" != "$PROJECT_OBJ" ]]; then
                CHANGED_IDS+=("$id")
            fi
        fi
    done

    # Categorize findings
    UNALLOWLISTED_MISSING=()
    UNALLOWLISTED_EXTRA=()
    UNALLOWLISTED_CHANGED=()
    ALLOWLISTED_COUNT=0

    for id in "${MISSING_IDS[@]}"; do
        if [[ " ${ALLOWED_IDS[@]} " =~ " ${id} " ]]; then
            ((ALLOWLISTED_COUNT++))
        else
            UNALLOWLISTED_MISSING+=("$id")
        fi
    done

    for id in "${EXTRA_IDS[@]}"; do
        if [[ " ${ALLOWED_IDS[@]} " =~ " ${id} " ]]; then
            ((ALLOWLISTED_COUNT++))
        else
            UNALLOWLISTED_EXTRA+=("$id")
        fi
    done

    for id in "${CHANGED_IDS[@]}"; do
        if [[ " ${ALLOWED_IDS[@]} " =~ " ${id} " ]]; then
            ((ALLOWLISTED_COUNT++))
        else
            UNALLOWLISTED_CHANGED+=("$id")
        fi
    done

    # Report missing automations
    if [[ ${#UNALLOWLISTED_MISSING[@]} -gt 0 ]]; then
        echo "MISSING (in template but not in project):"
        for id in "${UNALLOWLISTED_MISSING[@]}"; do
            NAME=$(echo "$TEMPLATE_AUTOMATIONS" | jq -r "map(select(.id == \"$id\")) | .[0].name")
            echo "  - $id: $NAME"
        done
        ANY_DRIFT=true
        TOTAL_MISSING=$((TOTAL_MISSING + ${#UNALLOWLISTED_MISSING[@]}))
    fi

    # Report extra automations
    if [[ ${#UNALLOWLISTED_EXTRA[@]} -gt 0 ]]; then
        echo "EXTRA (in project but not in template):"
        for id in "${UNALLOWLISTED_EXTRA[@]}"; do
            NAME=$(echo "$PROJECT_AUTOMATIONS" | jq -r "map(select(.id == \"$id\")) | .[0].name")
            echo "  - $id: $NAME"
        done
        ANY_DRIFT=true
        TOTAL_EXTRA=$((TOTAL_EXTRA + ${#UNALLOWLISTED_EXTRA[@]}))
    fi

    # Report changed automations
    if [[ ${#UNALLOWLISTED_CHANGED[@]} -gt 0 ]]; then
        echo "CHANGED (in both but with different content):"
        for id in "${UNALLOWLISTED_CHANGED[@]}"; do
            NAME=$(echo "$PROJECT_AUTOMATIONS" | jq -r "map(select(.id == \"$id\")) | .[0].name")
            echo "  - $id: $NAME"
        done
        ANY_DRIFT=true
        TOTAL_CHANGED=$((TOTAL_CHANGED + ${#UNALLOWLISTED_CHANGED[@]}))
    fi

    # Report allowlisted items
    if [[ $ALLOWLISTED_COUNT -gt 0 ]]; then
        echo "ALLOWLISTED (intentional overrides):"
        for id in "${ALLOWED_IDS[@]}"; do
            # Check if this ID was actually in one of our lists
            if [[ " ${MISSING_IDS[@]} ${EXTRA_IDS[@]} ${CHANGED_IDS[@]} " =~ " ${id} " ]]; then
                echo "  - $id (ok)"
            fi
        done
        TOTAL_ALLOWLISTED=$((TOTAL_ALLOWLISTED + ALLOWLISTED_COUNT))
    fi

    # If no drift found, report that
    if [[ ${#UNALLOWLISTED_MISSING[@]} -eq 0 && ${#UNALLOWLISTED_EXTRA[@]} -eq 0 && ${#UNALLOWLISTED_CHANGED[@]} -eq 0 ]]; then
        echo "✓ No unallowlisted drift"
    fi

    echo ""
done

# Print summary line
echo "DRIFT: missing=$TOTAL_MISSING extra=$TOTAL_EXTRA changed=$TOTAL_CHANGED allowlisted=$TOTAL_ALLOWLISTED"

# Exit based on whether any drift was found
if $ANY_DRIFT; then
    exit 1
else
    exit 0
fi

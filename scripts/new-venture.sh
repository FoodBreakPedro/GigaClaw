#!/usr/bin/env bash

# ============================================================================
# new-venture.sh - GigaClaw venture project scaffolding
# ============================================================================
# Usage: new-venture.sh <slug> [--base-url http://localhost:5230] [--workspace <path>]
#
# Creates a new GigaClaw venture project with essential labels.
# Idempotent: if the project exists, prints what exists and exits 0.
# If --workspace is provided, sets the workspace path after creation.
# ============================================================================

set -euo pipefail

# Hardcoded canonical venture list
readonly VALID_SLUGS=(
    "gamelifteat"
    "gamepowergym"
    "zabsconsulting"
    "pedrorzabala"
    "hyperlanetravels"
    "karalungaming"
)

# Labels to seed for every venture project
readonly LABELS=(
    "ready-for-cms"
    "dispatched"
    "approved"
    "blocked"
    "image-upgrade-pending"
    "needs-image"
)

# Default values
BASE_URL="${BASE_URL:-http://localhost:5230}"

# Parse command line arguments
if [[ $# -lt 1 ]]; then
    echo "Usage: $(basename "$0") <slug> [--base-url <url>] [--workspace <path>]"
    echo ""
    echo "Valid venture slugs:"
    printf '  - %s\n' "${VALID_SLUGS[@]}"
    exit 1
fi

SLUG="$1"
WORKSPACE=""
shift || true

# Parse optional arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url)
            BASE_URL="$2"
            shift 2
            ;;
        --workspace)
            WORKSPACE="$2"
            shift 2
            ;;
        *)
            echo "Error: Unknown argument '$1'"
            exit 1
            ;;
    esac
done

# ============================================================================
# Validate slug
# ============================================================================
slug_valid=false
for valid_slug in "${VALID_SLUGS[@]}"; do
    if [[ "$SLUG" == "$valid_slug" ]]; then
        slug_valid=true
        break
    fi
done

if [[ "$slug_valid" == false ]]; then
    echo "Error: Invalid venture slug '$SLUG'"
    echo ""
    echo "Valid venture slugs:"
    printf '  - %s\n' "${VALID_SLUGS[@]}"
    exit 1
fi

# ============================================================================
# Check for jq
# ============================================================================
if ! command -v jq &> /dev/null; then
    echo "Error: jq is required but not found on PATH"
    echo "Install it with: brew install jq (macOS) or apt-get install jq (Linux)"
    exit 1
fi

# ============================================================================
# Check API connectivity and list existing projects
# ============================================================================
echo "Checking API at ${BASE_URL}..."

# Try to fetch the projects list; fail loudly if API is unreachable
if ! projects_response=$(curl -s -w "\n%{http_code}" "${BASE_URL}/api/projects" 2>&1); then
    echo "Error: Failed to reach API at ${BASE_URL}"
    echo "Ensure GigaClaw is running: ./run.sh"
    exit 1
fi

http_code=$(echo "$projects_response" | tail -n 1)
projects_json=$(echo "$projects_response" | sed '$d')

if [[ "$http_code" != "200" ]]; then
    echo "Error: API returned HTTP $http_code"
    if [[ -n "$projects_json" ]]; then
        echo "Response: $projects_json"
    fi
    echo "Ensure GigaClaw is running: ./run.sh"
    exit 1
fi

# ============================================================================
# Check if project already exists
# ============================================================================
existing_project=$(echo "$projects_json" | jq -r ".[] | select(.slug == \"$SLUG\") | .slug" 2>/dev/null || echo "")

if [[ -n "$existing_project" ]]; then
    echo "Project '$SLUG' already exists."

    # Get the full project object to report workspace path
    project_info=$(echo "$projects_json" | jq ".[] | select(.slug == \"$SLUG\")" 2>/dev/null || echo "")
    workspace_path=$(echo "$project_info" | jq -r '.workspacePath // "not set"' 2>/dev/null || echo "not set")
    echo "  Workspace: $workspace_path"

    # Fetch the labels for this project
    labels_response=$(curl -s -w "\n%{http_code}" "${BASE_URL}/api/projects/${SLUG}/labels" 2>&1)
    labels_http_code=$(echo "$labels_response" | tail -n 1)
    labels_json=$(echo "$labels_response" | sed '$d')

    if [[ "$labels_http_code" == "200" ]]; then
        existing_labels=$(echo "$labels_json" | jq -r '.[].name' 2>/dev/null || echo "")
        echo "Existing labels:"
        if [[ -n "$existing_labels" ]]; then
            echo "$existing_labels" | sed 's/^/  - /'
        else
            echo "  (none)"
        fi
    fi

    echo ""
    echo "Idempotent: project already set up, no changes made."
    exit 0
fi

# ============================================================================
# Create the project
# ============================================================================
echo "Creating project '$SLUG'..."

create_response=$(curl -s -w "\n%{http_code}" \
    -X POST "${BASE_URL}/api/projects" \
    -H "Content-Type: application/json" \
    -d "{\"name\": \"$SLUG\"}" 2>&1)

create_http_code=$(echo "$create_response" | tail -n 1)
create_json=$(echo "$create_response" | sed '$d')

if [[ "$create_http_code" != "201" ]]; then
    echo "Error: Failed to create project (HTTP $create_http_code)"
    echo "Response: $create_json"
    exit 1
fi

echo "✓ Project created: $SLUG"

# ============================================================================
# Seed labels
# ============================================================================
echo "Seeding labels..."

for label_name in "${LABELS[@]}"; do
    label_response=$(curl -s -w "\n%{http_code}" \
        -X POST "${BASE_URL}/api/projects/${SLUG}/labels" \
        -H "Content-Type: application/json" \
        -d "{\"name\": \"$label_name\", \"color\": \"#6366f1\"}" 2>&1)

    label_http_code=$(echo "$label_response" | tail -n 1)
    label_json=$(echo "$label_response" | sed '$d')

    if [[ "$label_http_code" == "201" ]]; then
        echo "✓ Label created: $label_name"
    else
        echo "✗ Failed to create label '$label_name' (HTTP $label_http_code)"
        if [[ -n "$label_json" ]]; then
            echo "  Response: $label_json"
        fi
        # Continue anyway; project is partially set up
    fi
done

echo ""

# ============================================================================
# Set workspace path (if provided)
# ============================================================================
if [[ -n "$WORKSPACE" ]]; then
    echo "Setting workspace path..."

    workspace_response=$(curl -s -w "\n%{http_code}" \
        -X PATCH "${BASE_URL}/api/projects/${SLUG}" \
        -H "Content-Type: application/json" \
        -d "{\"workspacePath\": \"$WORKSPACE\"}" 2>&1)

    workspace_http_code=$(echo "$workspace_response" | tail -n 1)
    workspace_json=$(echo "$workspace_response" | sed '$d')

    if [[ "$workspace_http_code" == "200" ]]; then
        echo "✓ Workspace path set: $WORKSPACE"
    else
        echo "✗ Failed to set workspace path (HTTP $workspace_http_code)"
        if [[ -n "$workspace_json" ]]; then
            echo "  Response: $workspace_json"
        fi
    fi
    echo ""
fi

# ============================================================================
# Attempt template initialization (try new endpoint first, fall back to UI)
# ============================================================================
echo "Attempting template initialization..."

init_response=$(curl -s -w "\n%{http_code}" \
    -X POST "${BASE_URL}/api/projects/${SLUG}/initialize" \
    -H "Content-Type: application/json" 2>&1)

init_http_code=$(echo "$init_response" | tail -n 1)
init_json=$(echo "$init_response" | sed '$d')

if [[ "$init_http_code" == "200" ]] || [[ "$init_http_code" == "204" ]]; then
    echo "✓ Template initialization complete (via API endpoint)."
elif [[ "$init_http_code" == "404" ]]; then
    echo "✓ Template initialization not yet available via API."
    echo "  Please open the project in GigaClaw and click Initialize to:"
    echo "    - Copy agent templates to the workspace"
    echo "    - Create agent members"
    echo "    - Initialize version control"
    echo ""
    echo "  Note: A POST /api/projects/{slug}/initialize endpoint is planned"
    echo "  and will automate this step in the future."
else
    echo "✗ Unexpected response from initialize endpoint (HTTP $init_http_code)"
    if [[ -n "$init_json" ]]; then
        echo "  Response: $init_json"
    fi
fi

echo ""
echo "Venture project '$SLUG' is ready."

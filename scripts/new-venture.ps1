# ============================================================================
# new-venture.ps1 - GigaClaw venture project scaffolding
# ============================================================================
# Usage: .\new-venture.ps1 <slug> [-BaseUrl http://localhost:5230] [-Workspace <path>]
#
# Creates a new GigaClaw venture project with essential labels.
# Idempotent: if the project exists, prints what exists and exits 0.
# If -Workspace is provided, sets the workspace path after creation.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Slug,

    [Parameter(Mandatory = $false)]
    [string]$BaseUrl = "http://localhost:5230",

    [Parameter(Mandatory = $false)]
    [string]$Workspace = ""
)

$ErrorActionPreference = 'Stop'

# Hardcoded canonical venture list
$ValidSlugs = @(
    "gamelifteat",
    "gamepowergym",
    "zabsconsulting",
    "pedrorzabala",
    "hyperlanetravels",
    "karalungaming"
)

# Labels to seed for every venture project
$Labels = @(
    "ready-for-cms",
    "dispatched",
    "approved",
    "blocked",
    "image-upgrade-pending",
    "needs-image"
)

# ============================================================================
# Validate slug
# ============================================================================
if ($Slug -notin $ValidSlugs) {
    Write-Host "Error: Invalid venture slug '$Slug'"
    Write-Host ""
    Write-Host "Valid venture slugs:"
    $ValidSlugs | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

# ============================================================================
# Check API connectivity and list existing projects
# ============================================================================
Write-Host "Checking API at ${BaseUrl}..."

try {
    $projectsResponse = Invoke-RestMethod `
        -Uri "${BaseUrl}/api/projects" `
        -Method Get `
        -ErrorAction Stop
} catch {
    Write-Host "Error: Failed to reach API at ${BaseUrl}"
    Write-Host "Ensure GigaClaw is running: ./run.sh (or run.bat on Windows)"
    Write-Host "Exception: $($_.Exception.Message)"
    exit 1
}

# ============================================================================
# Check if project already exists
# ============================================================================
$existingProject = $projectsResponse | Where-Object { $_.slug -eq $Slug }

if ($null -ne $existingProject) {
    Write-Host "Project '$Slug' already exists."

    $workspacePath = if ($existingProject.workspacePath) { $existingProject.workspacePath } else { "not set" }
    Write-Host "  Workspace: $workspacePath"

    # Fetch the labels for this project
    try {
        $labelsResponse = Invoke-RestMethod `
            -Uri "${BaseUrl}/api/projects/${Slug}/labels" `
            -Method Get `
            -ErrorAction Stop

        Write-Host "Existing labels:"
        if ($labelsResponse.Count -gt 0) {
            $labelsResponse | ForEach-Object { Write-Host "  - $($_.name)" }
        } else {
            Write-Host "  (none)"
        }
    } catch {
        Write-Host "  (could not fetch labels)"
    }

    Write-Host ""
    Write-Host "Idempotent: project already set up, no changes made."
    exit 0
}

# ============================================================================
# Create the project
# ============================================================================
Write-Host "Creating project '$Slug'..."

try {
    $createRequest = @{
        name = $Slug
    } | ConvertTo-Json

    $projectResponse = Invoke-RestMethod `
        -Uri "${BaseUrl}/api/projects" `
        -Method Post `
        -ContentType "application/json" `
        -Body $createRequest `
        -ErrorAction Stop

    Write-Host "✓ Project created: $Slug"
} catch {
    Write-Host "Error: Failed to create project"
    Write-Host "Exception: $($_.Exception.Message)"
    if ($_.Exception.Response.StatusCode) {
        Write-Host "HTTP Status: $($_.Exception.Response.StatusCode)"
    }
    exit 1
}

# ============================================================================
# Seed labels
# ============================================================================
Write-Host "Seeding labels..."

foreach ($labelName in $Labels) {
    try {
        $labelRequest = @{
            name = $labelName
            color = "#6366f1"
        } | ConvertTo-Json

        $labelResponse = Invoke-RestMethod `
            -Uri "${BaseUrl}/api/projects/${Slug}/labels" `
            -Method Post `
            -ContentType "application/json" `
            -Body $labelRequest `
            -ErrorAction Stop

        Write-Host "✓ Label created: $labelName"
    } catch {
        Write-Host "✗ Failed to create label '$labelName'"
        Write-Host "  Exception: $($_.Exception.Message)"
        # Continue anyway; project is partially set up
    }
}

Write-Host ""

# ============================================================================
# Set workspace path (if provided)
# ============================================================================
if ($Workspace) {
    Write-Host "Setting workspace path..."

    try {
        $workspaceRequest = @{
            workspacePath = $Workspace
        } | ConvertTo-Json

        $workspaceResponse = Invoke-RestMethod `
            -Uri "${BaseUrl}/api/projects/${Slug}" `
            -Method Patch `
            -ContentType "application/json" `
            -Body $workspaceRequest `
            -ErrorAction Stop

        Write-Host "✓ Workspace path set: $Workspace"
    } catch {
        Write-Host "✗ Failed to set workspace path"
        Write-Host "  Exception: $($_.Exception.Message)"
    }
    Write-Host ""
}

# ============================================================================
# Attempt template initialization (try new endpoint first, fall back to UI)
# ============================================================================
Write-Host "Attempting template initialization..."

try {
    $initResponse = Invoke-RestMethod `
        -Uri "${BaseUrl}/api/projects/${Slug}/initialize" `
        -Method Post `
        -ContentType "application/json" `
        -ErrorAction Stop

    Write-Host "✓ Template initialization complete (via API endpoint)."
} catch {
    $statusCode = $_.Exception.Response.StatusCode.value__

    if ($statusCode -eq 404) {
        Write-Host "✓ Template initialization not yet available via API."
        Write-Host "  Please open the project in GigaClaw and click Initialize to:"
        Write-Host "    - Copy agent templates to the workspace"
        Write-Host "    - Create agent members"
        Write-Host "    - Initialize version control"
        Write-Host ""
        Write-Host "  Note: A POST /api/projects/{slug}/initialize endpoint is planned"
        Write-Host "  and will automate this step in the future."
    } else {
        Write-Host "✗ Unexpected response from initialize endpoint (HTTP $statusCode)"
        Write-Host "  Exception: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Venture project '$Slug' is ready."

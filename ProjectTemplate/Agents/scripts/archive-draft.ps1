<#
.SYNOPSIS
    Archives a finished GigaClaw content-pipeline draft to the Obsidian vault (or any other
    git-friendly folder) referenced by $env:GIGACLAW_ARCHIVE_ROOT. Task 16 / AD-7.

.DESCRIPTION
    Invoked as the executePowerShell step appended to the `cms-dispatch-on-done` automation,
    AFTER the CMS dispatch has already succeeded and the `dispatched` label + CMS receipt
    comment have already been applied — dispatch integrity is sealed before this ever runs.

    Best-effort only: every failure path here prints a warning to stderr and still exits 0, so a
    broken or unconfigured archive can never fail, retry, or otherwise disturb a dispatch that
    already put the Post live. This mirrors ActionExecutor's `AbortOnFailure: false` contract for
    this step in automations.json — belt AND suspenders.

    Fetches the ticket's description itself (rather than relying on {draft.*} placeholders, which
    are httpRequest-only and JSON-escaped for splicing into a JSON body — not safe to hand to a
    shell verbatim) via the GigaClaw REST API, so the archive always matches the exact text the
    CMS dispatch sent.

    Also used, unmodified, by tools/backfill-archive.sh / .ps1 to archive tickets dispatched
    before this task landed — same script, same behavior, just invoked in a loop instead of from
    the automation chain.

.PARAMETER TicketId
    The GigaClaw ticket id (numeric, passed as text).

.PARAMETER ProjectSlug
    The venture/project slug the ticket belongs to.

.PARAMETER AdminUrl
    Optional CMS admin URL for the published Post (from the preceding httpRequest action's
    {http.body.adminUrl}). Recorded as a provenance line; omitted if blank.

.OUTPUTS
    A single human-readable summary line on stdout — captured by ActionExecutor into
    {powershell.stdout} so the automation's follow-up addComment can post it verbatim onto the
    ticket, alongside the CMS receipt.

.EXAMPLE
    pwsh -File archive-draft.ps1 -TicketId 42 -ProjectSlug gamelifteat -AdminUrl https://cms.example/admin/42
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$TicketId,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$ProjectSlug,

    [Parameter(Mandatory = $false, Position = 2)]
    [string]$AdminUrl = ""
)

$ErrorActionPreference = 'Stop'

function Write-Diag {
    param([string]$Message)
    # Explicit OS-level stderr — never PowerShell's Warning/Verbose streams, whose
    # non-interactive redirection behavior differs across platforms/hosts. Keeps stdout reserved
    # for exactly one line: the summary ActionExecutor captures as {powershell.stdout}.
    [Console]::Error.WriteLine("[archive-draft] $Message")
}

# Hand-rolled on purpose, mirroring GigaClaw.Core/Automation/DraftFrontmatter.cs: only the two
# fields this script needs (slug for the filename, title for a nicer summary line), tolerant of
# a missing key, and deliberately ignorant of nested blocks (e.g. `seo:`) — an indented line is
# always skipped so a nested `title:` under `seo:` never shadows the top-level one.
function Get-FrontmatterField {
    param([string]$Text, [string]$Key)
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $pattern = "^" + [regex]::Escape($Key) + ":\s*(.*)$"
    $lines = ($Text -replace "`r`n", "`n" -replace "`r", "`n") -split "`n"
    $fenceCount = 0
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '---') {
            $fenceCount++
            if ($fenceCount -ge 2) { break }
            continue
        }
        if ($fenceCount -ne 1) { continue }
        if ($line.Length -gt 0 -and ($line[0] -eq ' ' -or $line[0] -eq "`t")) { continue }
        if ($trimmed -match $pattern) {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ""
}

function Get-SafeFileToken {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    return ($Value.Trim() -replace '[^a-zA-Z0-9\-_]', '-')
}

$summary = "Draft archival skipped."

try {
    $archiveRoot = $env:GIGACLAW_ARCHIVE_ROOT
    if ([string]::IsNullOrWhiteSpace($archiveRoot)) {
        $summary = "Draft archival skipped: GIGACLAW_ARCHIVE_ROOT is not set on the server. Set it to an Obsidian vault path (or any folder) to enable archival."
        Write-Diag $summary
        Write-Output $summary
        exit 0
    }

    $apiUrl = $env:GIGACLAW_API_URL
    if ([string]::IsNullOrWhiteSpace($apiUrl)) { $apiUrl = "http://localhost:5230" }
    $apiUrl = $apiUrl.TrimEnd('/')

    $ticket = $null
    try {
        $ticket = Invoke-RestMethod -Uri "$apiUrl/api/projects/$ProjectSlug/tickets/$TicketId" -Method Get -TimeoutSec 15
    } catch {
        $summary = "Draft archival failed: could not fetch ticket #$TicketId from ${apiUrl}: $($_.Exception.Message)"
        Write-Diag $summary
        Write-Output $summary
        exit 0
    }

    $description = [string]$ticket.description
    if ([string]::IsNullOrWhiteSpace($description)) {
        $summary = "Draft archival skipped: ticket #$TicketId has no description to archive."
        Write-Diag $summary
        Write-Output $summary
        exit 0
    }

    $slug = Get-SafeFileToken (Get-FrontmatterField -Text $description -Key 'slug')
    $fileToken = if ($slug) { $slug } else { "ticket-$TicketId" }
    $title = Get-FrontmatterField -Text $description -Key 'title'

    $monthDir = (Get-Date).ToString('yyyy-MM')
    $targetDir = Join-Path (Join-Path $archiveRoot $ProjectSlug) $monthDir
    $targetPath = Join-Path $targetDir "$fileToken.md"

    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    # Frontmatter preserved verbatim (AD-7's canonical draft text), plus appended provenance —
    # never edited, only appended to, so the archived copy is always a superset of what shipped.
    $provenance = @()
    $provenance += ""
    $provenance += "---"
    $provenance += "<!-- Archived by GigaClaw (Task 16 / AD-7). Frontmatter and body above are verbatim. -->"
    $provenance += "ticket-id: $ProjectSlug#$TicketId"
    if (-not [string]::IsNullOrWhiteSpace($AdminUrl)) {
        $provenance += "cms-admin-url: $AdminUrl"
    }
    $provenance += "archived-at: $((Get-Date).ToUniversalTime().ToString('o'))"

    $content = $description.TrimEnd() + "`n" + ($provenance -join "`n") + "`n"
    Set-Content -Path $targetPath -Value $content -Encoding utf8 -NoNewline

    $relativePath = Join-Path $ProjectSlug (Join-Path $monthDir "$fileToken.md")
    $summary = "Archived draft for ${ProjectSlug}#${TicketId} to $relativePath"
    if ($title) { $summary = "Archived '$title' (${ProjectSlug}#${TicketId}) to $relativePath" }

    # Best-effort git add+commit. The archive root need not be a git repo at all; a commit
    # failure (detached HEAD, no user.name/email configured, nothing new to hash, git missing
    # entirely, …) must never escalate past a warning — the file is already safely on disk
    # either way, which is the part that actually matters.
    try {
        $isRepoOutput = & git -C $archiveRoot rev-parse --is-inside-work-tree 2>$null
        if ($LASTEXITCODE -eq 0 -and $isRepoOutput -eq 'true') {
            & git -C $archiveRoot add -- $relativePath 2>&1 | ForEach-Object { Write-Diag "git add: $_" }
            & git -C $archiveRoot -c user.name="GigaClaw" -c user.email="automation@gigaclaw.local" `
                commit -m "Archive draft: $ProjectSlug#$TicketId" -- $relativePath 2>&1 |
                ForEach-Object { Write-Diag "git commit: $_" }
            if ($LASTEXITCODE -ne 0) {
                Write-Diag "git commit exited $LASTEXITCODE for $relativePath (non-fatal — file is already on disk)"
            }
        }
    } catch {
        Write-Diag "git add/commit failed (non-fatal — file is already on disk): $($_.Exception.Message)"
    }

    Write-Output $summary
    exit 0
} catch {
    $summary = "Draft archival failed unexpectedly: $($_.Exception.Message)"
    Write-Diag $summary
    Write-Output $summary
    exit 0
}

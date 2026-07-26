#!/usr/bin/env pwsh
# Publishes the three runnable GigaClaw projects (Web + QaRunner + ClaudeMock)
# into a single sibling-exe layout, which is what the qa-tester skill and the
# QaRunner's TestInstance expect (GIGACLAW_QARUNNER_EXE / GigaClaw.ClaudeMock.exe
# resolved relative to GigaClaw.Web.exe).
#
# Versioning: the assembly version is derived automatically by MinVer from the
# latest `vX.Y.Z` git tag. The release ritual is therefore:
#     git tag vX.Y.Z && git push --tags
# No manual edits to any csproj are required. Builds between tags are emitted
# as pre-releases (e.g. 0.7.1-alpha.0.3). MinVer needs full git history, so
# avoid `git clone --depth 1` when invoking this script.
[CmdletBinding()]
param(
    [string] $Out = 'C:\GigaClaw-stable',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

Write-Host "Publishing GigaClaw ($Configuration) to $Out ..." -ForegroundColor Cyan

# Web + QaRunner: published as siblings (GIGACLAW_QARUNNER_EXE expects this layout).
foreach ($proj in 'GigaClaw.Web', 'GigaClaw.QaRunner') {
    Write-Host "  -> $proj" -ForegroundColor DarkGray
    dotnet publish (Join-Path $repo $proj) -c $Configuration -o $Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# ClaudeMock: published into a qa-mock/ subfolder so it does NOT sit next to GigaClaw.Web.exe
# as `claude.exe`. Otherwise ClaudeRunner.ResolveClaudeBinary() would prefer the mock for *all*
# agents, not just QA. The QaRunner's TestInstance picks it up explicitly via GIGACLAW_CLAUDE_BIN.
$mockOut = Join-Path $Out 'qa-mock'
Write-Host "  -> GigaClaw.ClaudeMock (-> $mockOut)" -ForegroundColor DarkGray
dotnet publish (Join-Path $repo 'GigaClaw.ClaudeMock') -c $Configuration -o $mockOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for GigaClaw.ClaudeMock" }

Write-Host "`nDone. Stable build is in $Out" -ForegroundColor Green

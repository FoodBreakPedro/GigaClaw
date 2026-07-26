# Rebrand KittyClaw to GigaClaw

This plan outlines the complete refactoring and rebranding of the project from **KittyClaw** to **GigaClaw**. This covers solution and project files, directory names, namespaces, C# source files, Razor components, configuration files, scripts, documentation, and image assets.

## User Review Required

> [!IMPORTANT]
> - Directory and file renaming will be performed via terminal commands (`mv` / git operations) to ensure project integrity.
> - Image branding assets in `KittyClaw.Web/wwwroot` will be renamed and updated using the assets available in `branding/` (such as `GigaClaw.png`, `GigaClaw-Logo-Horizontal.png`, etc.).
> - Local data directory paths used in code (e.g. `~/.kittyclaw`) will be updated to `~/.gigaclaw`.

## Proposed Changes

### Solution & Projects
#### [DELETE] [KittyClaw.slnx](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/KittyClaw.slnx)
#### [NEW] [GigaClaw.slnx](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.slnx)

Rename project directories and `.csproj` files:
- `KittyClaw.Core/` -> `GigaClaw.Core/` (`KittyClaw.Core.csproj` -> `GigaClaw.Core.csproj`)
- `KittyClaw.Core.Tests/` -> `GigaClaw.Core.Tests/` (`KittyClaw.Core.Tests.csproj` -> `GigaClaw.Core.Tests.csproj`)
- `KittyClaw.Web/` -> `GigaClaw.Web/` (`KittyClaw.Web.csproj` -> `GigaClaw.Web.csproj`)
- `KittyClaw.ClaudeMock/` -> `GigaClaw.ClaudeMock/` (`KittyClaw.ClaudeMock.csproj` -> `GigaClaw.ClaudeMock.csproj`)
- `KittyClaw.QaRunner/` -> `GigaClaw.QaRunner/` (`KittyClaw.QaRunner.csproj` -> `GigaClaw.QaRunner.csproj`)

---

### Source Code Refactoring & Namespaces

Replace all case variations across C#, Razor, JSON, Props, YAML, Shell scripts, and Markdown files:
- `KittyClaw` -> `GigaClaw`
- `kittyclaw` -> `gigaclaw`
- `KITTYCLAW` -> `GIGACLAW`
- `kitty_claw` -> `giga_claw`
- `kitty-claw` -> `giga-claw`

Affected major file groups:
- `GigaClaw.Core/**/*.cs`
- `GigaClaw.Core.Tests/**/*.cs`
- `GigaClaw.Web/**/*.cs`, `*.razor`, `*.json`, `*.css`
- `GigaClaw.ClaudeMock/**/*.cs`, `*.csproj`
- `GigaClaw.QaRunner/**/*.cs`, `*.csproj`
- `Directory.Build.props`
- `run.sh` / `run.bat`
- `.github/workflows/ci.yml`
- `README.md`, `CLAUDE.md`, `CHANGELOG.md`, `ProjectTemplate/CLAUDE.md`

---

### Web Assets & Image Rebranding

In `GigaClaw.Web/wwwroot`:
- Update `KittyClaw-Logo-Horizontal.webp` -> `GigaClaw-Logo-Horizontal.webp`
- Update `KittyClaw-Logo.webp` -> `GigaClaw-Logo.webp`
- Update `KittyClaw-Picto.webp` -> `GigaClaw-Picto.webp`
- Update `KittyClaw.webp` -> `GigaClaw.webp`
- Update references in `App.razor`, layout components, and CSS files to point to the new `GigaClaw` assets.

---

## Verification Plan

### Automated Tests
- Build the solution: `dotnet build GigaClaw.slnx`
- Run unit tests: `dotnet test GigaClaw.Core.Tests/GigaClaw.Core.Tests.csproj`

### Manual Verification
- Verify that `grep -i "kittyclaw"` returns no unintended remaining references in the codebase.

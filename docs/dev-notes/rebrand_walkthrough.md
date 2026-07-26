# KittyClaw to GigaClaw Rebrand & Refactoring Walkthrough

The codebase has been refactored and rebranded from **KittyClaw** to **GigaClaw**.

## Changes Made

### Solution & Projects
- Renamed solution file: `KittyClaw.slnx` -> [GigaClaw.slnx](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.slnx)
- Renamed project directories and `.csproj` files:
  - `KittyClaw.Core` -> [GigaClaw.Core](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.Core)
  - `KittyClaw.Core.Tests` -> [GigaClaw.Core.Tests](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.Core.Tests)
  - `KittyClaw.Web` -> [GigaClaw.Web](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.Web)
  - `KittyClaw.ClaudeMock` -> [GigaClaw.ClaudeMock](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.ClaudeMock)
  - `KittyClaw.QaRunner` -> [GigaClaw.QaRunner](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.QaRunner)

### Code & Config Refactoring
- Updated all namespaces, class usages, variable names, CLI options, environment variable names (`GIGACLAW_*`), configuration keys, and database/storage paths (`~/.gigaclaw`).
- Applied string replacements across all C# source files, Razor components, JSON configs, launch settings, shell scripts ([run.sh](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/run.sh)), batch files ([run.bat](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/run.bat)), PowerShell scripts ([publish-stable.ps1](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/tools/publish-stable.ps1)), and documentation files ([README.md](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/README.md), [CLAUDE.md](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/CLAUDE.md), `doc/*.md`).
- A total of **892 replacements** across **255 files** were completed.

### Image Assets & Rebranding
- Added new GigaClaw branding assets to [branding/](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/branding) and converted WebP variants into [GigaClaw.Web/wwwroot/](file:///Users/pedrozabala/Documents/HomeBase/Github/KittyClaw/GigaClaw.Web/wwwroot):
  - `GigaClaw-Logo-Horizontal.webp`
  - `GigaClaw-Logo.webp`
  - `GigaClaw-Picto.webp`
  - `GigaClaw.webp`

## Verification Results

### Build & Unit Tests
- Executed `dotnet build GigaClaw.slnx`: **Succeeded** with 0 warnings, 0 errors.
- Executed `dotnet test GigaClaw.slnx`: **Passed** 481 / 481 tests (0 failed).
- Verified zero remaining `KittyClaw` references across all files.

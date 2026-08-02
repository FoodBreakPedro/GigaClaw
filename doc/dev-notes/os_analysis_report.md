# GigaClaw Codebase OS Platform Analysis Report

This report presents a breakdown of OS-agnostic vs. OS-specific files in the **GigaClaw** repository, following a thorough analysis of all project files and native platform dependencies.

---

## 1. Core Code Files (OS Agnostic)

The vast majority of the codebase is written in standard cross-platform C# (.NET 10.0) and Blazor, running identically across Windows, macOS, and Linux.

### Key OS-Agnostic Components & Files:
- **Solution & Project Definitions**:
  - [GigaClaw.slnx](../../GigaClaw.slnx)
  - `GigaClaw.Core/GigaClaw.Core.csproj`
  - `GigaClaw.Web/GigaClaw.Web.csproj`
  - `GigaClaw.ClaudeMock/GigaClaw.ClaudeMock.csproj`
  - `GigaClaw.QaRunner/GigaClaw.QaRunner.csproj`
- **Core Automation Engine & Domain Models** (`GigaClaw.Core/`):
  - [AutomationEngine.cs](../../GigaClaw.Core/Automation/AutomationEngine.cs)
  - [ClaudeRunner.cs](../../GigaClaw.Core/Automation/ClaudeRunner.cs)
  - [ClaudeStreamPump.cs](../../GigaClaw.Core/Automation/ClaudeStreamPump.cs)
  - [RunStateManager.cs](../../GigaClaw.Core/Automation/RunStateManager.cs)
  - All trigger handlers, condition evaluators, and action executors (`GigaClaw.Core/Automation/Triggers/*`)
- **Data Persistence & Database** (`GigaClaw.Core/Data/`):
  - [TodoDbContext.cs](../../GigaClaw.Core/Data/TodoDbContext.cs) (SQLite via EF Core)
  - [RegistryDbContext.cs](../../GigaClaw.Core/Data/RegistryDbContext.cs)
- **Services & Helpers**:
  - [ProjectService.cs](../../GigaClaw.Core/Services/ProjectService.cs)
  - [TicketService.cs](../../GigaClaw.Core/Services/TicketService.cs)
  - [ShellResolver.cs](../../GigaClaw.Core/Services/ShellResolver.cs) (cross-platform PATH detection for `pwsh` / `powershell` / `sh`)
  - [IFolderPicker.cs](../../GigaClaw.Core/Platform/IFolderPicker.cs)
- **Web UI & REST API** (`GigaClaw.Web/`):
  - [Program.cs](../../GigaClaw.Web/Program.cs)
  - All Blazor Pages & Components (`GigaClaw.Web/Components/Pages/*`, `Layout/*`)
  - All REST API endpoints (`GigaClaw.Web/Api/*`)
- **Testing & Tooling**:
  - All unit test files in `GigaClaw.Core.Tests/`
  - `GigaClaw.ClaudeMock/` and `GigaClaw.QaRunner/`

---

## 2. MacOS Specific Files

> [!NOTE]
> There are **no macOS-exclusive C# source files** or native macOS P/Invoke bindings in the codebase.

- **Scripts**:
  - [run.sh](../../run.sh): POSIX shell script used to run the app on macOS (and Linux).

*(Note: `TelemetryService.cs` performs a runtime platform check via `OperatingSystem.IsMacOS()` purely for user-agent header formatting).*

---

## 3. Windows Specific Files

If all Windows-specific files listed below were deleted, **the app would continue to compile and run on macOS without issues**.

- [WindowsFolderPicker.cs](../../GigaClaw.Core/Platform/WindowsFolderPicker.cs): Implements `IFolderPicker` using PowerShell and `System.Windows.Forms.FolderBrowserDialog`. Safe to remove on macOS (the UI automatically hides folder browsing if `IFolderPicker` is unregistered).
- [ProcessJobObject.cs](../../GigaClaw.Core/Automation/ProcessJobObject.cs): Encapsulates Win32 Job Objects via `kernel32.dll` P/Invoke (`CreateJobObject`, `AssignProcessToJobObject`) to ensure subprocess cleanup. On non-Windows OS, `ProcessJobObject.TryCreateAndAssign` returns `null` and falls back to standard `.Kill(true)`.
- [run.bat](../../run.bat): Windows CMD batch file launcher.

---

## 4. Android Specific Files

Currently, there are **0 Android-specific files** in the codebase.

---

## 5. Files Required to Compile & Run as an Android App

To compile GigaClaw as a native Android application (e.g. using .NET MAUI Blazor Hybrid), the following files and components would need to be added:

### Project & Solution Setup
1. **`GigaClaw.Android/GigaClaw.Android.csproj`**:
   - Target framework: `net10.0-android` (or `net9.0-android`)
   - Package reference: `Microsoft.AspNetCore.Components.WebView.Maui`
2. **`GigaClaw.Android/MauiProgram.cs`**:
   - Initializes MAUI app builder, registers Blazor WebView, and wires up `GigaClaw.Core` services and SQLite database.

### Android Manifest & App Lifecycle
3. **`GigaClaw.Android/Platforms/Android/AndroidManifest.xml`**:
   - Defines Android permissions: `INTERNET`, `POST_NOTIFICATIONS`, `FOREGROUND_SERVICE`, `READ_EXTERNAL_STORAGE`, `WRITE_EXTERNAL_STORAGE`.
4. **`GigaClaw.Android/Platforms/Android/MainActivity.cs`**:
   - Main entry point activity inheriting from `MauiAppCompatActivity`.
5. **`GigaClaw.Android/Platforms/Android/MainApplication.cs`**:
   - App class handling application-level lifecycle and Android DI registration.

### Android Resources & Icons
6. **`GigaClaw.Android/Resources/AppIcon/appicon.svg`**: App icon resource.
7. **`GigaClaw.Android/Resources/Splash/splash.svg`**: Splash screen resource.

### Android Platform Services
8. **`GigaClaw.Android/Services/AndroidFolderPicker.cs`**:
   - Implements `IFolderPicker` using Android's Storage Access Framework (`Intent.ActionOpenDocumentTree`).
9. **`GigaClaw.Android/Services/AndroidForegroundService.cs`**:
   - Android `ForegroundService` to keep background automation triggers (`AutomationEngine`) active when the app is minimized.

// Cross-platform launcher for the isolated GigaClaw debug instance (port 5232).
// Invoked by the `gigaclaw-web-debug` entry in .claude/launch.json as a .NET 10
// file-based app: `dotnet run tools/run-debug-instance.cs` from the repo root.
//
// Semantics (identical on Windows, macOS, Linux):
//   - GIGACLAW_DATA_DIR   = <ApplicationData>/GigaClaw-debug — same
//     Environment.SpecialFolder.ApplicationData the app itself uses to compute
//     its default data dir (%APPDATA% on Windows, ~/Library/Application Support
//     on macOS, ~/.config on Linux),
//     so the debug instance gets its own registry/projects/runs.
//   - GIGACLAW_CLAUDE_BIN = mock claude CLI (GigaClaw.ClaudeMock), built here
//     on every start so agent dispatches replay canned NDJSON scenarios.
//   - ASPNETCORE_ENVIRONMENT = Development, served at http://localhost:5232.

using System.Diagnostics;
using System.Runtime.InteropServices;

var repoRoot = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(repoRoot, "GigaClaw.slnx")))
{
    Console.Error.WriteLine($"run-debug-instance: expected to run from the repo root, but GigaClaw.slnx is not in {repoRoot}.");
    return 1;
}

var build = Process.Start(new ProcessStartInfo("dotnet")
{
    ArgumentList = { "build", "GigaClaw.ClaudeMock", "-v", "q" },
    WorkingDirectory = repoRoot,
})!;
build.WaitForExit();
if (build.ExitCode != 0)
{
    Console.Error.WriteLine("run-debug-instance: building GigaClaw.ClaudeMock failed.");
    return build.ExitCode;
}

var mockBin = Path.Combine(repoRoot, "GigaClaw.ClaudeMock", "bin", "Debug", "net10.0",
    OperatingSystem.IsWindows() ? "claude.exe" : "claude");
if (!File.Exists(mockBin))
{
    Console.Error.WriteLine($"run-debug-instance: mock claude CLI not found at {mockBin}.");
    return 1;
}

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GigaClaw-debug");

var psi = new ProcessStartInfo("dotnet")
{
    ArgumentList = { "run", "--project", "GigaClaw.Web", "--no-launch-profile", "--urls", "http://localhost:5232" },
    WorkingDirectory = repoRoot,
};
psi.Environment["GIGACLAW_DATA_DIR"] = dataDir;
psi.Environment["GIGACLAW_CLAUDE_BIN"] = mockBin;
psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

Console.WriteLine($"run-debug-instance: data dir {dataDir}");
Console.WriteLine($"run-debug-instance: mock claude {mockBin}");

var child = Process.Start(psi)!;

void KillChild()
{
    try { child.Kill(entireProcessTree: true); } catch { /* already gone */ }
}

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; KillChild(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; KillChild(); });
AppDomain.CurrentDomain.ProcessExit += (_, _) => KillChild();

child.WaitForExit();
return child.ExitCode;

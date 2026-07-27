using System.Text.Json;
using GigaClaw.QaRunner;

// CLI: --scenario <path> --target-api <url> [--ticket <id>] [--web-exe <path>]
//
// Spawns an isolated GigaClaw.Web instance, drives it via Playwright per the scenario,
// uploads screenshots to --target-api (the orchestrator that owns the ticket — typically
// the stable on :5230), and emits a JSON result on stdout.
//
// Exit codes: 0 = PASS, 1 = FAIL, 2 = scenario / runtime error.

string? scenarioPath = null;
string? targetApi = null;
int? ticketId = null;
string? webExe = null;

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--scenario": scenarioPath = args[++i]; break;
        case "--target-api": targetApi = args[++i].TrimEnd('/'); break;
        case "--ticket": ticketId = int.Parse(args[++i]); break;
        case "--web-exe": webExe = args[++i]; break;
    }
}

if (scenarioPath is null || targetApi is null)
{
    await Console.Error.WriteLineAsync("usage: GigaClaw.QaRunner --scenario <path> --target-api <url> [--ticket <id>] [--web-exe <path>]");
    return 2;
}

webExe ??= Environment.GetEnvironmentVariable("GIGACLAW_WEB_EXE");
webExe ??= ResolveWebExe();
if (webExe is null || !File.Exists(webExe))
{
    await Console.Error.WriteLineAsync($"GigaClaw.Web executable not found (tried: {webExe ?? "auto-discovery"}). Pass --web-exe.");
    return 2;
}

// Prefer an existing system browser so local QA does not require Playwright's
// separate ~150 MB Chromium bundle. Fall back to the managed browser when needed.
var browserExe = ResolveBrowserExe();
if (browserExe is not null)
{
    Environment.SetEnvironmentVariable("GIGACLAW_BROWSER_EXE", browserExe);
    Console.Error.WriteLine($"[qa-runner] using system browser: {browserExe}");
}
await EnsurePlaywrightAsync();

Scenario scenario;
try
{
    var json = await File.ReadAllTextAsync(scenarioPath);
    scenario = JsonSerializer.Deserialize<Scenario>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("scenario file deserialized to null");
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"failed to load scenario: {ex.Message}");
    return 2;
}

var screenshotDir = Path.Combine(Path.GetTempPath(), "gigaclaw-qa-shots-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(screenshotDir);

ScenarioResult result;
await using (var instance = await TestInstance.StartAsync(webExe))
{
    Console.Error.WriteLine($"[qa-runner] test instance up at {instance.ApiUrl}");
    var runner = new ScenarioRunner(instance.ApiUrl, screenshotDir);
    result = await runner.RunAsync(scenario);
}

// Upload screenshots to the *target* API (the orchestrator that holds the ticket).
var uploader = new ScreenshotUploader(targetApi);
foreach (var s in result.Screenshots)
{
    if (s.LocalPath is null) continue;
    try
    {
        s.UploadedUrl = await uploader.UploadAsync(s.LocalPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[qa-runner] upload failed for {s.Name}: {ex.Message}");
    }
}

// Cleanup the local screenshot dir; uploaded copies are now on the host.
try { Directory.Delete(screenshotDir, recursive: true); } catch { }

var output = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.Out.WriteLine(output);

return result.Verdict == "PASS" ? 0 : 1;


// ---------- helpers ----------

static string? ResolveWebExe()
{
    // Search order:
    //   1. Sibling of this exe (publish layout: QaRunner + Web together)
    //   2. ../GigaClaw.Web/bin/Debug/net10.0/GigaClaw.Web.exe (dev layout)
    //   3. Walk up to repo root looking for GigaClaw.Web/bin
    var exeName = OperatingSystem.IsWindows() ? "GigaClaw.Web.exe" : "GigaClaw.Web";
    var baseDir = AppContext.BaseDirectory;

    var sibling = Path.Combine(baseDir, exeName);
    if (File.Exists(sibling)) return sibling;

    var dir = new DirectoryInfo(baseDir);
    for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "GigaClaw.Web", "bin");
        if (Directory.Exists(candidate))
        {
            var found = Directory.EnumerateFiles(candidate, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }
    }
    return null;
}

static async Task EnsurePlaywrightAsync()
{
    var browserExe = Environment.GetEnvironmentVariable("GIGACLAW_BROWSER_EXE");
    if (!string.IsNullOrWhiteSpace(browserExe))
    {
        if (!File.Exists(browserExe))
            throw new InvalidOperationException($"GIGACLAW_BROWSER_EXE does not exist: {browserExe}");
        return;
    }

    var pwHome = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
    if (Directory.Exists(pwHome) && Directory.EnumerateDirectories(pwHome, "chromium*").Any())
        return;
    Console.Error.WriteLine("[qa-runner] Chromium not cached, downloading (one-time, ~150 MB)...");
    var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
    if (exitCode != 0)
        throw new InvalidOperationException($"playwright install chromium failed with exit {exitCode}");
}

static string? ResolveBrowserExe()
{
    var configured = Environment.GetEnvironmentVariable("GIGACLAW_BROWSER_EXE");
    if (!string.IsNullOrWhiteSpace(configured))
        return configured;

    string[] candidates;
    if (OperatingSystem.IsMacOS())
    {
        candidates =
        [
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Chromium.app/Contents/MacOS/Chromium",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        ];
    }
    else if (OperatingSystem.IsWindows())
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
    }
    else
    {
        candidates =
        [
            "/usr/bin/google-chrome",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/microsoft-edge",
        ];
    }

    return candidates.FirstOrDefault(File.Exists);
}

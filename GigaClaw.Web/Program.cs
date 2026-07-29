using System.Text.Json;
using System.Text.Json.Serialization;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Services;
using GigaClaw.Web.Api;
using GigaClaw.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Default to HTTP-only on :5230 when no URL config is provided. GigaClaw is a local-only
// app with no HTTPS cert, so the framework default (HTTP + HTTPS dual binding on :5000/:5001)
// is wrong here. 5230 is the historical GigaClaw port — kept for backward compatibility
// with existing skills, bookmarks, and external integrations that point at it.
//
// Only kick in when nothing else (ASPNETCORE_URLS, launchSettings.applicationUrl, --urls,
// urls config key) has set the URL — otherwise UseUrls() called after CreateBuilder would
// overwrite that config and break the qa launch profile, QaRunner test instances, etc.
//
// Also propagate to ASPNETCORE_URLS so downstream consumers that read the env var directly
// (e.g. ClaudeRunner.ResolveApiUrl, which builds the API URL passed to skills) see the same
// port Kestrel is actually binding.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    const string fallbackUrl = "http://localhost:5230";
    builder.WebHost.UseUrls(fallbackUrl);
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", fallbackUrl);
}

// GIGACLAW_DATA_DIR overrides the default %APPDATA%/GigaClaw location.
// Used by isolated test instances (GigaClaw.QaRunner) and anyone running
// multiple parallel GigaClaw processes that must not share registry/projects.
var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var dataDir = Environment.GetEnvironmentVariable("GIGACLAW_DATA_DIR")
    ?? Path.Combine(appData, "GigaClaw");
var legacyDataDir = Path.Combine(appData, "TodoApp");
if (!Directory.Exists(dataDir) && Directory.Exists(legacyDataDir))
{
    Directory.Move(legacyDataDir, dataDir);
}
var appSettings = new GigaClaw.Core.Services.AppSettingsService(dataDir);
builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton(new GigaClaw.Core.Services.LocalizationService(appSettings));
builder.Services.AddSingleton(new ProjectService(dataDir));
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<LocalMediaJobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalMediaJobService>());
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AgentsTemplateService>();
// O7 pack infrastructure: the staged install/uninstall engine for pack.json bundles
// (see doc/pack-infrastructure.md). Stateless — every call carries its own workspace.
builder.Services.AddSingleton<GigaClaw.Core.Packs.PackInstaller>();
builder.Services.AddSingleton<AgentTeamService>();
builder.Services.AddSingleton<TeamStore>();
builder.Services.AddSingleton<TeamRunService>();
builder.Services.AddScoped<GigaClaw.Web.Services.BoardFilterState>();
builder.Services.AddScoped<GigaClaw.Web.Services.BoardSortState>();
builder.Services.AddSingleton<GigaClaw.Web.Services.BoardUpdateNotifier>();
builder.Services.AddScoped<GigaClaw.Web.Services.EscapeKeyStack>();

// Automation engine
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddSingleton<TriggerStateStore>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton(new RunLogStore(dataDir));
builder.Services.AddSingleton<AgentRunRegistry>(sp => new AgentRunRegistry(sp.GetRequiredService<RunLogStore>()));
// Cap concurrent claude subprocesses across all projects (chats bypass). Override with the
// GIGACLAW_MAX_CONCURRENT_AGENTS env var if 3 is too tight or too loose for the host.
var maxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("GIGACLAW_MAX_CONCURRENT_AGENTS"), out var mc) && mc > 0 ? mc : 3;
builder.Services.AddSingleton(new RunConcurrencyGate(maxConcurrent));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<CostTracker>();
// R4: durable per-project file-ownership leases (doc/roadmap/lane-codex-runtime.md).
builder.Services.AddSingleton<GigaClaw.Core.Automation.Policy.FileLeaseStore>();
builder.Services.AddHostedService<GigaClaw.Core.Services.FileLeaseReaper>();
// Durable cost records: cost-log.jsonl (daily budget) + per-ticket token/USD totals.
builder.Services.AddHostedService<RunCostRecorder>();
builder.Services.AddSingleton<AutomationEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationEngine>());
builder.Services.AddSingleton<GitRepositoryWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitRepositoryWatcher>());
// Dead man's switch: force-release concurrency locks held by hung runs (ticket #98, feature #3).
builder.Services.AddHostedService<GigaClaw.Core.Services.ConcurrencyLockReaper>();
builder.Services.AddSingleton<GigaClaw.Core.Services.DashboardTileGate>();
builder.Services.AddSingleton<GigaClaw.Core.Services.DashboardScriptRunner>();
builder.Services.AddSingleton<GigaClaw.Core.Services.DashboardRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GigaClaw.Core.Services.DashboardRefreshService>());
// Auto-promote Scheduled tickets to their target column once FireAt fires (feature #99).
builder.Services.AddHostedService<GigaClaw.Core.Services.ScheduledPromotionService>();
builder.Services.AddSingleton<GigaClaw.Web.Services.AgentRunsState>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GigaClaw.Web.Services.HermesAgentService>();
// Dedicated client for the httpRequest automation action. Redirects are not followed: an
// automation posting to a webhook should see the real status, not a silently followed 3xx.
builder.Services.AddHttpClient(GigaClaw.Core.Automation.HttpRequestActionSpec.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<GigaClaw.Web.Services.UpdateCheckService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GigaClaw.Web.Services.UpdateCheckService>());
// Anonymous daily usage heartbeat (see README "Telemetry" and doc/telemetry.md).
// Never in Development: excludes dotnet watch sessions and QaRunner test instances.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<GigaClaw.Web.Services.TelemetryService>();
}

// Folder picker: only on Windows hosts (local or MAUI-Windows). Cloud deployments
// register nothing, so the UI hides the Parcourir button.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<GigaClaw.Core.Platform.IFolderPicker, GigaClaw.Core.Platform.WindowsFolderPicker>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Serve uploaded images
var uploadsDir = Path.Combine(dataDir, "uploads");
Directory.CreateDirectory(uploadsDir);
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads",
    // Uploads are user/agent-supplied: forbid content-type sniffing and any active content
    // (e.g. a pre-existing SVG) from executing in the app's origin.
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    }
});

app.UseAntiforgery();

app.MapOpenApi();
app.MapTodoApi();

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/update-check/simulate", (string version, GigaClaw.Web.Services.UpdateCheckService svc) =>
    {
        svc.SimulateUpdate(version);
        return Results.Ok(new { simulated = version });
    }).ExcludeFromDescription();
    app.MapPost("/api/dev/update-check/reset", (GigaClaw.Web.Services.UpdateCheckService svc) =>
    {
        svc.ResetSimulation();
        return Results.Ok(new { reset = true });
    }).ExcludeFromDescription();
}

app.MapGet("/api/docs", async (HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    using var client = new HttpClient();
    var json = await client.GetStringAsync($"{baseUrl}/openapi/v1.json");
    using var doc = JsonDocument.Parse(json);
    var markdown = OpenApiMarkdownGenerator.Generate(doc);
    return Results.Text(markdown, "text/markdown; charset=utf-8");
}).ExcludeFromDescription();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

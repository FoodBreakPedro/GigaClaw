using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace GigaClaw.Core.Tests.Automation;

[CollectionDefinition("MockCodex")]
public sealed class MockCodexCollection : ICollectionFixture<MockCodexBinFixture> { }

public sealed class MockCodexBinFixture : IDisposable
{
    public MockCodexBinFixture()
    {
        var executable = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var bin = Path.Combine(directory.FullName, "GigaClaw.CodexMock", "bin");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, executable, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found is null) continue;
            Environment.SetEnvironmentVariable("GIGACLAW_CODEX_BIN", found);
            return;
        }

        throw new FileNotFoundException(
            $"Built Codex mock executable '{executable}' was not found under GigaClaw.CodexMock/bin.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_BIN", null);
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", null);
    }
}

[Collection("MockCodex")]
public sealed class CodexRunnerMockIntegrationTests
{
    [Fact]
    public async Task Dispatch_StreamsPersistsUsageAndMapsClaudeTier()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("codex-integration");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");

        var sessions = new SessionRegistry();
        var runner = new CodexRunner(
            sessions,
            new AgentRunRegistry(),
            new RunConcurrencyGate(1),
            NullLogger<CodexRunner>.Instance);
        var context = Context(project.Slug, workspace);

        var run = await runner.RunAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal("codex", run.Backend);
        Assert.Equal("gpt-5.6-terra", run.Model);
        Assert.Equal("019fd288-363e-7093-92f5-cba942f8eb57", run.SessionId);
        Assert.Equal(14528, run.InputTokens);
        Assert.Equal(9984, run.CacheReadTokens);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant" && e.Text == "CODEX_MOCK_OK");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "cost_unavailable");

        var resumed = await runner.RunAsync(context, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, resumed.Status);
        Assert.Equal(run.SessionId, resumed.SessionId);
    }

    [Fact]
    public async Task FailedTurn_MarksRunFailed()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("codex-error");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", "error-exit");
        try
        {
            var runner = new CodexRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(1),
                NullLogger<CodexRunner>.Instance);
            var run = await runner.RunAsync(Context(project.Slug, workspace), CancellationToken.None);

            Assert.Equal(AgentRunStatus.Failed, run.Status);
            Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text == "mock Codex failure");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", null);
        }
    }

    [Fact]
    public async Task FailedPrimary_RetriesOnceWithActionFallbackModel()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("codex-fallback");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", "primary-model-fails");
        try
        {
            var runner = new CodexRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(1),
                NullLogger<CodexRunner>.Instance);
            var context = Context(project.Slug, workspace, fallbackModel: "gpt-5.4-mini");

            var run = await runner.RunAsync(context, CancellationToken.None);

            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal("gpt-5.4-mini", run.Model);
            Assert.Contains(run.SnapshotBuffer(), e =>
                e.Kind == "fallback" && e.Text.Contains("gpt-5.4-mini", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", null);
        }
    }

    [Fact]
    public async Task CompletedTurnFollowedByLingeringProcess_CompletesAfterResultExitGrace()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("codex-linger");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "programmer", scenario: "default");
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", "linger-after-success");
        try
        {
            var runner = new CodexRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(1),
                NullLogger<CodexRunner>.Instance)
            {
                ResultExitGrace = TimeSpan.FromMilliseconds(250),
            };

            var elapsed = Stopwatch.StartNew();
            var run = await runner.RunAsync(Context(project.Slug, workspace), CancellationToken.None);
            elapsed.Stop();

            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "result" && e.Text == "Codex turn completed");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"Expected runner to complete promptly after terminal event, elapsed {elapsed.Elapsed}.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO", null);
        }
    }

    [Fact]
    public async Task UnsupportedModel_FailsBeforeLaunchingCodex()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("codex-invalid-model");
        var workspace = projects.ResolveWorkspacePath(project);

        var runner = new CodexRunner(
            new SessionRegistry(),
            new AgentRunRegistry(),
            new RunConcurrencyGate(1),
            NullLogger<CodexRunner>.Instance);

        var run = await runner.RunAsync(
            Context(project.Slug, workspace, "qwen3-coder:30b"),
            CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e =>
            e.Kind == "error" && e.Text.Contains("Invalid Codex model", StringComparison.Ordinal));
        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "launch");
    }

    private static ClaudeRunContext Context(
        string slug,
        string workspace,
        string? model = "claude-sonnet-4-6",
        string? fallbackModel = null) => new()
    {
        ProjectSlug = slug,
        WorkspacePath = workspace,
        AgentName = "programmer",
        SkillFile = "programmer/SKILL.md",
        Model = model,
        FallbackModel = fallbackModel,
        MaxRunDuration = TimeSpan.FromSeconds(30),
    };
}

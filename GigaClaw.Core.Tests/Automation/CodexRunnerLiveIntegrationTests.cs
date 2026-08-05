using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

public sealed class CodexRunnerLiveIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveCodex")]
    public async Task RealCli_CompletesWithHooksStreamingAndUsage()
    {
        if (Environment.GetEnvironmentVariable("GIGACLAW_LIVE_CODEX") != "1") return;

        var previousBinary = Environment.GetEnvironmentVariable("GIGACLAW_CODEX_BIN");
        Environment.SetEnvironmentVariable("GIGACLAW_CODEX_BIN", ResolveLiveCodexBinary());
        try
        {
            using var temp = new TempDir();
            var workspace = FindRepositoryRoot();
            var runner = new CodexRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(1),
                NullLogger<CodexRunner>.Instance);
            var context = new ClaudeRunContext
            {
                ProjectSlug = "live-codex",
                WorkspacePath = temp.Path,
                ExecutionPath = workspace,
                AgentName = "programmer",
                SkillFile = "(inline)",
                InlineSkillContent = "Reply with exactly CODEX_RUNNER_LIVE_OK. Do not use tools.",
                Model = "gpt-5.6-sol",
                PersistSession = false,
                MaxRunDuration = TimeSpan.FromMinutes(2),
            };

            var run = await runner.RunAsync(context, CancellationToken.None);

            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal("codex", run.Backend);
            Assert.True(run.HasUsage);
            Assert.NotNull(run.ExternalRunId);
            Assert.Contains(run.SnapshotBuffer(), e =>
                e.Kind == "assistant" && e.Text.Contains("CODEX_RUNNER_LIVE_OK", StringComparison.Ordinal));
            Assert.DoesNotContain(run.SnapshotBuffer(), e =>
                e.Kind == "error" && e.Text.Contains("policy hook", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIGACLAW_CODEX_BIN", previousBinary);
        }
    }

    private static string ResolveLiveCodexBinary()
    {
        var configured = Environment.GetEnvironmentVariable("GIGACLAW_LIVE_CODEX_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var executable = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "Live Codex CLI not found on PATH. Set GIGACLAW_LIVE_CODEX_BIN to its absolute path.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("GigaClaw repository root was not found");
    }
}

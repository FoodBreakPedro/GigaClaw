using System.Text.Json;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Automation;

public class AgentRunRegistryTests
{
    [Fact]
    public void Complete_IsIdempotent_DoesNotDowngradeTerminalStatus()
    {
        var registry = new AgentRunRegistry();
        var run = new AgentRun
        {
            RunId = "r1", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };
        registry.Register(run);

        registry.Complete("r1", AgentRunStatus.Completed, 0);
        Assert.Equal(AgentRunStatus.Completed, run.Status);

        // Stray second call must not downgrade to Failed
        registry.Complete("r1", AgentRunStatus.Failed, -1);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Push_UpdatesLastActivityAt_ToEventTimestamp()
    {
        var run = new AgentRun
        {
            RunId = "r1", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };

        var t = DateTime.UtcNow.AddSeconds(5);
        run.Push(new StreamEvent(t, "assistant", "heartbeat"));

        Assert.Equal(t, run.LastActivityAt);
    }

    [Fact]
    public void ReservedCompletion_KeepsRunActive_UntilPostRunOwnerReleasesIt()
    {
        var registry = new AgentRunRegistry();
        var run = new AgentRun
        {
            RunId = "deferred", ProjectSlug = "p", TicketId = 42,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };

        registry.ReserveCompletion(run.RunId);
        registry.Register(run);
        registry.Complete(run.RunId, AgentRunStatus.Completed, 0);

        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.Equal(AgentRunStatus.Completed, registry.EffectiveStatus(run.RunId));
        Assert.Contains(run, registry.ActiveForProject("p"));

        registry.ReleaseCompletion(run.RunId);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.DoesNotContain(run, registry.ActiveForProject("p"));
    }

    [Fact]
    public void Constructor_ReconcilesStaleLRunningSnapshots_ToStopped()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);

        // Persist a run that looks like it was still Running when the process died. The owner is
        // what makes it stale: pid 0 is what a pre-Plan-2.2 snapshot deserializes to, and no live
        // process ever answers to it. See AgentRunRegistryLoadTests for the full liveness rule.
        var staleRun = new AgentRun
        {
            RunId = "stale", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
            HostProcessId = 0, HostProcessStartTime = null,
        };
        // Status is Running (default) — simulate orphaned run
        store.Save(staleRun);

        var registry = new AgentRunRegistry(store);
        var loaded = registry.Get("stale");

        Assert.NotNull(loaded);
        Assert.Equal(AgentRunStatus.Stopped, loaded!.Status);
        Assert.NotNull(loaded.EndedAt);
    }

    /// <summary>
    /// A5: the 24h purge is the only place a run's usage leaves memory, and it also deletes the
    /// snapshot JSON — so PurgeOld must append the run's cost line to the durable
    /// <c>runs/costs.ndjson</c> ledger before the snapshot disappears.
    /// </summary>
    [Fact]
    public void PurgeOld_AppendsCostLedgerLine_BeforeDeletingRunJson()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        var registry = new AgentRunRegistry(store);

        var run = new AgentRun
        {
            RunId = "purged", ProjectSlug = "p", TicketId = 7,
            AgentName = "qa-tester", SkillFile = "qa-tester/SKILL.md",
            ConcurrencyGroup = "qa-tester", StartedAt = DateTime.UtcNow.AddDays(-2),
        };
        run.Model = "claude-haiku-4-5";
        run.AddUsage(100, 50, 10, 5, 0.25m);
        registry.Register(run);
        registry.Complete("purged", AgentRunStatus.Completed, 0);
        run.EndedAt = DateTime.UtcNow.AddHours(-48);

        registry.PurgeOld(TimeSpan.FromHours(24));

        Assert.Null(registry.Get("purged"));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "runs", "purged.json")));

        var ledgerPath = Path.Combine(tmp.Path, "runs", "costs.ndjson");
        Assert.True(File.Exists(ledgerPath), "PurgeOld must write the cost ledger before deleting the run JSON.");
        var line = Assert.Single(File.ReadAllLines(ledgerPath));

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("purged", root.GetProperty("runId").GetString());
        Assert.Equal("p", root.GetProperty("projectSlug").GetString());
        Assert.Equal(7, root.GetProperty("ticketId").GetInt32());
        Assert.Equal("qa-tester", root.GetProperty("agentName").GetString());
        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal(100, root.GetProperty("inputTokens").GetInt32());
        Assert.Equal(50, root.GetProperty("outputTokens").GetInt32());
        Assert.Equal(10, root.GetProperty("cacheReadTokens").GetInt32());
        Assert.Equal(5, root.GetProperty("cacheWriteTokens").GetInt32());
        Assert.Equal(0.25m, root.GetProperty("totalCostUsd").GetDecimal());
    }
}

[Collection("MockClaude")]
public class ClaudeRunnerPumpExceptionTests
{
    /// <summary>
    /// An OnEvent subscriber that throws must not leave the run in Running state.
    /// The runner must catch the exception from the pump and complete the run as Failed.
    /// </summary>
    [Fact]
    public async Task ThrowingEventSubscriber_RunEndsAsFailed_NotRunning()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("pump-throw-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "default");

        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(new SessionRegistry(), runs, new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 1,
            OnEventHook = _ => throw new InvalidOperationException("subscriber intentionally throws"),
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.NotEqual(AgentRunStatus.Running, run.Status);
    }
}

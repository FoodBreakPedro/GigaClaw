using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Plan 2.2 — what the store-backed <see cref="AgentRunRegistry"/> constructor does with a
/// <see cref="AgentRunStatus.Running"/> snapshot it did not write. This is the only place in the
/// system that decides a run's liveness, so the whole rule is pinned here.
/// <para>
/// The behavior is not cosmetic: a run left reading Running keeps
/// <see cref="AgentRunRegistry.HasActiveInGroup"/> true, which silently shuts its whole dispatch
/// lane — so every assertion pairs the status with the lock it must release or preserve.
/// </para>
/// </summary>
public class AgentRunRegistryLoadTests
{
    private static AgentRun MakeRun(
        string runId,
        string group,
        int hostPid,
        DateTime? hostStart = null,
        string slug = "p",
        int? ticketId = null) =>
        new()
        {
            RunId = runId,
            ProjectSlug = slug,
            TicketId = ticketId,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
            ConcurrencyGroup = group,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            HostProcessId = hostPid,
            HostProcessStartTime = hostStart,
        };

    /// <summary>Writes an in-flight snapshot the way a host that then crashed would have left it.</summary>
    private static void SeedRunning(RunLogStore store, AgentRun run) => store.Save(run);

    private static int ForeignPid => Environment.ProcessId == int.MaxValue ? 1 : Environment.ProcessId + 1;

    // ── Orphans ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_run_whose_host_is_gone_loads_as_Stopped_and_releases_its_concurrency_group()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        SeedRunning(store, MakeRun("orphan", "code", ForeignPid, DateTime.UtcNow.AddHours(-1), ticketId: 7));

        var runs = new AgentRunRegistry(new RunLogStore(tmp.Path), _ => false);

        var orphan = runs.Get("orphan")!;
        Assert.Equal(AgentRunStatus.Stopped, orphan.Status);
        Assert.NotNull(orphan.EndedAt);
        Assert.False(runs.HasActiveInGroup("p", "code"));
        Assert.Empty(runs.ActiveForTicket("p", 7));
        Assert.Empty(runs.AllActive());
    }

    [Fact]
    public void A_snapshot_with_no_recorded_owner_is_treated_as_dead()
    {
        // Pre-2.2 snapshots have no hostProcessId and deserialize to 0 — unknowable, hence dead.
        using var tmp = new TempDir();
        SeedRunning(new RunLogStore(tmp.Path), MakeRun("legacy", "code", hostPid: 0));

        var runs = new AgentRunRegistry(new RunLogStore(tmp.Path));

        Assert.Equal(AgentRunStatus.Stopped, runs.Get("legacy")!.Status);
        Assert.False(runs.HasActiveInGroup("p", "code"));
    }

    /// <summary>
    /// The pid-reuse case, which is the whole reason the start time is persisted beside the pid. A
    /// dead host's number gets handed to something else — an unrelated process, or this very one —
    /// and without the second half of the identity the orphan would look alive forever.
    /// </summary>
    [Fact]
    public void A_recycled_pid_does_not_resurrect_the_run_that_used_to_own_it()
    {
        using var tmp = new TempDir();
        SeedRunning(
            new RunLogStore(tmp.Path),
            // This process's pid, but a birth instant that is not this process's.
            MakeRun("recycled", "code", Environment.ProcessId, DateTime.UtcNow.AddDays(-3)));

        var runs = new AgentRunRegistry(new RunLogStore(tmp.Path));

        Assert.Equal(AgentRunStatus.Stopped, runs.Get("recycled")!.Status);
    }

    [Fact]
    public void A_pid_that_matches_but_carries_no_start_time_is_treated_as_dead()
    {
        using var tmp = new TempDir();
        SeedRunning(new RunLogStore(tmp.Path), MakeRun("halfKnown", "code", Environment.ProcessId, hostStart: null));

        var runs = new AgentRunRegistry(new RunLogStore(tmp.Path));

        Assert.Equal(AgentRunStatus.Stopped, runs.Get("halfKnown")!.Status);
    }

    [Fact]
    public void Reconciled_status_is_written_back_to_the_store()
    {
        using var tmp = new TempDir();
        SeedRunning(new RunLogStore(tmp.Path), MakeRun("orphan", "code", ForeignPid, DateTime.UtcNow.AddHours(-1)));

        _ = new AgentRunRegistry(new RunLogStore(tmp.Path), _ => false);

        var reloaded = new RunLogStore(tmp.Path).LoadAll().Single();
        Assert.Equal(AgentRunStatus.Stopped, reloaded.Status);
        Assert.NotNull(reloaded.EndedAt);
    }

    // ── Live hosts ────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression the conditional rule exists for: two GigaClaw instances may share a data dir
    /// (the devcheck launch config shares the main one), so a starting instance must not stamp
    /// Stopped over another instance's genuinely in-flight runs.
    /// </summary>
    [Fact]
    public void A_run_owned_by_a_still_running_host_is_left_alone()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        // Registering stamps this process's pid and start time — a host that is provably alive.
        var live = new AgentRunRegistry(store).Register(MakeRun2("live", "code", ticketId: 7));

        var reopened = new AgentRunRegistry(new RunLogStore(tmp.Path));

        Assert.Equal(AgentRunStatus.Running, reopened.Get(live.RunId)!.Status);
        Assert.True(reopened.HasActiveInGroup("p", "code"));
        Assert.Single(reopened.ActiveForTicket("p", 7));
    }

    [Fact]
    public void Already_terminal_runs_are_never_touched()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        var runs = new AgentRunRegistry(store);
        runs.Register(MakeRun2("done", "code"));
        runs.Complete("done", AgentRunStatus.Completed, 0);

        var reopened = new AgentRunRegistry(new RunLogStore(tmp.Path), _ => false);

        var done = reopened.Get("done")!;
        Assert.Equal(AgentRunStatus.Completed, done.Status);
        Assert.Equal(0, done.ExitCode);
    }

    [Fact]
    public void Loading_twice_is_idempotent()
    {
        using var tmp = new TempDir();
        SeedRunning(new RunLogStore(tmp.Path), MakeRun("orphan", "code", ForeignPid, DateTime.UtcNow.AddHours(-1)));

        _ = new AgentRunRegistry(new RunLogStore(tmp.Path), _ => false);
        var second = new AgentRunRegistry(new RunLogStore(tmp.Path), _ => false);

        Assert.Equal(AgentRunStatus.Stopped, second.Get("orphan")!.Status);
        Assert.Empty(second.AllActive());
    }

    [Fact]
    public void A_mixed_store_terminalizes_only_the_orphans()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        new AgentRunRegistry(store).Register(MakeRun2("live", "live-group"));
        SeedRunning(store, MakeRun("orphan", "dead-group", ForeignPid, DateTime.UtcNow.AddHours(-1)));

        var reopened = new AgentRunRegistry(new RunLogStore(tmp.Path));

        Assert.Equal(AgentRunStatus.Running, reopened.Get("live")!.Status);
        Assert.Equal(AgentRunStatus.Stopped, reopened.Get("orphan")!.Status);
        Assert.True(reopened.HasActiveInGroup("p", "live-group"));
        Assert.False(reopened.HasActiveInGroup("p", "dead-group"));
    }

    // ── Persistence: an orphan has to survive the crash to be reconcilable ────

    [Fact]
    public void A_run_is_persisted_the_moment_it_starts_so_a_crash_leaves_evidence()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);

        var registry = new AgentRunRegistry(store);
        var run = registry.Register(MakeRun2("in-flight", "code", ticketId: 7));
        registry.NoteProcessId(run.RunId, 4242);

        // Nothing completed the run — this is the crash. A fresh host reads the store back.
        var reloaded = new RunLogStore(tmp.Path).LoadAll().Single();
        Assert.Equal("in-flight", reloaded.RunId);
        Assert.Equal(AgentRunStatus.Running, reloaded.Status);
        Assert.Equal(Environment.ProcessId, reloaded.HostProcessId);
        Assert.NotNull(reloaded.HostProcessStartTime);
        Assert.Equal(4242, reloaded.ProcessId);
        Assert.Equal("code", reloaded.ConcurrencyGroup);
        Assert.Equal(7, reloaded.TicketId);
    }

    /// <summary>A run stamped by this process, i.e. with a host that is trivially alive.</summary>
    private static AgentRun MakeRun2(string runId, string group, int? ticketId = null) =>
        new()
        {
            RunId = runId,
            ProjectSlug = "p",
            TicketId = ticketId,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
            ConcurrencyGroup = group,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
        };
}

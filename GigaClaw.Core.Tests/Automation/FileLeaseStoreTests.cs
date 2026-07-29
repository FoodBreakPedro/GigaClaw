using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R4's durable SQLite lease store (doc/roadmap/lane-codex-runtime.md). Covers the acceptance bar
/// directly: disjoint scopes both proceed, intersecting scopes serialize onto exactly one winner,
/// stale leases from a crashed run are reaped and become reassignable, and the acquire/reap
/// sequence is safe under genuinely concurrent connections against the same project db file.
/// </summary>
public class FileLeaseStoreTests
{
    private static async Task<(TempDir tmp, ProjectService projects, string slug, FileLeaseStore store)> BuildAsync(
        string projectName = "lease-store-test")
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync(projectName);
        return (tmp, projects, project.Slug, new FileLeaseStore(projects));
    }

    [Fact]
    public async Task Two_concurrent_runs_with_disjoint_scopes_both_proceed()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var now = DateTime.UtcNow;

        var a = await store.AcquireAsync(slug, 1, "run-a", "programmer", ["src/**"], now, TimeSpan.FromMinutes(30));
        var b = await store.AcquireAsync(slug, 2, "run-b", "writer", ["docs/**"], now, TimeSpan.FromMinutes(30));

        Assert.True(a.IsAcquired);
        Assert.True(b.IsAcquired);
        Assert.Equal(2, (await store.ListActiveAsync(slug)).Count);
    }

    [Fact]
    public async Task Intersecting_scopes_serialize_the_second_conflicts_and_names_the_holder()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var now = DateTime.UtcNow;

        var a = await store.AcquireAsync(slug, 1, "run-a", "programmer", ["src/**"], now, TimeSpan.FromMinutes(30));
        var b = await store.AcquireAsync(slug, 2, "run-b", "code-janitor", ["src/api/**"], now, TimeSpan.FromMinutes(30));

        Assert.True(a.IsAcquired);
        Assert.False(b.IsAcquired);
        Assert.Equal(FileLeaseAcquireOutcome.Conflict, b.Outcome);
        Assert.Equal("run-a", b.ConflictingLease!.RunId);
        Assert.Equal("programmer", b.ConflictingLease.Agent);
        Assert.Equal(1, b.ConflictingLease.TicketId);
        // Only the winner is active — a conflict must never leave a partial row behind.
        Assert.Single(await store.ListActiveAsync(slug));
    }

    [Fact]
    public async Task Acquire_for_the_same_runId_is_idempotent_not_a_self_conflict()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var now = DateTime.UtcNow;

        var first = await store.AcquireAsync(slug, 1, "run-a", "programmer", ["src/**"], now, TimeSpan.FromMinutes(30));
        var retry = await store.AcquireAsync(slug, 1, "run-a", "programmer", ["src/**"], now, TimeSpan.FromMinutes(30));

        Assert.True(first.IsAcquired);
        Assert.True(retry.IsAcquired);
        Assert.Equal(first.Lease!.LeaseId, retry.Lease!.LeaseId);
        Assert.Single(await store.ListActiveAsync(slug));
    }

    [Fact]
    public async Task A_stale_lease_from_a_crashed_run_is_reaped_and_the_scope_becomes_reassignable()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var acquiredAt = DateTime.UtcNow.AddHours(-2);
        var now = DateTime.UtcNow;

        // Short TTL relative to `acquiredAt` simulates a run that took the lease and then crashed
        // without ever releasing it.
        var crashed = await store.AcquireAsync(slug, 1, "run-crashed", "programmer", ["src/**"], acquiredAt, TimeSpan.FromMinutes(5));
        Assert.True(crashed.IsAcquired);

        var reaped = await store.ReapStaleAsync(slug, now);

        Assert.Single(reaped);
        Assert.Equal("run-crashed", reaped[0].RunId);
        Assert.Empty(await store.ListActiveAsync(slug));

        // Reassignable: the freed scope can now be leased by someone else.
        var next = await store.AcquireAsync(slug, 2, "run-next", "code-janitor", ["src/**"], now, TimeSpan.FromMinutes(30));
        Assert.True(next.IsAcquired);
    }

    [Fact]
    public async Task Acquire_reaps_expired_leases_inline_so_a_freed_scope_is_immediately_reassignable()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var acquiredAt = DateTime.UtcNow.AddHours(-2);
        var now = DateTime.UtcNow;

        await store.AcquireAsync(slug, 1, "run-crashed", "programmer", ["src/**"], acquiredAt, TimeSpan.FromMinutes(5));

        // No explicit ReapStaleAsync call here: Acquire itself reaps inside its own transaction.
        var next = await store.AcquireAsync(slug, 2, "run-next", "code-janitor", ["src/**"], now, TimeSpan.FromMinutes(30));

        Assert.True(next.IsAcquired);
        Assert.Single(await store.ListActiveAsync(slug));
    }

    [Fact]
    public async Task Release_then_reacquire_succeeds_for_a_different_run()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var now = DateTime.UtcNow;

        var a = await store.AcquireAsync(slug, 1, "run-a", "programmer", ["src/**"], now, TimeSpan.FromMinutes(30));
        Assert.True(a.IsAcquired);

        var blocked = await store.AcquireAsync(slug, 2, "run-b", "code-janitor", ["src/**"], now, TimeSpan.FromMinutes(30));
        Assert.False(blocked.IsAcquired);

        await store.ReleaseAsync(slug, "run-a", now);
        Assert.Empty(await store.ListActiveAsync(slug));

        var b = await store.AcquireAsync(slug, 2, "run-b", "code-janitor", ["src/**"], now, TimeSpan.FromMinutes(30));
        Assert.True(b.IsAcquired);
    }

    [Fact]
    public async Task Release_for_a_run_that_never_held_a_lease_is_a_harmless_no_op()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        await store.ReleaseAsync(slug, "never-acquired", DateTime.UtcNow);
        Assert.Empty(await store.ListActiveAsync(slug));
    }

    /// <summary>
    /// Acquire/reap race, transaction-tested with genuinely concurrent connections: N tasks each
    /// call <see cref="FileLeaseStore.AcquireAsync"/> for the same overlapping scope, and M tasks
    /// concurrently call <see cref="FileLeaseStore.ReapStaleAsync"/> against a lease seeded as
    /// already stale — all against real, independently-opened <c>SqliteConnection</c>s to the same
    /// project db file. BEGIN IMMEDIATE plus the busy_timeout configured in
    /// <see cref="FileLeaseStore"/> must serialize every writer so exactly one acquirer ever wins,
    /// regardless of how the acquires and reaps interleave.
    /// </summary>
    [Fact]
    public async Task AcquireReapRace_concurrent_connections_never_double_grant_an_overlapping_scope()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        var now = DateTime.UtcNow;

        // Seed a lease that is already stale as of `now`, so both the inline reap inside Acquire
        // and the standalone ReapStaleAsync calls race to clear it.
        var seeded = await store.AcquireAsync(
            slug, 1, "run-seed", "programmer", ["src/**"], now.AddHours(-1), TimeSpan.FromMinutes(1));
        Assert.True(seeded.IsAcquired);

        var acquireTasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => store.AcquireAsync(
                slug, 2, $"run-{i}", "code-janitor", new[] { "src/**" }, now, TimeSpan.FromMinutes(30))))
            .ToArray();
        var reapTasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => store.ReapStaleAsync(slug, now)))
            .ToArray();

        await Task.WhenAll(acquireTasks.Cast<Task>().Concat(reapTasks.Cast<Task>()));

        var acquireResults = acquireTasks.Select(t => t.Result).ToList();
        Assert.Equal(1, acquireResults.Count(r => r.IsAcquired));
        Assert.Equal(7, acquireResults.Count(r => r.Outcome == FileLeaseAcquireOutcome.Conflict));

        // Never more than the single winner's lease is visible as active, at any point after the
        // race settles — the seeded stale lease is gone and no acquirer double-granted.
        var active = await store.ListActiveAsync(slug);
        Assert.Single(active);
        Assert.Contains(active[0].RunId, acquireResults.Where(r => r.IsAcquired).Select(r => r.Lease!.RunId));
    }

    [Fact]
    public async Task Acquire_rejects_an_empty_scope()
    {
        var (tmp, _, slug, store) = await BuildAsync();
        using var tmpDisposer = tmp;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AcquireAsync(slug, 1, "run-a", "programmer", Array.Empty<string>(), DateTime.UtcNow, TimeSpan.FromMinutes(30)));
    }
}

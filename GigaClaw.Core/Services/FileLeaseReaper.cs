using GigaClaw.Core.Automation.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Services;

/// <summary>
/// Durable reaper for R4 file-ownership leases (doc/roadmap/lane-codex-runtime.md, Task R4).
///
/// Modeled on <see cref="ConcurrencyLockReaper"/>'s poll-loop shape, but it solves a different
/// problem and does not reuse that reaper's store: <see cref="ConcurrencyLockReaper"/>
/// force-releases an in-memory <see cref="Automation.AgentRun"/> concurrency-group lock, which
/// lives in a <see cref="Automation.AgentRunRegistry"/> that a process restart already wipes for
/// free — there is nothing left to reap after a crash because the lock never survived it. A file
/// lease is durable SQLite state precisely so it *does* survive a crash, which means the opposite
/// failure mode applies: nothing in this process's memory will ever notice a lease whose run died
/// mid-flight unless something scans the store itself. This service is that something — on each
/// tick it asks <see cref="FileLeaseStore.ReapStaleAsync"/> to reap every project's leases past
/// their TTL, independent of whether the run that took them is known to this process at all.
/// </summary>
public sealed class FileLeaseReaper : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly FileLeaseStore _leases;
    private readonly ILogger<FileLeaseReaper> _logger;

    // Coarser than ConcurrencyLockReaper's 30s: a file lease's TTL (FileLeaseStore.DefaultTtl,
    // 45 minutes) is a crash-safety backstop, not a tight dead man's switch, so a slower cadence
    // costs nothing observable while scanning every project's lease table less often.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    public FileLeaseReaper(ProjectService projects, FileLeaseStore leases, ILogger<FileLeaseReaper> logger)
    {
        _projects = projects;
        _leases = leases;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileLeaseReaper started (poll every {Seconds}s)", PollInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReapAllAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "FileLeaseReaper tick failed"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reaps every project's stale leases and returns what was reaped. <paramref name="now"/> is
    /// injected so tests are deterministic; a single project whose reap fails (e.g. a locked file)
    /// is logged and skipped rather than aborting the rest of the sweep.
    /// </summary>
    internal async Task<IReadOnlyList<FileLease>> ReapAllAsync(DateTime now, CancellationToken ct)
    {
        var reaped = new List<FileLease>();
        foreach (var project in await _projects.ListProjectsAsync())
        {
            IReadOnlyList<FileLease> stale;
            try
            {
                stale = await _leases.ReapStaleAsync(project.Slug, now, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileLeaseReaper: failed to reap project {Slug}", project.Slug);
                continue;
            }

            foreach (var lease in stale)
            {
                _logger.LogWarning(
                    "FileLeaseReaper: reaped stale lease leaseId={LeaseId} run={RunId} agent={Agent} ticket=#{Ticket} project={Slug} (expired {ExpiresAt:u})",
                    lease.LeaseId, lease.RunId, lease.Agent, lease.TicketId, project.Slug, lease.ExpiresAtUtc);
                reaped.Add(lease);
            }
        }
        return reaped;
    }
}

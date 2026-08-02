using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace GigaClaw.Core.Automation;

public enum AgentRunStatus { Running, Completed, Failed, Stopped }

public sealed class AgentRun
{
    public required string RunId { get; init; }
    public required string ProjectSlug { get; init; }
    public required int? TicketId { get; init; }
    public required string AgentName { get; init; }
    public required string SkillFile { get; init; }
    public required string ConcurrencyGroup { get; init; }
    public required DateTime StartedAt { get; init; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public string? ChatTarget { get; set; }
    /// <summary>Execution backend. Existing runs and Claude CLI runs use "claude".</summary>
    public string Backend { get; set; } = "claude";
    /// <summary>Provider-owned run id, when the work executes outside GigaClaw.</summary>
    public string? ExternalRunId { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>
    /// Plan 2.2 — id of the GigaClaw host process that owns this run, stamped at construction and
    /// persisted with the snapshot. Together with <see cref="HostProcessStartTime"/> this is the
    /// liveness signal, and the only one: a loaded run is alive if and only if the process that
    /// registered it is still running. The claude subprocess's own pid (<see cref="ProcessId"/>)
    /// cannot answer that question — it is null until the spawn succeeds, it is recycled by the OS,
    /// and a surviving orphan subprocess is still a dead run because the host lost its stdout pipe
    /// and can never complete it. See <see cref="AgentRunRegistry.IsHostAlive"/> for the rule.
    /// </summary>
    public int HostProcessId { get; init; } = Environment.ProcessId;

    /// <summary>
    /// Start time (UTC) of the host process named by <see cref="HostProcessId"/>. A pid alone is
    /// not an identity — the OS recycles it, so an unrelated process wearing a dead host's number
    /// would make an orphaned run look alive forever. The pair is unique for as long as it matters.
    /// Null on a snapshot written before this field existed: unknowable, therefore dead.
    /// </summary>
    public DateTime? HostProcessStartTime { get; init; } = AgentRunRegistry.CurrentHostStartTime;

    /// <summary>
    /// Pid of the claude subprocess once spawned, recorded for diagnosis only — a reconciled run
    /// names the process an operator may still have to kill by hand on platforms without
    /// <see cref="ProcessJobObject"/>'s kill-on-close containment (i.e. everything but Windows).
    /// Never used as a liveness signal: see <see cref="HostProcessId"/>.
    /// </summary>
    public int? ProcessId { get; set; }

    // Token usage accumulated from the claude CLI's terminal `result` events. A single AgentRun
    // can spawn several subprocesses (resume retry, quota fallback, chat steer replay), each
    // emitting its own result event, so these are sums — not the last event's values.
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }
    public decimal? TotalCostUsd { get; private set; }
    public long TotalTokens => (long)InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    public bool HasUsage => TotalTokens > 0 || TotalCostUsd is not null;

    public void AddUsage(int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens, decimal? costUsd)
    {
        lock (_logLock)
        {
            InputTokens += inputTokens;
            OutputTokens += outputTokens;
            CacheReadTokens += cacheReadTokens;
            CacheWriteTokens += cacheWriteTokens;
            if (costUsd is not null) TotalCostUsd = (TotalCostUsd ?? 0m) + costUsd.Value;
        }
    }

    /// <summary>Minutes of inactivity after which the concurrency-lock reaper force-releases this run's
    /// group (dead man's switch). Null disables the timeout. Set from the automation's
    /// <c>lockTimeoutMinutes</c> via <see cref="ClaudeRunContext.LockTimeoutMinutes"/>.</summary>
    public int? LockTimeoutMinutes { get; init; }

    /// <summary>UTC timestamp of the last StreamEvent pushed onto this run — its heartbeat. Updated on
    /// every <see cref="Push"/>. The reaper compares <c>now - LastActivityAt</c> against
    /// <see cref="LockTimeoutMinutes"/> to detect a hung run whose subprocess stopped emitting.</summary>
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    private readonly object _logLock = new();
    private readonly LinkedList<StreamEvent> _buffer = new();
    private const int MaxBuffer = 500;

    public Channel<string> SteeringQueue { get; } = Channel.CreateUnbounded<string>();
    public CancellationTokenSource Cancellation { get; } = new();
    public bool IsAwaitingUserAnswer { get; set; }
    public event Action<StreamEvent>? OnEvent;

    private readonly List<string> _pendingSteerMessages = new();
    public IReadOnlyList<string> PendingSteerMessages => _pendingSteerMessages;

    public void AddPendingSteerMessage(string msg)
    {
        lock (_logLock) _pendingSteerMessages.Add(msg);
    }

    public IReadOnlyList<string> DrainPendingSteerMessages()
    {
        lock (_logLock)
        {
            var result = _pendingSteerMessages.ToList();
            _pendingSteerMessages.Clear();
            return result;
        }
    }

    public IReadOnlyList<StreamEvent> SnapshotBuffer()
    {
        lock (_logLock) return _buffer.ToList();
    }

    public void Push(StreamEvent ev)
    {
        lock (_logLock)
        {
            _buffer.AddLast(ev);
            while (_buffer.Count > MaxBuffer) _buffer.RemoveFirst();
            LastActivityAt = ev.At;
        }
        OnEvent?.Invoke(ev);
    }
}

public sealed record StreamEvent(DateTime At, string Kind, string Text, string? Detail = null);

/// <summary>Serializable snapshot of a completed AgentRun for disk persistence.</summary>
public sealed class AgentRunSnapshot
{
    public string RunId { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public int? TicketId { get; set; }
    public string AgentName { get; set; } = "";
    public string SkillFile { get; set; } = "";
    public string ConcurrencyGroup { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public string? ChatTarget { get; set; }
    public string Backend { get; set; } = "claude";
    public string? ExternalRunId { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentRunStatus Status { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>See <see cref="AgentRun.HostProcessId"/>. A snapshot written before Plan 2.2 has no
    /// such field and deserializes to 0, which matches no live process — an unknowable owner is
    /// treated as dead, never as alive.</summary>
    public int HostProcessId { get; set; }
    /// <summary>See <see cref="AgentRun.HostProcessStartTime"/>. Absent on pre-Plan-2.2 snapshots,
    /// which therefore read as dead rather than as owned by whatever wears the pid today.</summary>
    public DateTime? HostProcessStartTime { get; set; }
    /// <summary>See <see cref="AgentRun.ProcessId"/>. Diagnostic only.</summary>
    public int? ProcessId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public decimal? TotalCostUsd { get; set; }
    public List<StreamEvent> Events { get; set; } = [];
    public List<string> PendingSteerMessages { get; set; } = [];
}

/// <summary>Persists completed runs as JSON files on disk.</summary>
public sealed class RunLogStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RunLogStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(_dir);
    }

    public void Save(AgentRun run)
    {
        var snapshot = new AgentRunSnapshot
        {
            RunId = run.RunId,
            ProjectSlug = run.ProjectSlug,
            TicketId = run.TicketId,
            AgentName = run.AgentName,
            SkillFile = run.SkillFile,
            ConcurrencyGroup = run.ConcurrencyGroup,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            SessionId = run.SessionId,
            Model = run.Model,
            ChatTarget = run.ChatTarget,
            Backend = run.Backend,
            ExternalRunId = run.ExternalRunId,
            Status = run.Status,
            ExitCode = run.ExitCode,
            HostProcessId = run.HostProcessId,
            HostProcessStartTime = run.HostProcessStartTime,
            ProcessId = run.ProcessId,
            InputTokens = run.InputTokens,
            OutputTokens = run.OutputTokens,
            CacheReadTokens = run.CacheReadTokens,
            CacheWriteTokens = run.CacheWriteTokens,
            TotalCostUsd = run.TotalCostUsd,
            Events = run.SnapshotBuffer().ToList(),
            PendingSteerMessages = run.PendingSteerMessages.ToList(),
        };
        var path = Path.Combine(_dir, $"{run.RunId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, s_json));
    }

    public void Delete(string runId)
    {
        var path = Path.Combine(_dir, $"{runId}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private readonly object _ledgerLock = new();

    /// <summary>
    /// A5: appends one NDJSON line of usage data per purged run to <c>runs/costs.ndjson</c>,
    /// called by <see cref="AgentRunRegistry.PurgeOld"/> immediately before <see cref="Delete"/>
    /// removes the full snapshot. The registry purges every run older than 24h, so without this
    /// ledger no question beyond a day of spend ("what did qa-tester cost last week?") is
    /// answerable from data. Append-only, never rewritten, never read by the engine.
    /// Failures are swallowed: the ledger is telemetry and must never block the purge.
    /// </summary>
    public void AppendCostLedger(AgentRun run)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                runId = run.RunId,
                projectSlug = run.ProjectSlug,
                ticketId = run.TicketId,
                agentName = run.AgentName,
                model = run.Model,
                backend = run.Backend,
                startedAt = run.StartedAt,
                endedAt = run.EndedAt,
                status = run.Status.ToString(),
                exitCode = run.ExitCode,
                inputTokens = run.InputTokens,
                outputTokens = run.OutputTokens,
                cacheReadTokens = run.CacheReadTokens,
                cacheWriteTokens = run.CacheWriteTokens,
                totalCostUsd = run.TotalCostUsd,
            }, s_json);
            lock (_ledgerLock)
                File.AppendAllText(Path.Combine(_dir, "costs.ndjson"), line + "\n");
        }
        catch
        {
            // Telemetry only — losing one ledger line is better than a purge that never completes.
        }
    }

    public IEnumerable<AgentRun> LoadAll()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            AgentRunSnapshot? snapshot;
            try
            {
                var json = File.ReadAllText(file);
                snapshot = JsonSerializer.Deserialize<AgentRunSnapshot>(json, s_json);
            }
            catch { continue; }
            if (snapshot is null) continue;

            var run = new AgentRun
            {
                RunId = snapshot.RunId,
                ProjectSlug = snapshot.ProjectSlug,
                TicketId = snapshot.TicketId,
                AgentName = snapshot.AgentName,
                SkillFile = snapshot.SkillFile,
                ConcurrencyGroup = snapshot.ConcurrencyGroup,
                StartedAt = snapshot.StartedAt,
                HostProcessId = snapshot.HostProcessId,
                HostProcessStartTime = snapshot.HostProcessStartTime,
            };
            run.ProcessId = snapshot.ProcessId;
            run.SessionId = snapshot.SessionId;
            run.Model = snapshot.Model;
            run.ChatTarget = snapshot.ChatTarget;
            run.Backend = string.IsNullOrWhiteSpace(snapshot.Backend) ? "claude" : snapshot.Backend;
            run.ExternalRunId = snapshot.ExternalRunId;
            run.Status = snapshot.Status;
            run.EndedAt = snapshot.EndedAt;
            run.ExitCode = snapshot.ExitCode;
            run.AddUsage(snapshot.InputTokens, snapshot.OutputTokens,
                snapshot.CacheReadTokens, snapshot.CacheWriteTokens, snapshot.TotalCostUsd);
            foreach (var ev in snapshot.Events)
                run.Push(ev);
            foreach (var msg in snapshot.PendingSteerMessages)
                run.AddPendingSteerMessage(msg);
            yield return run;
        }
    }
}

public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<string, AgentRun> _runs = new();
    private readonly ConcurrentDictionary<string, byte> _deferredCompletions = new();
    private readonly ConcurrentDictionary<string, (AgentRunStatus Status, int? ExitCode)> _pendingCompletions = new();
    private readonly RunLogStore? _store;

    public event Action<AgentRun>? OnRunStarted;
    public event Action<AgentRun>? OnRunEnded;

    /// <summary>
    /// Start time (UTC) of this process, read once. Stamped onto every run alongside
    /// <see cref="Environment.ProcessId"/> so a later host can tell "the owner is still running"
    /// from "something else now wears that pid". Unreadable on a locked-down host — then no run
    /// this process writes can ever be proven alive, which is the safe direction.
    /// </summary>
    internal static readonly DateTime? CurrentHostStartTime = ReadCurrentHostStartTime();

    private static DateTime? ReadCurrentHostStartTime()
    {
        try { using var self = System.Diagnostics.Process.GetCurrentProcess(); return self.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    /// <summary>
    /// The one liveness rule in the system. A run loaded from disk is alive only while the host
    /// process that registered it is still running — identified by pid <em>and</em> start time,
    /// because a pid on its own is recycled and an unrelated process wearing a dead host's number
    /// would keep an orphaned run "Running" (and its concurrency group shut) forever. Anything
    /// unknowable — pid 0 from a pre-Plan-2.2 snapshot, a missing start time, a process the OS will
    /// not describe — reads as dead: terminalizing a live run costs one re-dispatch, while
    /// resurrecting a dead one deadlocks a dispatch lane until someone restarts the app.
    /// </summary>
    public static bool IsHostAlive(AgentRun run)
    {
        if (run.HostProcessId <= 0 || run.HostProcessStartTime is not DateTime startedAt) return false;
        try
        {
            using var host = System.Diagnostics.Process.GetProcessById(run.HostProcessId);
            if (host.HasExited) return false;
            // Same pid AND same birth instant: the clocks agree to the second at best, so compare
            // with a tolerance rather than for equality.
            return Math.Abs((host.StartTime.ToUniversalTime() - startedAt).TotalSeconds) <= 2;
        }
        catch
        {
            // No such process, or the OS refuses to describe it. Either way this host cannot prove
            // the run is alive, and an unprovable run is treated as orphaned.
            return false;
        }
    }

    public AgentRunRegistry() { }

    public AgentRunRegistry(RunLogStore store) : this(store, null) { }

    /// <summary>
    /// Loads persisted runs and terminalizes the orphans among them. Runs are persisted at
    /// <see cref="Register"/>, not only at completion (Plan 2.2), so a host that dies mid-dispatch
    /// leaves a <see cref="AgentRunStatus.Running"/> snapshot behind. That snapshot is not
    /// cosmetic: <see cref="HasActiveInGroup"/> and <see cref="ActiveForTicket"/> key off it, so one
    /// stale record holds a concurrency group — and its agent's whole dispatch lane — shut for as
    /// long as this process lives. Marking it Stopped here releases both.
    /// <para>
    /// It is a <em>conditional</em> reconciliation, not a blanket one: several GigaClaw instances
    /// can share a data dir (the devcheck launch config shares the main one), and a blanket rule
    /// would let a starting instance stamp Stopped over another instance's genuinely in-flight
    /// runs. <paramref name="isHostAlive"/> overrides the probe for tests; production always uses
    /// <see cref="IsHostAlive"/>.
    /// </para>
    /// </summary>
    public AgentRunRegistry(RunLogStore store, Func<AgentRun, bool>? isHostAlive)
    {
        _store = store;
        var alive = isHostAlive ?? IsHostAlive;
        foreach (var run in store.LoadAll())
        {
            if (run.Status == AgentRunStatus.Running && !alive(run))
            {
                run.Status = AgentRunStatus.Stopped;
                run.EndedAt = DateTime.UtcNow;
                store.Save(run);
            }
            _runs[run.RunId] = run;
        }
    }

    public AgentRun Register(AgentRun run)
    {
        _runs[run.RunId] = run;
        // Plan 2.2: persist the run the moment it starts, not only when it ends. Until now a host
        // that died mid-dispatch left no trace of the run at all — it simply vanished from history,
        // and nothing downstream could tell "never happened" from "crashed halfway". The in-flight
        // snapshot is what makes an orphan detectable (and reconcilable) after a restart.
        Persist(run);
        OnRunStarted?.Invoke(run);
        return run;
    }

    /// <summary>
    /// Writes the run's current state to the log store, if one is configured. Called at
    /// registration, whenever a durable field changes mid-run (the subprocess pid), and at
    /// completion. A no-op for registries built without a store, as the tests' are.
    /// </summary>
    public void Persist(AgentRun run)
    {
        try { _store?.Save(run); }
        catch { /* run-log persistence is best-effort; never fail a dispatch over it */ }
    }

    /// <summary>Records the claude subprocess pid on a live run and persists it. Diagnostic only —
    /// see <see cref="AgentRun.ProcessId"/>.</summary>
    public void NoteProcessId(string runId, int processId)
    {
        if (!_runs.TryGetValue(runId, out var run)) return;
        run.ProcessId = processId;
        Persist(run);
    }

    public void Complete(string runId, AgentRunStatus status, int? exitCode)
    {
        if (!_runs.TryGetValue(runId, out var run)) return;

        // Automation action chains reserve their run before dispatch. The subprocess may
        // finish before trigger-state commit and post-run actions do; keep the run visibly
        // Running until the owner releases the reservation so ActiveForProject(...).Any()
        // is a reliable "the whole chain is done" signal.
        if (_deferredCompletions.ContainsKey(runId))
        {
            _pendingCompletions.TryAdd(runId, (status, exitCode));
            return;
        }

        ApplyCompletion(run, status, exitCode);
    }

    private void ApplyCompletion(AgentRun run, AgentRunStatus status, int? exitCode)
    {
        // Idempotent: a terminal status must never be downgraded by a stray second call.
        if (run.Status != AgentRunStatus.Running) return;
        run.Status = status;
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = exitCode;
        _store?.Save(run);
        OnRunEnded?.Invoke(run);
    }

    /// <summary>
    /// Defers the terminal registry transition for an automation-owned run until its
    /// trigger commit and post-run action chain have completed.
    /// </summary>
    internal void ReserveCompletion(string runId) => _deferredCompletions.TryAdd(runId, 0);

    /// <summary>Returns the subprocess outcome even while its registry completion is deferred.</summary>
    internal AgentRunStatus EffectiveStatus(string runId)
    {
        if (_pendingCompletions.TryGetValue(runId, out var pending))
            return pending.Status;
        return _runs.TryGetValue(runId, out var run) ? run.Status : AgentRunStatus.Failed;
    }

    internal int? EffectiveExitCode(string runId)
    {
        if (_pendingCompletions.TryGetValue(runId, out var pending))
            return pending.ExitCode;
        return _runs.TryGetValue(runId, out var run) ? run.ExitCode : -1;
    }

    /// <summary>
    /// Releases an automation-owned run after all trigger/post-run work. Any terminal
    /// subprocess result captured by <see cref="Complete"/> becomes observable atomically.
    /// </summary>
    internal void ReleaseCompletion(string runId)
    {
        _deferredCompletions.TryRemove(runId, out _);
        if (_pendingCompletions.TryRemove(runId, out var pending)
            && _runs.TryGetValue(runId, out var run))
        {
            ApplyCompletion(run, pending.Status, pending.ExitCode);
        }
    }

    public AgentRun? Get(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    public IEnumerable<AgentRun> ActiveForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.Status == AgentRunStatus.Running);

    /// <summary>All currently-Running runs across every project. Used by the concurrency-lock reaper.</summary>
    public IEnumerable<AgentRun> AllActive() =>
        _runs.Values.Where(r => r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> ActiveForTicket(string projectSlug, int ticketId) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.TicketId == ticketId && r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> AllForTicket(string projectSlug, int ticketId) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.TicketId == ticketId);

    public IEnumerable<AgentRun> AllForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug);

    public bool HasActiveInGroup(string projectSlug, string concurrencyGroup) =>
        _runs.Values.Any(r => r.ProjectSlug == projectSlug && r.ConcurrencyGroup == concurrencyGroup && r.Status == AgentRunStatus.Running);

    public bool HasActiveAny(string projectSlug, IEnumerable<string> concurrencyGroups)
    {
        var set = new HashSet<string>(concurrencyGroups);
        return _runs.Values.Any(r => r.ProjectSlug == projectSlug && set.Contains(r.ConcurrencyGroup) && r.Status == AgentRunStatus.Running);
    }

    public void Remove(string runId)
    {
        _deferredCompletions.TryRemove(runId, out _);
        _pendingCompletions.TryRemove(runId, out _);
        _runs.TryRemove(runId, out _);
    }

    public AgentRun? LastCompletedForChatTarget(string projectSlug, string chatTarget) =>
        _runs.Values
            .Where(r => r.ProjectSlug == projectSlug && r.ChatTarget == chatTarget && r.Status != AgentRunStatus.Running && r.EndedAt is not null)
            .MaxBy(r => r.EndedAt);

    /// <summary>Purge runs that ended more than N minutes ago. The purge is the only place a
    /// run's usage leaves memory, so each purged run is appended to the durable cost ledger
    /// (<see cref="RunLogStore.AppendCostLedger"/>) before its snapshot JSON is deleted.</summary>
    public void PurgeOld(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        foreach (var r in _runs.Values.Where(r => r.Status != AgentRunStatus.Running && r.EndedAt is not null && r.EndedAt < cutoff).ToList())
        {
            // The TryRemove result gates the ledger append: two overlapping purges must not
            // record the same run's cost twice.
            if (!_runs.TryRemove(r.RunId, out _)) continue;
            _store?.AppendCostLedger(r);
            _store?.Delete(r.RunId);
        }
    }
}

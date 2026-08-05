namespace GigaClaw.Core.Automation.Runners;

/// <summary>
/// Host-neutral contract for dispatching an agent and observing its run (doc/roadmap/lane-codex-runtime.md,
/// Task R7). <see cref="ClaudeRunner"/> and <see cref="CodexRunner"/> implement it for their respective
/// CLIs. This is the narrow adapter boundary between run consumers (ActionExecutor, AutomationEngine,
/// DashboardRefreshService, chat/run endpoints) and a specific agent host — it exposes exactly what those
/// consumers use today via <see cref="ClaudeRunContext"/> and <see cref="AgentRun"/>, and nothing
/// speculative beyond the two proven CLI hosts.
///
/// A single <see cref="RunAsync"/> call implicitly covers every capability an agent host must provide:
/// dispatch (spawning/containing the underlying process so it cannot outlive the run), normalized stream
/// events (pushed onto the returned <see cref="AgentRun"/> via <see cref="AgentRun.Push"/> /
/// <see cref="AgentRun.OnEvent"/> regardless of the host's native wire format), session resume/restart
/// (keyed off <see cref="ClaudeRunContext.SessionScope"/> / <c>PersistSession</c> /
/// <c>RetryOnResumeFailure</c>), queued steering delivery (<see cref="AgentRun.SteeringQueue"/> and
/// <see cref="ClaudeRunContext.PendingSteerMessages"/>), per-run usage/cost when the host reports it
/// (<see cref="AgentRun.AddUsage"/>), and policy hook injection (the implementation is responsible for
/// wiring <c>ContractPolicy</c> enforcement into the dispatched process via <c>PolicyHookRunSession</c>).
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Dispatches an agent per <paramref name="ctx"/> and returns once the run has reached a terminal
    /// status (Completed/Failed/Stopped). The returned <see cref="AgentRun"/> is registered and live from
    /// the moment dispatch begins — callers may subscribe to its stream (<see cref="AgentRun.OnEvent"/>) or
    /// snapshot it (<see cref="AgentRun.SnapshotBuffer"/>) concurrently with awaiting this task.
    /// </summary>
    Task<AgentRun> RunAsync(ClaudeRunContext ctx, CancellationToken ct);
}

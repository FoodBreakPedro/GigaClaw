using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>
/// A trigger decides when an automation's actions fire, and provides the context
/// (ticket id, title, status) for each firing.
/// </summary>
public interface ITrigger
{
    /// <summary>
    /// Evaluate the trigger against current project state.
    /// Return one entry per dispatch that should happen this tick.
    /// </summary>
    Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct);

    /// <summary>
    /// Called by the engine after the firing's full action chain completes successfully.
    /// Triggers that keep persistent "seen" state (snapshot, debounce) should persist
    /// it here so a firing skipped by a transient gate or ending in failure is retried
    /// instead of being silently dropped.
    /// </summary>
    Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null) => Task.CompletedTask;

    /// <summary>
    /// Called exactly once when an action chain reaches a terminal outcome. The default
    /// preserves the historical contract: successful chains commit trigger state, while
    /// failed/stopped chains remain eligible for retry. Triggers with bounded retry state
    /// can override this method to record both outcomes.
    /// </summary>
    Task CompleteFiringAsync(
        TriggerContext ctx,
        TriggerFiring firing,
        bool succeeded,
        DateTime? completedAt = null) =>
        succeeded ? CommitFiringAsync(ctx, firing, completedAt) : Task.CompletedTask;

    /// <summary>
    /// Called by the engine when an external signal is pushed via
    /// <c>AutomationEngine.NotifySignal</c>. Event-driven triggers can inspect
    /// <paramref name="signal"/> and immediately produce firings without waiting
    /// for the next poll cycle. Purely time-based triggers (Interval, BoardIdle,
    /// AgentInactivity) can keep the default no-op implementation.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the trigger handled the signal and produced at least one
    /// firing; <c>false</c> to ignore (default).
    /// </returns>
    bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        firings = Array.Empty<TriggerFiring>();
        return false;
    }

    /// <summary>
    /// Returns the next UTC time this trigger is expected to fire, or null for purely
    /// event-driven triggers where the next occurrence cannot be predicted.
    /// </summary>
    DateTime? GetNextRunAt(DateTime now) => null;
}

public sealed class TriggerContext
{
    public required string ProjectSlug { get; init; }
    public required string WorkspacePath { get; init; }
    public required Automation Automation { get; init; }
    public required TicketService Tickets { get; init; }
    public required MemberService Members { get; init; }
    public required SessionRegistry Sessions { get; init; }
    public required AgentRunRegistry Runs { get; init; }
    public required DateTime Now { get; init; }
}

public sealed record TriggerFiring(int? TicketId, string? TicketTitle, string? TicketStatus);

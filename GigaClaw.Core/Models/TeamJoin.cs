namespace GigaClaw.Core.Models;

/// <summary>
/// What a <see cref="TeamJoinPolicy"/> says about the tasks of a run at one point in time.
/// <para>
/// <see cref="Fires"/> answers "stop waiting?"; <see cref="Success"/> answers "did the run get what
/// it asked for?". They are separate questions: a quorum join fires <i>and</i> succeeds while two
/// lanes are still open, and an all-done join fires without succeeding when a lane failed.
/// </para>
/// <see cref="Missing"/> is every task that is not <see cref="TeamTaskStatus.Done"/> at that moment —
/// failed, cancelled, or still open — because that is exactly the set a synthesizer must be told
/// about. A synthesis that silently drops a lane is the failure mode this record exists to prevent.
/// </summary>
public sealed record TeamJoinVerdict(
    bool Fires,
    bool Success,
    string Reason,
    IReadOnlyList<TeamTask> Reported,
    IReadOnlyList<TeamTask> Missing,
    IReadOnlyList<TeamTask> StillOpen)
{
    public static TeamJoinVerdict Waiting(string reason) => new(false, false, reason, [], [], []);
}

/// <summary>
/// The join decision, as a pure function of a policy and the run's task rows. I/O-free on purpose,
/// like <see cref="TeamDefinition.Validate"/>: the reconcile pass, a future UI and the tests share
/// one verdict instead of three readings of the same three words.
/// </summary>
public static class TeamJoinEvaluator
{
    public static TeamJoinVerdict Evaluate(TeamJoinPolicy policy, IReadOnlyList<TeamTask> tasks)
    {
        // A run whose graph is not materialized yet has nothing to join — fan-out comes first, and
        // an empty list would otherwise read as "every task is terminal".
        if (tasks.Count == 0)
            return TeamJoinVerdict.Waiting("the run has no tasks yet");

        var open = tasks.Where(task => task.IsOpen).ToArray();
        var done = tasks.Where(task => task.Status == TeamTaskStatus.Done).ToArray();
        var failed = tasks.Where(task => task.Status == TeamTaskStatus.Failed).ToArray();

        return policy.Mode switch
        {
            TeamJoinMode.FirstFailure when failed.Length > 0 =>
                Fired(false, $"first-failure: {Name(failed[0])} failed ({failed[0].FailureReason ?? "no reason given"})"),

            // No failure yet: first-failure waits for everyone, exactly like all-done.
            TeamJoinMode.FirstFailure or TeamJoinMode.AllDone => open.Length > 0
                ? TeamJoinVerdict.Waiting($"{open.Length} of {tasks.Count} lane(s) still open")
                : Fired(
                    done.Length == tasks.Count,
                    done.Length == tasks.Count
                        ? $"every lane reported ({done.Length} of {tasks.Count})"
                        : $"every lane is terminal, {tasks.Count - done.Length} of {tasks.Count} without a result"),

            TeamJoinMode.Quorum when done.Length >= policy.Quorum =>
                Fired(true, $"quorum {policy.Quorum} of {tasks.Count} reached ({done.Length} lane(s) reported)"),

            // The quorum can no longer be met: waiting on the rest would only delay the receipt.
            TeamJoinMode.Quorum when done.Length + open.Length < policy.Quorum =>
                Fired(false, $"quorum {policy.Quorum} of {tasks.Count} is out of reach: {done.Length} reported, {open.Length} still able to"),

            TeamJoinMode.Quorum =>
                TeamJoinVerdict.Waiting($"quorum {policy.Quorum} of {tasks.Count}: {done.Length} reported, {open.Length} still open"),

            _ => TeamJoinVerdict.Waiting($"unknown join mode {policy.Mode}")
        };

        TeamJoinVerdict Fired(bool success, string reason) => new(
            true,
            success,
            reason,
            done,
            [.. tasks.Where(task => task.Status != TeamTaskStatus.Done)],
            open);
    }

    /// <summary>
    /// Whether a run that has already joined counts as a success, recomputed from its terminal task
    /// rows. Deliberately derived rather than stored: the run may sit in
    /// <see cref="TeamRunStatus.Joining"/> across a restart, and re-reading the rows cannot drift
    /// from them the way a remembered flag could.
    /// </summary>
    public static bool Succeeded(TeamJoinPolicy policy, IReadOnlyList<TeamTask> tasks)
    {
        var done = tasks.Count(task => task.Status == TeamTaskStatus.Done);
        return policy.Mode == TeamJoinMode.Quorum
            ? done >= policy.Quorum
            : tasks.Count > 0 && done == tasks.Count;
    }

    /// <summary>One line per lane that did not report, for the run's failure receipt.</summary>
    public static string DescribeMissing(IEnumerable<TeamTask> missing) =>
        string.Join("; ", missing.Select(task =>
            $"{Name(task)} {task.Status.ToString().ToLowerInvariant()}" +
            (task.FailureReason is null ? "" : $" ({task.FailureReason})")));

    private static string Name(TeamTask task) => $"{task.TemplateKey} (#{task.TicketId})";
}

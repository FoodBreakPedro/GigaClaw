namespace GigaClaw.Core.Automation.Policy;

/// <summary>
/// R6's trust anchor for the merge queue (doc/roadmap/lane-codex-runtime.md, Task R6), modeled
/// directly on R3's <see cref="OutboundApprovalGate"/>: an ordinary ticket label is orchestration
/// metadata an agent with board-write can set on its own ticket, so it is never treated as
/// authorization to land that ticket's branch. The approved-project list lives in the owner's app
/// settings (<c>%APPDATA%/GigaClaw/settings.json</c>), which sits outside every workspace and
/// therefore outside every agent's write globs.
/// <para>
/// Default is deny: a project not listed here holds every candidate rather than merging it. Adding
/// a project is a deliberate owner action, not an omission a fresh install falls into by accident.
/// </para>
/// </summary>
public sealed class MergeApprovalGate
{
    private readonly Func<IReadOnlyCollection<string>> _approvedProjects;

    public MergeApprovalGate(Func<IReadOnlyCollection<string>> approvedProjects)
    {
        ArgumentNullException.ThrowIfNull(approvedProjects);
        _approvedProjects = approvedProjects;
    }

    /// <summary>
    /// Re-reads the approved-project list on every call (never cached) so an owner edit to
    /// settings.json takes effect on the very next enqueue or queue-processor poll, with no engine
    /// restart — the same hot-reload contract R3 gives outbound host approval.
    /// </summary>
    public bool IsApproved(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        foreach (var candidate in _approvedProjects())
        {
            if (string.Equals(candidate?.Trim(), slug, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

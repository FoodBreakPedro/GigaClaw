namespace GigaClaw.Core.Models;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? WorkspacePath { get; set; }
    public bool IsPaused { get; set; } = false;
    public string? FallbackModel { get; set; }
    public string? LocalModelBaseUrl { get; set; }
    public string? LocalModelName { get; set; }
    /// <summary>
    /// R6 (doc/roadmap/lane-codex-runtime.md): shell command run in a candidate's rebased worktree
    /// before it is merged by the <c>enqueueMerge</c> automation action's queue processor
    /// (<c>MergeQueueProcessor</c>). Per-automation <c>EnqueueMergeActionSpec.IntegrationCommand</c>
    /// overrides this when set; when both are null/blank the integration step is skipped and the
    /// skip is recorded on the merge receipt rather than silently treated as green.
    /// </summary>
    public string? IntegrationCommand { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

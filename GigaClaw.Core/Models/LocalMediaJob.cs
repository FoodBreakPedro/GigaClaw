namespace GigaClaw.Core.Models;

public static class LocalMediaJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingReview = "awaiting-review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public sealed record LocalMediaJob(
    string Id,
    int TicketId,
    string Kind,
    string Provider,
    string ResourceClass,
    string Status,
    string ExecutionSpecPath,
    string ExecutionSpecSha256,
    string ReceiptPath,
    string? OutputPath,
    string? ProvenanceJson,
    string? Error,
    string IdempotencyKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ReviewedAt,
    string? ReviewDecision,
    int? ProcessId,
    DateTime? BoardNotifiedAt
);

public sealed record CreateLocalMediaJobRequest(
    int TicketId,
    string Kind,
    string Provider,
    string ExecutionSpecPath,
    string IdempotencyKey,
    string Author
);

public sealed record CreateLocalMediaJobResult(LocalMediaJob Job, bool Created);

public sealed record ReviewLocalMediaJobRequest(string Decision, string Author);

public sealed record LocalMediaJobActionRequest(string Author);

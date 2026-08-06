namespace GigaClaw.Core.Models;

/// <summary>
/// A supported user-facing content outcome and the agent that begins its workflow.
/// </summary>
public sealed record DeliverableDefinition(
    string Slug,
    string Name,
    string Description,
    string EntryAgent,
    string OutputCategory);

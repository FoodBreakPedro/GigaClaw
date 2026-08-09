namespace GigaClaw.Core.Models;

/// <summary>
/// A supported user-facing content outcome and the agent that begins its workflow.
/// </summary>
public sealed record DeliverableDefinition(
    string Slug,
    string Name,
    string Description,
    string EntryAgent,
    string OutputCategory)
{
    /// <summary>
    /// The concrete state this journey reaches with the currently configured automation. This must
    /// not claim that content is sent or published when the route only records owner approval.
    /// </summary>
    public string CompletionOutcome { get; init; } = "";

    /// <summary>
    /// Whether the shipped route performs an automated delivery action after content production.
    /// </summary>
    public bool HasAutomatedDelivery { get; init; }
}

namespace GigaClaw.Core.Models;

/// <summary>
/// Validation details for a set of deliverable definitions.
/// </summary>
public sealed record DeliverableCatalogValidationResult(
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

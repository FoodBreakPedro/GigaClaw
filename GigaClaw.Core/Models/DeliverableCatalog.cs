using System.Text;

namespace GigaClaw.Core.Models;

/// <summary>
/// Canonical, API-ready definitions of the content outcomes a project can request.
/// </summary>
public static class DeliverableCatalog
{
    private static readonly IReadOnlyList<DeliverableDefinition> Definitions = Array.AsReadOnly(
    [
        new DeliverableDefinition(
            "blog-post",
            "Blog Post",
            "A researched, reviewable article for publication on the blog.",
            "blog-writer",
            "Editorial article"),
        new DeliverableDefinition(
            "email-newsletter",
            "Email Newsletter",
            "A subscriber email with a clear topic, structure, and call to action.",
            "email-copywriter",
            "Email campaign"),
        new DeliverableDefinition(
            "social-media-content",
            "Social Media Content",
            "Platform-ready social copy and creative direction for a campaign or update.",
            "growth-writer",
            "Social post"),
        new DeliverableDefinition(
            "product-review",
            "Product Review",
            "An evidence-led review of a product, service, or experience.",
            "blog-writer",
            "Review article"),
        new DeliverableDefinition(
            "lead-magnet",
            "Lead Magnet",
            "A useful downloadable resource designed to earn an audience opt-in.",
            "lead-magnet-creator",
            "Downloadable resource"),
        new DeliverableDefinition(
            "content-series",
            "Content Series",
            "A coordinated multi-part content plan with a clear editorial arc.",
            "content-series-planner",
            "Content program"),
    ]);

    private static readonly DeliverableCatalogValidationResult CatalogValidation = Validate(Definitions);

    static DeliverableCatalog()
    {
        if (!CatalogValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Deliverable catalog is invalid: {string.Join("; ", CatalogValidation.Errors)}");
        }
    }

    /// <summary>
    /// Returns every supported deliverable in stable display order.
    /// </summary>
    public static IReadOnlyList<DeliverableDefinition> GetAll() => Definitions;

    /// <summary>
    /// Resolves a user or API supplied deliverable identifier after normalizing harmless separators and casing.
    /// </summary>
    public static bool TryGet(string? value, out DeliverableDefinition? definition)
    {
        definition = null;
        if (!TryNormalizeSlug(value, out var slug))
        {
            return false;
        }

        definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        return definition is not null;
    }

    /// <summary>
    /// Converts human-friendly API input such as "Blog Post" into the canonical "blog-post" slug.
    /// </summary>
    public static bool TryNormalizeSlug(string? value, out string? slug)
    {
        slug = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = new StringBuilder(value.Length);
        var needsSeparator = false;

        foreach (var character in value.Trim())
        {
            if (character is >= 'A' and <= 'Z')
            {
                if (needsSeparator && normalized.Length > 0)
                {
                    normalized.Append('-');
                }

                normalized.Append(char.ToLowerInvariant(character));
                needsSeparator = false;
                continue;
            }

            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (needsSeparator && normalized.Length > 0)
                {
                    normalized.Append('-');
                }

                normalized.Append(character);
                needsSeparator = false;
                continue;
            }

            if (character is '-' or '_' || char.IsWhiteSpace(character))
            {
                needsSeparator = normalized.Length > 0;
                continue;
            }

            return false;
        }

        if (normalized.Length == 0)
        {
            return false;
        }

        slug = normalized.ToString();
        return true;
    }

    /// <summary>
    /// Validates catalog data before it is exposed through an API or persisted in a ticket.
    /// </summary>
    public static DeliverableCatalogValidationResult Validate(IEnumerable<DeliverableDefinition>? definitions)
    {
        if (definitions is null)
        {
            return new DeliverableCatalogValidationResult(["Definitions are required."]);
        }

        var errors = new List<string>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var definition in definitions)
        {
            index++;
            if (definition is null)
            {
                errors.Add($"Definition {index} is required.");
                continue;
            }

            if (!TryNormalizeSlug(definition.Slug, out var normalizedSlug) ||
                !string.Equals(definition.Slug, normalizedSlug, StringComparison.Ordinal))
            {
                errors.Add($"Definition {index} has an invalid slug.");
            }
            else if (!slugs.Add(normalizedSlug!))
            {
                errors.Add($"Definition {index} duplicates slug '{normalizedSlug}'.");
            }

            AddRequiredFieldError(errors, definition.Name, index, "name");
            AddRequiredFieldError(errors, definition.Description, index, "description");
            AddRequiredFieldError(errors, definition.EntryAgent, index, "entry agent");
            AddRequiredFieldError(errors, definition.OutputCategory, index, "output category");
        }

        if (index == 0)
        {
            errors.Add("At least one definition is required.");
        }

        return new DeliverableCatalogValidationResult(errors);
    }

    private static void AddRequiredFieldError(List<string> errors, string? value, int index, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            var article = field is "entry agent" or "output category" ? "an" : "a";
            errors.Add($"Definition {index} requires {article} {field}.");
        }
    }
}

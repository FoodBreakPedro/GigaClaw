namespace GigaClaw.Core.Packs;

/// <summary>
/// Raised whenever a pack manifest or a composition breaks a rule in doc/pack-infrastructure.md.
/// Packs fail closed: a malformed manifest is refused, never best-effort parsed, and a rejected
/// install leaves the disk untouched (§4, D5).
/// </summary>
public sealed class PackValidationException : Exception
{
    public PackValidationException(IReadOnlyList<string> errors)
        : base(Format(errors))
    {
        Errors = errors;
    }

    public PackValidationException(string error) : this(new[] { error }) { }

    public IReadOnlyList<string> Errors { get; }

    private static string Format(IReadOnlyList<string> errors) =>
        errors.Count == 1
            ? errors[0]
            : $"Pack validation failed with {errors.Count} errors:{Environment.NewLine}  - "
              + string.Join(Environment.NewLine + "  - ", errors);
}

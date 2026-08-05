namespace GigaClaw.Core.Models;

public static class CodexModelCatalog
{
    public const string DefaultModel = "gpt-5.6-sol";

    public static readonly IReadOnlyList<string> Models =
    [
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
    ];

    public static bool TryResolve(string? configuredModel, out string model)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            model = DefaultModel;
            return true;
        }

        var configured = configuredModel.Trim();
        if (configured.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            model = configured;
            return true;
        }

        if (configured.Contains("haiku", StringComparison.OrdinalIgnoreCase))
        {
            model = "gpt-5.6-luna";
            return true;
        }
        if (configured.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
        {
            model = "gpt-5.6-terra";
            return true;
        }
        if (configured.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
            configured.Contains("fable", StringComparison.OrdinalIgnoreCase))
        {
            model = "gpt-5.6-sol";
            return true;
        }

        model = configured;
        return false;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaClaw.Eval;

public sealed record PromptBudgetConfig(
    string Source,
    string Unit,
    int WarningThreshold,
    int MaximumThreshold);

// Replay is optional and defaulted so a config written before the replay layer existed
// (and the ones the static tests synthesize) still deserializes.
public sealed record EvalConfig(
    int Version,
    string ArtifactRoot,
    string BaselineRoot,
    PromptBudgetConfig PromptBudget,
    ReplayConfig? Replay = null);

public sealed record EvalCheckResult(
    string Id,
    string Category,
    string Status,
    string Message);

public sealed record EvalAgentResult(
    string Agent,
    string BaselineStatus,
    IReadOnlyList<EvalCheckResult> Checks);

public sealed record EvalReport(
    int Version,
    string Mode,
    string Target,
    PromptBudgetConfig PromptBudget,
    IReadOnlyList<EvalAgentResult> Agents);

public sealed record EvalBaselineCheck(string Id, string Category, string Status);

public sealed record EvalBaseline(
    int Version,
    string Agent,
    IReadOnlyList<EvalBaselineCheck> ExpectedChecks);

public sealed record EvalRunResult(EvalReport Report, int ExitCode, long ElapsedMilliseconds);

internal static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Could not parse {path}.");

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options) + "\n";
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaClaw.Eval;

public sealed record PromptBudgetConfig(
    string Source,
    string Unit,
    int WarningThreshold,
    int MaximumThreshold);

// Replay, Judge and MonteCarlo are optional and defaulted so a config written before those layers
// existed (and the ones the static tests synthesize) still deserializes.
public sealed record EvalConfig(
    int Version,
    string ArtifactRoot,
    string BaselineRoot,
    PromptBudgetConfig PromptBudget,
    ReplayConfig? Replay = null,
    JudgeConfig? Judge = null,
    MonteCarloConfig? MonteCarlo = null);

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

/// <summary>Removes per-agent artifact files whose name no longer matches a live agent. Reports
/// are keyed by slug and only ever overwritten, so an agent that leaves the catalog would keep
/// its last report on disk forever — the gitignored artifact root grew without bound.</summary>
internal static class EvalArtifacts
{
    public static void PruneOrphans(string directory, IEnumerable<string> currentNames)
    {
        if (!Directory.Exists(directory)) return;
        var current = currentNames.ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            if (!current.Contains(Path.GetFileNameWithoutExtension(path)))
                File.Delete(path);
    }
}

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

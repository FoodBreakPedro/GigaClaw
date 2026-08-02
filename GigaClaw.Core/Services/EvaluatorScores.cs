using System.Globalization;
using System.Text.Json;

namespace GigaClaw.Core.Services;

/// <summary>Per-agent quality aggregate distilled from the evaluator's <c>scores.json</c>. Every
/// metric is nullable because the file legitimately carries two record shapes and neither one has
/// all of them — an absent metric renders as "—", never as a zero.</summary>
public sealed record EvaluatorAgentScore(
    string Agent,
    int Samples,
    double? Outcome,
    double? Process,
    double? Efficiency,
    double? Communication,
    double? FirstPassRate,
    double? DeliveryQuality,
    double? FeedbackCompliance,
    double? BlockRate,
    DateTime? LastEvaluatedAtUtc);

/// <summary>
/// Plan 4.2 §8 — the <b>first consumer</b> of <c>&lt;workspace&gt;/.agents/evaluator/memory/scores.json</c>.
/// Until now the evaluator wrote that file and nothing read it, which is the explicit gate on the
/// O3/O4 outcome-grounded model routing entry in <c>doc/roadmap/packs-and-later.md</c>.
///
/// <para><b>Two shapes, both honest.</b> <c>ProjectTemplate/Agents/evaluator/</c> defines the file
/// twice on purpose: <c>references/procedure-steps.md</c> specifies the score <i>cache</i>
/// (<c>tickets</c> keyed by ticket id, carrying <c>worker</c>, <c>firstPass</c>,
/// <c>deliveryQuality</c>, <c>feedbackCompliance</c>, <c>blocked</c>), and <c>SKILL.md</c> specifies
/// the typed <b>verdict-contract v1</b> record with a four-entry <c>categories</c> array (outcome /
/// process / efficiency / communication). A real workspace may hold either or both, so this reader
/// accepts both and averages whatever is actually present per agent rather than forcing one shape.</para>
///
/// <para>Parsing never throws: a missing, unreadable, or malformed file yields an empty list and the
/// tile shows its empty state. The evaluator's own contract says a malformed cache must not be
/// overwritten, so a reader that crashed on one would be the only thing that ever broke.</para>
/// </summary>
public static class EvaluatorScores
{
    /// <summary>Workspace-relative path of the file this reader consumes.</summary>
    public const string RelativePath = ".agents/evaluator/memory/scores.json";

    /// <summary>Largest <c>scores.json</c> this reader will load. The file is a curated per-agent
    /// cache; anything past this is a runaway append, and reading it whole on every dashboard
    /// render would cost more than the tile it feeds is worth. Oversized files yield the empty
    /// state, exactly like a malformed one.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    public static IReadOnlyList<EvaluatorAgentScore> ReadForWorkspace(string workspacePath)
    {
        var path = Path.Combine(workspacePath, ".agents", "evaluator", "memory", "scores.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes) return [];
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Aggregates one <c>scores.json</c> document into per-agent averages, newest activity
    /// first. Exposed for tests and for anyone holding the JSON already.</summary>
    public static IReadOnlyList<EvaluatorAgentScore> Parse(string json)
    {
        var accumulators = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            Collect(document.RootElement, accumulators, depth: 0);
        }
        catch (JsonException)
        {
            return [];
        }

        return accumulators.Values
            .Select(a => a.ToScore())
            .OrderByDescending(s => s.LastEvaluatedAtUtc ?? DateTime.MinValue)
            .ThenBy(s => s.Agent, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Collect(JsonElement element, Dictionary<string, Accumulator> into, int depth)
    {
        // The two documented shapes nest at most three levels (root → tickets → entry); the bound is
        // there so an unexpected hand-edited file cannot turn a dashboard render into a deep walk.
        if (depth > 4) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, into, depth + 1);
                return;

            case JsonValueKind.Object:
                if (TryReadRecord(element, into)) return;
                foreach (var property in element.EnumerateObject())
                {
                    // retryQueue holds failures, not scores, and has no worker — skip it explicitly
                    // so a queued ticket id can never be mistaken for an agent.
                    if (property.NameEquals("retryQueue") || property.NameEquals("source")) continue;
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Collect(property.Value, into, depth + 1);
                }
                return;
        }
    }

    private static bool TryReadRecord(JsonElement element, Dictionary<string, Accumulator> into)
    {
        var hasCategories = element.TryGetProperty("categories", out var categories)
            && categories.ValueKind == JsonValueKind.Array;
        var hasCacheMetrics = element.TryGetProperty("deliveryQuality", out _)
            || element.TryGetProperty("firstPass", out _);
        if (!hasCategories && !hasCacheMetrics) return false;

        var agent = Str(element, "worker")
            ?? Str(element, "subject")
            ?? Str(element, "agentUnderReview")
            ?? Str(element, "agent")
            ?? "unknown";

        if (!into.TryGetValue(agent, out var acc))
            into[agent] = acc = new Accumulator(agent);
        acc.Samples++;

        if (hasCategories)
        {
            foreach (var category in categories.EnumerateArray())
            {
                var name = Str(category, "name");
                if (name is null) continue;
                var score = Num(category, "score");
                if (score is null) continue;
                var max = Num(category, "max") is double m && m > 0 ? m : 5.0;
                var normalized = score.Value / max * 5.0; // everything is presented on a 0–5 scale
                if (name.StartsWith("outcome", StringComparison.OrdinalIgnoreCase)) acc.Outcome.Add(normalized);
                else if (name.StartsWith("process", StringComparison.OrdinalIgnoreCase)) acc.Process.Add(normalized);
                else if (name.StartsWith("efficien", StringComparison.OrdinalIgnoreCase)) acc.Efficiency.Add(normalized);
                else if (name.StartsWith("communicat", StringComparison.OrdinalIgnoreCase)) acc.Communication.Add(normalized);
            }
        }

        if (Num(element, "deliveryQuality") is double delivery) acc.Delivery.Add(delivery);
        if (Num(element, "feedbackCompliance") is double compliance) acc.Compliance.Add(compliance);
        if (Bool(element, "firstPass") is bool firstPass) acc.FirstPass.Add(firstPass ? 1 : 0);
        if (Bool(element, "blocked") is bool blocked) acc.Blocked.Add(blocked ? 1 : 0);

        var at = Date(element, "evaluatedAt") ?? Date(element, "reviewedAtUtc");
        if (at is DateTime stamp && (acc.LastAt is null || stamp > acc.LastAt)) acc.LastAt = stamp;
        return true;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTime? Date(JsonElement element, string name)
    {
        var raw = Str(element, name);
        return raw is not null && DateTime.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private sealed class Accumulator(string agent)
    {
        public int Samples;
        public DateTime? LastAt;
        public readonly List<double> Outcome = [];
        public readonly List<double> Process = [];
        public readonly List<double> Efficiency = [];
        public readonly List<double> Communication = [];
        public readonly List<double> Delivery = [];
        public readonly List<double> Compliance = [];
        public readonly List<double> FirstPass = [];
        public readonly List<double> Blocked = [];

        private static double? Avg(List<double> values) => values.Count == 0 ? null : values.Average();

        public EvaluatorAgentScore ToScore() => new(
            agent,
            Samples,
            Avg(Outcome),
            Avg(Process),
            Avg(Efficiency),
            Avg(Communication),
            Avg(FirstPass),
            Avg(Delivery),
            Avg(Compliance),
            Avg(Blocked),
            LastAt);
    }
}

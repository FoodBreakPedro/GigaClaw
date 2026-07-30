using System.Security.Cryptography;
using System.Text.Json;

namespace GigaClaw.Core.Automation.Verdicts;

/// <summary>
/// A reviewer's judgement, parsed from the v1 verdict contract
/// (<c>ProjectTemplate/Agents/scripts/verdict.schema.json</c>, documented in
/// <c>doc/verdict-contract.md</c>). The Python validator shipped to workspaces is the
/// authoring-side enforcement point; this type is the host-side reader used by the
/// automation vocabulary. Both are held to the same fixture corpus by
/// <c>TemplateVerdictContractTests</c>.
/// </summary>
public sealed record AgentVerdict(
    string Agent,
    string TicketId,
    string Decision,
    string InputDigest,
    string? Summary,
    IReadOnlyList<VerdictCategory> Categories,
    IReadOnlyList<VerdictVeto> VetoItems,
    IReadOnlyList<VerdictEvidence> Evidence,
    DateTime ReviewedAtUtc);

public sealed record VerdictCategory(string Name, double Score, double Max, string? Notes);

public sealed record VerdictVeto(string Code, string Statement, IReadOnlyList<string> EvidenceRefs);

public sealed record VerdictEvidence(string Kind, string Ref, string? Note);

/// <summary>
/// What the newest verdict on a ticket says. <see cref="Missing"/>, <see cref="Invalid"/> and
/// <see cref="Stale"/> are outcomes an automation can route on, so "the reviewer produced prose
/// instead of a verdict" fails loudly instead of looking like "not yet reviewed".
/// </summary>
public enum VerdictOutcome
{
    Missing,
    Invalid,
    Stale,
    Ship,
    Fix,
    Block,
}

public static class VerdictReader
{
    /// <summary>Marker line that carries a verdict in a ticket comment. See doc/verdict-contract.md.</summary>
    public const string MarkerPrefix = "GIGACLAW-VERDICT v1";

    private static readonly System.Text.RegularExpressions.Regex MarkerRegex = new(
        @"^GIGACLAW-VERDICT\s+v1\s+(?<agent>[a-z0-9][a-z0-9-]*)\s+(?<verdict>SHIP|FIX|BLOCK)\s+artifact-(?<digest>sha256:[0-9a-f]{64})\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FenceRegex = new(
        "```json\\s*\\n(?<body>.*?)\\n```",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DigestRegex = new(
        "^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SlugRegex = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool ContainsMarker(string? commentBody)
        => commentBody is not null && MarkerRegex.IsMatch(commentBody);

    /// <summary>
    /// Reads the verdict out of a comment body. The last marker wins so an edited comment
    /// cannot resurrect an earlier judgement, and the marker must agree with the JSON body.
    /// Returns false with a diagnostic for anything else — callers treat that as BLOCK.
    /// </summary>
    public static bool TryRead(string? commentBody, out AgentVerdict? verdict, out string? error)
    {
        verdict = null;
        error = null;

        if (string.IsNullOrWhiteSpace(commentBody))
        {
            error = "comment is empty";
            return false;
        }

        System.Text.RegularExpressions.Match? marker = null;
        foreach (System.Text.RegularExpressions.Match candidate in MarkerRegex.Matches(commentBody))
            marker = candidate;

        if (marker is null)
        {
            error = $"comment has no '{MarkerPrefix} <agent> <VERDICT> artifact-sha256:<digest>' marker line";
            return false;
        }

        var fence = FenceRegex.Match(commentBody, marker.Index + marker.Length);
        if (!fence.Success)
        {
            error = "comment has a verdict marker but no ```json verdict block after it";
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(fence.Groups["body"].Value);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            error = $"verdict block is not valid JSON: {exception.Message}";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "verdict block must be a JSON object";
            return false;
        }

        if (!TryBuild(root, out verdict, out error))
            return false;

        if (!string.Equals(verdict!.Agent, marker.Groups["agent"].Value, StringComparison.Ordinal))
            error = $"marker line says agent={marker.Groups["agent"].Value} but the verdict block says {verdict.Agent}";
        else if (!string.Equals(verdict.Decision, marker.Groups["verdict"].Value, StringComparison.Ordinal))
            error = $"marker line says verdict={marker.Groups["verdict"].Value} but the verdict block says {verdict.Decision}";
        else if (!string.Equals(verdict.InputDigest, marker.Groups["digest"].Value, StringComparison.Ordinal))
            error = "marker line and verdict block disagree on inputDigest";

        if (error is null)
            return true;

        verdict = null;
        return false;
    }

    /// <summary>Validates a bare verdict object (no comment transport) and materializes it.</summary>
    public static bool TryRead(JsonElement root, out AgentVerdict? verdict, out string? error)
        => TryBuild(root, out verdict, out error);

    private static bool TryBuild(JsonElement root, out AgentVerdict? verdict, out string? error)
    {
        verdict = null;
        error = null;

        foreach (var property in root.EnumerateObject())
        {
            if (!KnownProperties.Contains(property.Name))
            {
                error = $"verdict has unknown property '{property.Name}'";
                return false;
            }
        }

        if (!root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1)
        {
            error = "verdict must declare schemaVersion 1";
            return false;
        }

        if (!TryString(root, "agent", out var agent, ref error) || !SlugRegex.IsMatch(agent!))
        {
            error ??= $"agent '{agent}' is not an agent slug";
            return false;
        }

        if (!root.TryGetProperty("ticketId", out var ticketElement)
            || ticketElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            error = "verdict is missing ticketId";
            return false;
        }

        var ticketId = ticketElement.ValueKind == JsonValueKind.Number
            ? ticketElement.GetRawText()
            : ticketElement.GetString() ?? "";

        if (!TryString(root, "verdict", out var decision, ref error))
            return false;
        if (decision is not ("SHIP" or "FIX" or "BLOCK"))
        {
            error = $"verdict '{decision}' must be SHIP, FIX or BLOCK";
            return false;
        }

        if (!TryString(root, "inputDigest", out var digest, ref error) || !DigestRegex.IsMatch(digest!))
        {
            error ??= "inputDigest must be sha256:<64 hex>";
            return false;
        }

        if (!TryString(root, "reviewedAtUtc", out var reviewedAt, ref error)
            || !DateTime.TryParse(
                reviewedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var reviewedAtUtc))
        {
            error ??= "reviewedAtUtc must be an ISO-8601 UTC instant";
            return false;
        }

        var categories = new List<VerdictCategory>();
        if (!root.TryGetProperty("categories", out var categoryArray) || categoryArray.ValueKind != JsonValueKind.Array)
        {
            error = "verdict is missing categories";
            return false;
        }

        foreach (var element in categoryArray.EnumerateArray())
        {
            if (!TryString(element, "name", out var name, ref error))
                return false;
            if (!element.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number
                || !element.TryGetProperty("max", out var max) || max.ValueKind != JsonValueKind.Number)
            {
                error = $"category '{name}' must have numeric score and max";
                return false;
            }

            if (score.GetDouble() > max.GetDouble())
            {
                error = $"category '{name}' scores {score.GetDouble()} out of {max.GetDouble()}";
                return false;
            }

            categories.Add(new VerdictCategory(
                name!,
                score.GetDouble(),
                max.GetDouble(),
                element.TryGetProperty("notes", out var notes) ? notes.GetString() : null));
        }

        if (categories.Count == 0)
        {
            error = "verdict must score at least one category";
            return false;
        }

        if (categories.Select(c => c.Name.Trim().ToLowerInvariant()).Distinct().Count() != categories.Count)
        {
            error = "verdict repeats a category name";
            return false;
        }

        var vetoItems = new List<VerdictVeto>();
        if (!root.TryGetProperty("vetoItems", out var vetoArray) || vetoArray.ValueKind != JsonValueKind.Array)
        {
            error = "verdict is missing vetoItems (use [] when there are none)";
            return false;
        }

        foreach (var element in vetoArray.EnumerateArray())
        {
            if (!TryString(element, "code", out var code, ref error) || !SlugRegex.IsMatch(code!))
            {
                error ??= $"veto code '{code}' is not a slug";
                return false;
            }

            if (!TryString(element, "statement", out var statement, ref error))
                return false;

            var refs = new List<string>();
            if (element.TryGetProperty("evidenceRefs", out var refArray) && refArray.ValueKind == JsonValueKind.Array)
                refs.AddRange(refArray.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0));

            vetoItems.Add(new VerdictVeto(code!, statement!, refs));
        }

        var evidence = new List<VerdictEvidence>();
        if (!root.TryGetProperty("evidence", out var evidenceArray) || evidenceArray.ValueKind != JsonValueKind.Array)
        {
            error = "verdict is missing evidence";
            return false;
        }

        foreach (var element in evidenceArray.EnumerateArray())
        {
            if (!TryString(element, "kind", out var kind, ref error) || !TryString(element, "ref", out var reference, ref error))
                return false;
            if (kind is not ("path" or "hash" or "link"))
            {
                error = $"evidence kind '{kind}' must be path, hash or link";
                return false;
            }

            if (kind == "path" && !IsWorkspaceRelative(reference!))
            {
                error = $"evidence path '{reference}' must be workspace-relative";
                return false;
            }

            evidence.Add(new VerdictEvidence(
                kind!,
                reference!,
                element.TryGetProperty("note", out var note) ? note.GetString() : null));
        }

        var evidenceRefs = evidence.Select(e => e.Ref).ToHashSet(StringComparer.Ordinal);
        foreach (var veto in vetoItems)
        {
            foreach (var reference in veto.EvidenceRefs)
            {
                if (!evidenceRefs.Contains(reference))
                {
                    error = $"veto item '{veto.Code}' cites evidence '{reference}' that the verdict does not list";
                    return false;
                }
            }
        }

        if (decision == "SHIP" && vetoItems.Count > 0)
        {
            error = "verdict is SHIP but carries veto items";
            return false;
        }

        if (decision is "FIX" or "BLOCK" && vetoItems.Count == 0 && categories.All(c => c.Score >= c.Max))
        {
            error = $"verdict is {decision} but states no machine-readable reason";
            return false;
        }

        verdict = new AgentVerdict(
            agent!,
            ticketId,
            decision!,
            digest!,
            root.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
            categories,
            vetoItems,
            evidence,
            reviewedAtUtc);
        return true;
    }

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "agent", "ticketId", "verdict", "summary",
        "categories", "vetoItems", "evidence", "reviewedAtUtc", "inputDigest", "reviewCycle",
    };

    private static bool TryString(JsonElement element, string name, out string? value, ref string? error)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            error = $"verdict is missing {name}";
            return false;
        }

        value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"verdict has an empty {name}";
            return false;
        }

        return true;
    }

    private static bool IsWorkspaceRelative(string reference)
    {
        var normalized = reference.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith('~') || normalized.Contains(':'))
            return false;
        return !normalized.Split('/').Contains("..");
    }

    /// <summary>
    /// A verdict is fresh while the artifact it judged still hashes to <c>inputDigest</c>.
    /// The verdict names that artifact through its <c>path</c> evidence; when none of those
    /// files matches (edited since review, missing, or never declared) the verdict is stale
    /// and must not gate anything.
    /// </summary>
    public static bool IsFresh(AgentVerdict verdict, string? workspacePath, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            reason = "workspace is unavailable, so the reviewed artifact cannot be re-hashed";
            return false;
        }

        var paths = verdict.Evidence.Where(e => e.Kind == "path").Select(e => e.Ref).ToList();
        if (paths.Count == 0)
        {
            reason = "verdict declares no path evidence, so freshness cannot be verified";
            return false;
        }

        var root = Path.GetFullPath(workspacePath);
        foreach (var relative in paths)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                continue;

            if (string.Equals(Sha256Of(full), verdict.InputDigest, StringComparison.Ordinal))
                return true;
        }

        reason = $"no reviewed artifact still hashes to {verdict.InputDigest}";
        return false;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>A ticket comment as the scanner sees it. Newest last, matching ticket comment order.</summary>
public sealed record VerdictComment(string Content, string Author, DateTime CreatedAt);

public sealed record VerdictResolution(VerdictOutcome Outcome, AgentVerdict? Verdict, string? Diagnostic);

/// <summary>
/// Decides what the newest verdict on a ticket says. Pure: the freshness probe is injected so
/// the file hashing stays at the caller's boundary.
/// </summary>
public static class VerdictScanner
{
    public static VerdictResolution Resolve(
        IReadOnlyList<VerdictComment> comments,
        string? agent = null,
        Func<AgentVerdict, (bool Fresh, string? Reason)>? freshness = null)
    {
        for (var index = comments.Count - 1; index >= 0; index--)
        {
            var comment = comments[index];
            if (!VerdictReader.ContainsMarker(comment.Content))
                continue;

            if (!string.IsNullOrEmpty(agent) && !ClaimsAgent(comment.Content, agent))
                continue; // Another reviewer's verdict never shadows the one being gated on.

            if (!VerdictReader.TryRead(comment.Content, out var verdict, out var error))
                return new VerdictResolution(VerdictOutcome.Invalid, null, error);

            if (freshness is not null)
            {
                var (fresh, reason) = freshness(verdict!);
                if (!fresh)
                    return new VerdictResolution(VerdictOutcome.Stale, verdict, reason);
            }

            return new VerdictResolution(
                verdict!.Decision switch
                {
                    "SHIP" => VerdictOutcome.Ship,
                    "FIX" => VerdictOutcome.Fix,
                    _ => VerdictOutcome.Block,
                },
                verdict,
                null);
        }

        return new VerdictResolution(
            VerdictOutcome.Missing,
            null,
            agent is null ? "no verdict on this ticket" : $"no verdict from {agent} on this ticket");
    }

    private static bool ClaimsAgent(string content, string agent)
        => content.Contains($"{VerdictReader.MarkerPrefix} {agent} ", StringComparison.Ordinal);
}

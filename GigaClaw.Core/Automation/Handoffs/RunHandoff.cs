using System.Text;
using System.Text.Json;

namespace GigaClaw.Core.Automation.Handoffs;

/// <summary>
/// What one agent leaves behind for the next: outputs, owned files, assumptions, open loops
/// and where the work goes next. Parsed from the v1 handoff contract
/// (<c>ProjectTemplate/Agents/scripts/handoff.schema.json</c>, documented in
/// <c>doc/handoff-contract.md</c>). The Python validator is the authoring-side enforcement
/// point; this is the host-side reader the engine injects from.
/// </summary>
public sealed record RunHandoff(
    string Agent,
    string TicketId,
    string RunId,
    string Summary,
    IReadOnlyList<HandoffArtifact> Inputs,
    IReadOnlyList<HandoffArtifact> Outputs,
    IReadOnlyList<string> OwnedFiles,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<HandoffOpenLoop> OpenLoops,
    IReadOnlyList<HandoffCriterion> AcceptanceCriteria,
    string? NextRole,
    DateTime ProducedAtUtc);

public sealed record HandoffArtifact(string Kind, string Ref, string? Note);

public sealed record HandoffOpenLoop(string Statement, bool Blocking);

public sealed record HandoffCriterion(string Statement, bool Met, string? EvidenceRef);

public static class HandoffReader
{
    /// <summary>Marker line that carries a handoff in a ticket comment. See doc/handoff-contract.md.</summary>
    public const string MarkerPrefix = "GIGACLAW-HANDOFF v1";

    private static readonly System.Text.RegularExpressions.Regex MarkerRegex = new(
        @"^GIGACLAW-HANDOFF\s+v1\s+(?<agent>[a-z0-9][a-z0-9-]*)\s+ticket-(?<ticket>[0-9]+)\s+run-(?<run>\S+)\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FenceRegex = new(
        "```json\\s*\\n(?<body>.*?)\\n```",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SlugRegex = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool ContainsMarker(string? commentBody)
        => commentBody is not null && MarkerRegex.IsMatch(commentBody);

    /// <summary>
    /// Reads the handoff out of a comment body. The last marker wins, and the marker must agree
    /// with the JSON body. Anything else returns false: an unreadable handoff is no handoff, and
    /// the next agent starts from the ticket rather than from a half-parsed one.
    /// </summary>
    public static bool TryRead(string? commentBody, out RunHandoff? handoff, out string? error)
    {
        handoff = null;
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
            error = $"comment has no '{MarkerPrefix} <agent> ticket-<id> run-<runId>' marker line";
            return false;
        }

        var fence = FenceRegex.Match(commentBody, marker.Index + marker.Length);
        if (!fence.Success)
        {
            error = "comment has a handoff marker but no ```json handoff block after it";
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
            error = $"handoff block is not valid JSON: {exception.Message}";
            return false;
        }

        if (!TryBuild(root, out handoff, out error))
            return false;

        if (!string.Equals(handoff!.Agent, marker.Groups["agent"].Value, StringComparison.Ordinal))
            error = $"marker line says agent={marker.Groups["agent"].Value} but the handoff says {handoff.Agent}";
        else if (!string.Equals(handoff.TicketId, marker.Groups["ticket"].Value, StringComparison.Ordinal))
            error = $"marker line says ticket-{marker.Groups["ticket"].Value} but the handoff says {handoff.TicketId}";
        else if (!string.Equals(handoff.RunId, marker.Groups["run"].Value, StringComparison.Ordinal))
            error = "marker line and handoff disagree on runId";

        if (error is null)
            return true;

        handoff = null;
        return false;
    }

    public static bool TryRead(JsonElement root, out RunHandoff? handoff, out string? error)
        => TryBuild(root, out handoff, out error);

    private static bool TryBuild(JsonElement root, out RunHandoff? handoff, out string? error)
    {
        handoff = null;
        error = null;

        foreach (var property in root.EnumerateObject())
        {
            if (!KnownProperties.Contains(property.Name))
            {
                error = $"handoff has unknown property '{property.Name}'";
                return false;
            }
        }

        if (!root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
        {
            error = "handoff must declare schemaVersion 1";
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
            error = "handoff is missing ticketId";
            return false;
        }

        var ticketId = ticketElement.ValueKind == JsonValueKind.Number
            ? ticketElement.GetRawText()
            : ticketElement.GetString() ?? "";

        if (!TryString(root, "runId", out var runId, ref error)
            || !TryString(root, "summary", out var summary, ref error))
            return false;

        if (!TryString(root, "producedAtUtc", out var produced, ref error)
            || !DateTime.TryParse(
                produced,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var producedAtUtc))
        {
            error ??= "producedAtUtc must be an ISO-8601 UTC instant";
            return false;
        }

        string? nextRole = null;
        if (root.TryGetProperty("nextRole", out var nextElement) && nextElement.ValueKind == JsonValueKind.String)
        {
            nextRole = nextElement.GetString();
            if (!string.IsNullOrEmpty(nextRole) && !SlugRegex.IsMatch(nextRole))
            {
                error = $"nextRole '{nextRole}' is not an agent slug";
                return false;
            }
        }

        if (!TryArtifacts(root, "inputs", out var inputs, ref error)
            || !TryArtifacts(root, "outputs", out var outputs, ref error))
            return false;

        var ownedFiles = new List<string>();
        if (root.TryGetProperty("ownedFiles", out var ownedElement) && ownedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ownedElement.EnumerateArray())
            {
                var value = entry.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "ownedFiles contains an empty entry";
                    return false;
                }

                // A lease on an absolute path or a traversal is not enforceable, so it is
                // rejected here rather than handed to the lease layer to discover.
                if (!IsWorkspaceRelative(value))
                {
                    error = $"ownedFiles entry '{value}' must be workspace-relative";
                    return false;
                }

                ownedFiles.Add(value);
            }
        }

        var assumptions = new List<string>();
        if (root.TryGetProperty("assumptions", out var assumptionsElement) && assumptionsElement.ValueKind == JsonValueKind.Array)
            assumptions.AddRange(assumptionsElement.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0));

        var openLoops = new List<HandoffOpenLoop>();
        if (root.TryGetProperty("openLoops", out var loopsElement) && loopsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in loopsElement.EnumerateArray())
            {
                if (!TryString(entry, "statement", out var statement, ref error))
                    return false;
                openLoops.Add(new HandoffOpenLoop(
                    statement!,
                    entry.TryGetProperty("blocking", out var blocking) && blocking.ValueKind == JsonValueKind.True));
            }
        }

        var criteria = new List<HandoffCriterion>();
        if (root.TryGetProperty("acceptanceCriteria", out var criteriaElement) && criteriaElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in criteriaElement.EnumerateArray())
            {
                if (!TryString(entry, "statement", out var statement, ref error))
                    return false;
                if (!entry.TryGetProperty("met", out var met) || met.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"acceptance criterion '{statement}' must state whether it is met";
                    return false;
                }

                criteria.Add(new HandoffCriterion(
                    statement!,
                    met.ValueKind == JsonValueKind.True,
                    entry.TryGetProperty("evidenceRef", out var evidence) ? evidence.GetString() : null));
            }
        }

        handoff = new RunHandoff(
            agent!, ticketId, runId!, summary!,
            inputs, outputs, ownedFiles, assumptions, openLoops, criteria, nextRole, producedAtUtc);
        return true;
    }

    /// <summary>
    /// Renders the previous run's handoff for injection into the next dispatch. Compact on
    /// purpose: this is prepended to an agent's prompt, so it carries the decisions and the
    /// unfinished work, not the whole artifact list.
    /// </summary>
    public static string Render(RunHandoff handoff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Handoff from {handoff.Agent} (run {handoff.RunId}, {handoff.ProducedAtUtc:yyyy-MM-dd HH:mm}Z)]");
        sb.AppendLine(handoff.Summary);

        if (handoff.Outputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Produced:");
            foreach (var output in handoff.Outputs)
                sb.AppendLine($"- {output.Ref}{(output.Note is null ? "" : $" — {output.Note}")}");
        }

        if (handoff.Assumptions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Assumed (inherit or challenge):");
            foreach (var assumption in handoff.Assumptions)
                sb.AppendLine($"- {assumption}");
        }

        var unmet = handoff.AcceptanceCriteria.Where(c => !c.Met).ToList();
        if (unmet.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Acceptance criteria still unmet:");
            foreach (var criterion in unmet)
                sb.AppendLine($"- {criterion.Statement}");
        }

        if (handoff.OpenLoops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Open loops:");
            foreach (var loop in handoff.OpenLoops)
                sb.AppendLine($"- {(loop.Blocking ? "[blocking] " : "")}{loop.Statement}");
        }

        if (handoff.OwnedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Files the previous run claimed: {string.Join(", ", handoff.OwnedFiles)}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Newest readable handoff on a ticket, or null. Unreadable ones are skipped, not fatal.</summary>
    public static RunHandoff? Latest(IReadOnlyList<string> commentBodies)
    {
        for (var index = commentBodies.Count - 1; index >= 0; index--)
        {
            if (!ContainsMarker(commentBodies[index]))
                continue;
            if (TryRead(commentBodies[index], out var handoff, out _))
                return handoff;
        }

        return null;
    }

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "agent", "ticketId", "runId", "summary", "inputs", "outputs",
        "ownedFiles", "assumptions", "openLoops", "acceptanceCriteria", "nextRole", "producedAtUtc",
    };

    private static bool TryArtifacts(JsonElement root, string name, out List<HandoffArtifact> artifacts, ref string? error)
    {
        artifacts = [];
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            return name != "outputs" || Missing(name, ref error);

        foreach (var entry in element.EnumerateArray())
        {
            if (!TryString(entry, "kind", out var kind, ref error) || !TryString(entry, "ref", out var reference, ref error))
                return false;
            if (kind is not ("path" or "hash" or "link"))
            {
                error = $"{name} entry kind '{kind}' must be path, hash or link";
                return false;
            }

            if (kind == "path" && !IsWorkspaceRelative(reference!))
            {
                error = $"{name} path '{reference}' must be workspace-relative";
                return false;
            }

            artifacts.Add(new HandoffArtifact(
                kind!, reference!, entry.TryGetProperty("note", out var note) ? note.GetString() : null));
        }

        return true;
    }

    private static bool Missing(string name, ref string? error)
    {
        error = $"handoff is missing {name}";
        return false;
    }

    private static bool TryString(JsonElement element, string name, out string? value, ref string? error)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            error = $"handoff is missing {name}";
            return false;
        }

        value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"handoff has an empty {name}";
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
}

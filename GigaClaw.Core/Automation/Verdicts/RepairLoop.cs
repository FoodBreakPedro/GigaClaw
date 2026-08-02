using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GigaClaw.Core.Automation.Verdicts;

/// <summary>One review round that ended in <c>FIX</c>, numbered from 1 within the current episode.</summary>
/// <param name="RepeatsPreviousArtifact">
/// True when this round judged the same bytes as the round before it — the producing agent changed
/// nothing. Worth surfacing: it is the difference between "iterating and still failing" and
/// "spinning in place", and an owner reading the escalation should not have to diff two digests
/// to tell them apart.
/// </param>
public sealed record RepairCycle(int Number, AgentVerdict Verdict, bool RepeatsPreviousArtifact);

/// <summary>
/// The repair budget for one ticket, derived from its comment trail. Nothing here is stored:
/// re-deriving it after an engine restart yields the same numbers, because the evidence is the
/// verdicts themselves.
/// </summary>
public sealed record RepairLoopState(int MaxCycles, IReadOnlyList<RepairCycle> Cycles)
{
    /// <summary>FIX verdicts in the current episode — one per completed review round.</summary>
    public int CyclesUsed => Cycles.Count;

    public int Remaining => Math.Max(0, MaxCycles - CyclesUsed);

    /// <summary>True once the loop must escalate instead of re-dispatching.</summary>
    public bool Exhausted => CyclesUsed >= MaxCycles;

    /// <summary>The FIX being repaired right now, or null when no repair is outstanding.</summary>
    public RepairCycle? Newest => Cycles.Count == 0 ? null : Cycles[^1];
}

/// <summary>
/// The bounded repair loop (C3). A <c>FIX</c> verdict sends the work back to the agent that
/// produced it with the failed categories and veto items attached; after <c>maxReviewCycles</c>
/// rounds the ticket escalates to the owner instead of looping forever.
/// <para>
/// <b>Where the count comes from.</b> Not from a counter column and not from engine memory — from
/// the ticket's own comments. Every FIX verdict on the ticket since the episode opened is one
/// round. That makes the number auditable (an owner can recount it by reading the ticket),
/// restart-proof (the engine holds no state to lose) and immune to a resumed or re-triggered run
/// restarting the count, since a replayed run either posts another FIX — which counts — or posts
/// nothing.
/// </para>
/// <para>
/// <b>Where an episode starts.</b> A <c>SHIP</c> or <c>BLOCK</c> closes the episode: the work was
/// accepted or escalated, so a later FIX is a new round-one, not round three. The escalation
/// receipt this loop posts closes it too, so an owner who unblocks a ticket hands the agents a
/// fresh budget rather than a permanently spent one.
/// </para>
/// </summary>
public static class RepairLoop
{
    /// <summary>Cap used when the workspace declares no <c>maxReviewCycles</c> for the agent.</summary>
    public const int DefaultMaxCycles = 2;

    /// <summary>Marker line of the escalation receipt. Also the episode reset marker.</summary>
    public const string EscalationMarkerPrefix = "GIGACLAW-REPAIR v1";

    private static readonly Regex EscalationRegex = new(
        @"^GIGACLAW-REPAIR\s+v1\s+ticket-(?<ticket>[0-9]+)\s+escalated\s+(?<used>[0-9]+)/(?<max>[0-9]+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool IsEscalationReceipt(string? commentBody)
        => commentBody is not null && EscalationRegex.IsMatch(commentBody);

    /// <summary>
    /// Recounts the repair budget from the comment trail. <paramref name="agent"/> restricts the
    /// count to one reviewer, matching <c>verdictIs</c>'s own agent filter, so a second reviewer's
    /// FIX never spends the budget of the one being gated on.
    /// </summary>
    public static RepairLoopState Resolve(
        IReadOnlyList<VerdictComment> comments,
        int maxCycles,
        string? agent = null)
    {
        var cycles = new List<RepairCycle>();
        string? previousDigest = null;

        foreach (var comment in comments)
        {
            // The receipt this loop posts is a reset, not a verdict: after an escalation the
            // owner owns the ticket, and whatever happens next starts a new episode.
            if (IsEscalationReceipt(comment.Content))
            {
                cycles.Clear();
                previousDigest = null;
                continue;
            }

            if (!VerdictReader.ContainsMarker(comment.Content))
                continue;

            // An unreadable verdict is INVALID — the gate routes it to the owner. It is not a
            // repair round, and it must not silently spend the budget either.
            if (!VerdictReader.TryRead(comment.Content, out var verdict, out _))
                continue;

            if (!string.IsNullOrEmpty(agent) && !string.Equals(verdict!.Agent, agent, StringComparison.Ordinal))
                continue;

            if (verdict!.Decision != "FIX")
            {
                // SHIP: accepted. BLOCK: already escalated. Either way the episode is over.
                cycles.Clear();
                previousDigest = null;
                continue;
            }

            cycles.Add(new RepairCycle(
                cycles.Count + 1,
                verdict,
                previousDigest is not null && string.Equals(previousDigest, verdict.InputDigest, StringComparison.Ordinal)));
            previousDigest = verdict.InputDigest;
        }

        return new RepairLoopState(maxCycles, cycles);
    }

    /// <summary>
    /// Reads <c>maxReviewCycles</c> out of a <c>.agents/contracts.json</c> manifest: the first of
    /// <paramref name="agents"/> that declares one, else the manifest's <c>defaults</c> block.
    /// Both the nested (<c>{"agents":{…}}</c>) and the flat (<c>{"slug":{…}}</c>) manifest shapes
    /// are accepted, matching what a dispatch already tolerates.
    /// </summary>
    /// <returns>
    /// False when the manifest is present but cannot be understood — the caller escalates rather
    /// than looping on a guessed budget. True with a null <paramref name="cycles"/> means the
    /// manifest is readable and simply silent about these agents.
    /// </returns>
    public static bool TryReadMaxCycles(string? manifestJson, IReadOnlyList<string?> agents, out int? cycles)
    {
        cycles = null;
        if (string.IsNullOrWhiteSpace(manifestJson))
            return true;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var map = root;
            if (TryGetIgnoreCase(root, "agents", out var nested))
            {
                if (nested.ValueKind != JsonValueKind.Object)
                    return false;
                map = nested;
            }

            foreach (var agent in agents)
            {
                if (string.IsNullOrWhiteSpace(agent)) continue;
                if (!TryGetIgnoreCase(map, agent, out var contract) || contract.ValueKind != JsonValueKind.Object)
                    continue;
                if (ReadCycles(contract) is int declared)
                {
                    cycles = declared;
                    return true;
                }
            }

            if (TryGetIgnoreCase(root, "defaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
                cycles = ReadCycles(defaults);
            return true;
        }

        static int? ReadCycles(JsonElement element)
            => element.TryGetProperty("maxReviewCycles", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var declared)
                && declared >= 0
                ? declared
                : null;

        static bool TryGetIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }

    /// <summary>
    /// The repair brief prepended to the re-dispatch: what the reviewer refused and why, in the
    /// same "previous run's output, injected" shape as the handoff. Compact on purpose — the
    /// newest verdict in full, earlier rounds only as the veto codes that keep coming back.
    /// </summary>
    public static string RenderBrief(RepairLoopState state, string ticketReference)
    {
        var newest = state.Newest;
        if (newest is null)
            return "";

        var verdict = newest.Verdict;
        var sb = new StringBuilder();
        sb.AppendLine(
            $"[Repair round {newest.Number} of {state.MaxCycles} — {verdict.Agent} returned FIX on {ticketReference}]");
        if (!string.IsNullOrWhiteSpace(verdict.Summary))
            sb.AppendLine(verdict.Summary);
        sb.AppendLine("Fix everything below before handing the work back for review.");
        sb.AppendLine(state.Remaining switch
        {
            0 => "The repair budget is spent: the next pass escalates this ticket to the owner.",
            1 => "One more FIX verdict is allowed on this ticket; after that it escalates to the owner.",
            var n => $"{n} more FIX verdicts are allowed on this ticket; after that it escalates to the owner.",
        });

        if (newest.RepeatsPreviousArtifact)
        {
            sb.AppendLine();
            sb.AppendLine(
                "The reviewer judged the same bytes as the previous round — the last attempt did not "
                + "change the artifact. Change it, or say in the handoff why the finding is wrong.");
        }

        AppendFindings(sb, verdict);

        var earlier = state.Cycles
            .Take(state.Cycles.Count - 1)
            .SelectMany(cycle => cycle.Verdict.VetoItems.Select(veto => veto.Code))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (earlier.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Earlier rounds also vetoed: {string.Join(", ", earlier)}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The escalation comment. Carries the receipt marker plus <em>every</em> round's reasons, so
    /// an owner can see the whole argument on the ticket without opening a single run log.
    /// </summary>
    public static string RenderEscalation(RepairLoopState state, int ticketId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{EscalationMarkerPrefix} ticket-{ticketId} escalated {state.CyclesUsed}/{state.MaxCycles}");
        sb.AppendLine();

        if (state.CyclesUsed == 0)
        {
            sb.AppendLine(
                "The repair loop escalated with no FIX verdict on record — the repair budget could "
                + "not be established, so the ticket goes to the owner rather than round again.");
            return sb.ToString().TrimEnd();
        }

        var reviewers = state.Cycles
            .Select(cycle => cycle.Verdict.Agent)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        sb.AppendLine(
            $"The repair loop is exhausted: {string.Join(" / ", reviewers)} returned FIX "
            + $"{state.CyclesUsed} time{(state.CyclesUsed == 1 ? "" : "s")} without a SHIP, and the cap "
            + $"(`maxReviewCycles`) is {state.MaxCycles}. Every round's reasons are below — the agents "
            + "could not close them on their own.");

        if (state.Cycles.Any(cycle => cycle.RepeatsPreviousArtifact))
        {
            sb.AppendLine();
            sb.AppendLine(
                "At least one round re-reviewed an unchanged artifact: the loop was spinning in "
                + "place rather than converging.");
        }

        foreach (var cycle in state.Cycles)
        {
            var verdict = cycle.Verdict;
            sb.AppendLine();
            sb.AppendLine(
                $"### Round {cycle.Number}/{state.MaxCycles} — {verdict.Agent}, FIX, "
                + $"{verdict.ReviewedAtUtc:yyyy-MM-dd HH:mm}Z");
            sb.AppendLine($"Artifact reviewed: `{verdict.InputDigest}`"
                + (cycle.RepeatsPreviousArtifact ? " (unchanged since the previous round)" : ""));
            if (!string.IsNullOrWhiteSpace(verdict.Summary))
                sb.AppendLine(verdict.Summary);

            AppendFindings(sb, verdict);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendFindings(StringBuilder sb, AgentVerdict verdict)
    {
        if (verdict.VetoItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Veto items (any one of them forbids SHIP):");
            foreach (var veto in verdict.VetoItems)
            {
                var refs = veto.EvidenceRefs.Count > 0 ? $" [{string.Join(", ", veto.EvidenceRefs)}]" : "";
                sb.AppendLine($"- {veto.Code}: {veto.Statement}{refs}");
            }
        }

        var unmet = verdict.Categories.Where(category => category.Score < category.Max).ToList();
        if (unmet.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Categories below their maximum:");
            foreach (var category in unmet)
            {
                var notes = string.IsNullOrWhiteSpace(category.Notes) ? "" : $" — {category.Notes}";
                sb.AppendLine($"- {category.Name} {Format(category.Score)}/{Format(category.Max)}{notes}");
            }
        }
    }

    private static string Format(double value)
        => value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}

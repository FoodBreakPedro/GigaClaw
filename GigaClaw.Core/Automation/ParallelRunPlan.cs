using GigaClaw.Core.Models;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Translates a <see cref="ParallelRunAgentsActionSpec"/> into the ad-hoc
/// <see cref="TeamDefinition"/> the run is executed as.
/// <para>
/// The whole point of C5 part 1 is that <c>parallelRunAgents</c> adds <b>vocabulary</b>, not a second
/// execution engine. Everything after this translation — fan-out to sub-tickets, dependency edges,
/// the join policy, cancellation propagation, synthesize-with-gaps, the restart reconcile — is the
/// C4 machinery, unchanged. This file is the only thing that stands between the two, and it is
/// I/O-free so the automation editor, the executor and the tests share one reading of a spec.
/// </para>
/// </summary>
internal static class ParallelRunPlan
{
    /// <summary>Role id given to the synthesizer. A branch may not claim it.</summary>
    public const string SynthesizerRoleId = "synthesizer";

    /// <summary>
    /// Builds the definition, or throws <see cref="ParallelRunPlanException"/> naming every problem
    /// at once. Structural problems the team model already knows about (quorum out of range,
    /// duplicate keys) are left to <see cref="TeamDefinition.Validate"/> rather than re-implemented.
    /// </summary>
    public static TeamDefinition ToDefinition(ParallelRunAgentsActionSpec spec)
    {
        var problems = new List<string>();

        if (spec.Branches.Count == 0)
            problems.Add("parallelRunAgents needs at least one branch.");

        var slug = string.IsNullOrWhiteSpace(spec.RunSlug) ? "parallel-run" : spec.RunSlug.Trim();

        var roles = new List<TeamRole>();
        var tasks = new List<TeamTaskTemplate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (branch, index) in spec.Branches.Select((branch, index) => (branch, index)))
        {
            var agent = branch.Agent?.Trim() ?? "";
            if (agent.Length == 0)
            {
                problems.Add($"Branch {index + 1} has no agent.");
                continue;
            }

            var key = string.IsNullOrWhiteSpace(branch.Key) ? agent : branch.Key.Trim();
            if (string.Equals(key, SynthesizerRoleId, StringComparison.OrdinalIgnoreCase))
            {
                // The synthesizer's seat is reserved; a branch taking it would produce a duplicate
                // role and a synthesis dispatched to the wrong agent.
                problems.Add($"Branch {index + 1} may not use the reserved key '{SynthesizerRoleId}'.");
                continue;
            }
            if (!seen.Add(key))
            {
                problems.Add(
                    $"Branch key '{key}' is used twice. Two branches on the same agent need explicit, distinct keys.");
                continue;
            }

            roles.Add(new TeamRole(key, agent));
            tasks.Add(new TeamTaskTemplate(
                key,
                key,
                string.IsNullOrWhiteSpace(branch.Title) ? agent : branch.Title!.Trim())
            {
                Prompt = branch.Prompt
            });
        }

        var synthesizer = spec.Synthesizer?.Trim();
        if (!string.IsNullOrEmpty(synthesizer))
            roles.Add(new TeamRole(SynthesizerRoleId, synthesizer));

        if (!TryReadJoin(spec, out var join))
            problems.Add($"Unknown join policy '{spec.Join}'. Use allDone, quorum or firstFailure.");

        if (!TryReadPartialFailure(spec, out var partialFailure))
            problems.Add($"Unknown onPartialFailure '{spec.OnPartialFailure}'. Use synthesize or failFast.");

        if (spec.MaxConcurrency < 0)
            problems.Add($"maxConcurrency {spec.MaxConcurrency} is negative; use 0 for unlimited.");

        var definition = new TeamDefinition(
            slug,
            string.IsNullOrWhiteSpace(spec.Name) ? slug : spec.Name!.Trim(),
            "Ad-hoc parallel branches declared by a parallelRunAgents automation.",
            "⑂")
        {
            Roles = roles,
            TaskGraph = tasks,
            JoinPolicy = join,
            SynthesizerRole = string.IsNullOrEmpty(synthesizer) ? null : SynthesizerRoleId,
            PartialFailure = partialFailure,
            MaxConcurrency = spec.MaxConcurrency
        };

        // Only ask the model for its verdict once the spec-level problems are known, so a spec with
        // no branches reports "needs at least one branch" rather than a cascade of graph errors.
        if (problems.Count == 0)
            problems.AddRange(definition.Validate());

        if (problems.Count > 0)
            throw new ParallelRunPlanException(string.Join(" ", problems));

        return definition;
    }

    private static bool TryReadJoin(ParallelRunAgentsActionSpec spec, out TeamJoinPolicy policy)
    {
        switch ((spec.Join ?? "").Trim().ToLowerInvariant())
        {
            case "" or "alldone":
                policy = TeamJoinPolicy.AllDone;
                return true;
            case "quorum":
                policy = TeamJoinPolicy.OfQuorum(spec.Quorum);
                return true;
            case "firstfailure":
                policy = TeamJoinPolicy.FirstFailure;
                return true;
            default:
                // Fails loudly rather than defaulting: a typo that silently became all-done would
                // change what the run waits for without saying so.
                policy = TeamJoinPolicy.AllDone;
                return false;
        }
    }

    private static bool TryReadPartialFailure(ParallelRunAgentsActionSpec spec, out TeamPartialFailure mode)
    {
        switch ((spec.OnPartialFailure ?? "").Trim().ToLowerInvariant())
        {
            case "" or "synthesize":
                mode = TeamPartialFailure.Synthesize;
                return true;
            case "failfast":
                mode = TeamPartialFailure.FailFast;
                return true;
            default:
                mode = TeamPartialFailure.Synthesize;
                return false;
        }
    }
}

/// <summary>Raised when a <c>parallelRunAgents</c> spec cannot describe a runnable set of branches.</summary>
public sealed class ParallelRunPlanException(string message) : InvalidOperationException(message);

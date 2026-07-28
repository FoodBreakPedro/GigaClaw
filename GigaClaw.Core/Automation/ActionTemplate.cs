using GigaClaw.Core.Automation.Triggers;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Mutable state shared by every action of one automation chain.
/// <para>
/// Beyond the status/assignee snapshot used to revert a ticket when a run fails, it carries
/// <see cref="ChainValues"/> — the output→input bag that lets a later action template a value
/// produced by an earlier one (e.g. an <c>addComment</c> writing back the CMS admin URL an
/// <c>httpRequest</c> just received). Chain values live only for the duration of the chain and
/// are never persisted.
/// </para>
/// <para>
/// One instance per chain, shared with the detached-chain task and with the post-run actions
/// that follow a <c>runAgent</c>. Action specs themselves are shared, mutable objects loaded
/// from disk and must never be written to — all substitution happens into locals at execution time.
/// </para>
/// </summary>
internal sealed class ActionState
{
    public AgentRun? LastRun;
    public string? StatusBeforeMove;
    public string? StatusAfterMove;
    public string? AssigneeBeforeMove;

    /// <summary>
    /// Values published by earlier actions, keyed without braces (<c>http.status</c>,
    /// <c>http.body</c>, <c>http.body.id</c>, …) and referenced in templates as
    /// <c>{http.status}</c>. Later writes overwrite earlier ones, so a second httpRequest
    /// in the same chain replaces the first one's capture.
    /// </summary>
    public readonly Dictionary<string, string> ChainValues = new(StringComparer.Ordinal);
}

/// <summary>
/// The single placeholder-substitution point for automation actions.
/// <para>
/// Every call site passes the exact placeholder set it supports; unknown placeholders are left
/// verbatim in the output (a template referencing <c>{other}</c> keeps <c>{other}</c>), which is
/// the historical behavior of the ad-hoc <c>string.Replace</c> chains this replaces.
/// </para>
/// </summary>
public static class ActionTemplate
{
    /// <summary>
    /// Substitutes <c>{key}</c> with the matching value. A null value renders as the empty string.
    /// Keys absent from <paramref name="values"/> are left untouched.
    /// </summary>
    public static string Render(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        var result = template;
        foreach (var (key, value) in values)
        {
            var token = "{" + key + "}";
            if (result.Contains(token, StringComparison.Ordinal))
                result = result.Replace(token, value ?? string.Empty, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Renders the placeholder set common to ticket-scoped actions — <c>{ticketId}</c> and
    /// <c>{ticketTitle}</c> — plus every chain value captured so far.
    /// </summary>
    internal static string Render(string? template, ActionState? state, TriggerFiring? firing) =>
        Render(template, Values(state,
            ("ticketId", firing?.TicketId?.ToString() ?? ""),
            ("ticketTitle", firing?.TicketTitle ?? "")));

    /// <summary>
    /// Builds a value bag from the chain values captured so far plus the caller's own
    /// placeholders. Caller-supplied entries win over chain values of the same name.
    /// </summary>
    internal static Dictionary<string, string?> Values(
        ActionState? state,
        params (string Key, string? Value)[] placeholders)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (state is not null)
        {
            foreach (var (key, value) in state.ChainValues)
                values[key] = value;
        }
        foreach (var (key, value) in placeholders)
            values[key] = value;
        return values;
    }
}

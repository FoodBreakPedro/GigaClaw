namespace GigaClaw.Core.Automation.Policy;

/// <summary>
/// U17's host-side half. Claude's <c>PreToolUse</c> hook governs what the *agent* does, but the
/// <c>httpRequest</c> automation action runs in the GigaClaw process, where no hook can see it —
/// so outbound automation traffic needs its own preflight.
/// <para>
/// The trust anchor is deliberately not anything an agent can reach. A ticket label will not do:
/// agents hold board-write and can set labels, so gating on one lets the thing being governed
/// authorise itself. The approved-host list lives in the owner's app settings
/// (<c>%APPDATA%/GigaClaw/settings.json</c>), which sits outside every workspace and therefore
/// outside every agent's write globs.
/// </para>
/// <para>
/// Default is deny: an empty list means every outbound request is a dry run. That is the point of
/// "dry-run by default" — a fresh install cannot silently call out, and enabling one host is a
/// deliberate owner action rather than an omission.
/// </para>
/// </summary>
public sealed class OutboundApprovalGate
{
    private readonly Func<IReadOnlyCollection<string>> _approvedHosts;

    public OutboundApprovalGate(Func<IReadOnlyCollection<string>> approvedHosts)
    {
        ArgumentNullException.ThrowIfNull(approvedHosts);
        _approvedHosts = approvedHosts;
    }

    /// <summary>
    /// Decides whether a rendered request URL may actually be sent. Returns the reason it may not
    /// when it may not, so the caller can write a receipt that says which host to approve.
    /// </summary>
    public OutboundApprovalDecision Evaluate(string? renderedUrl)
    {
        if (string.IsNullOrWhiteSpace(renderedUrl))
            return OutboundApprovalDecision.DryRun("httpRequest has no URL to send to.");

        if (!Uri.TryCreate(renderedUrl, UriKind.Absolute, out var uri))
        {
            return OutboundApprovalDecision.DryRun(
                $"httpRequest URL '{renderedUrl}' is not an absolute URI.");
        }

        // A placeholder that never got substituted would otherwise render as a literal host and
        // could match nothing — or, worse, match an approved host by accident.
        if (renderedUrl.Contains('{') || renderedUrl.Contains('}'))
        {
            return OutboundApprovalDecision.DryRun(
                $"httpRequest URL '{renderedUrl}' still contains an unsubstituted placeholder.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return OutboundApprovalDecision.DryRun(
                $"httpRequest scheme '{uri.Scheme}' is not http or https.");
        }

        var host = uri.Host;
        var approved = _approvedHosts();
        if (approved.Count == 0)
        {
            return OutboundApprovalDecision.DryRun(
                $"No outbound hosts are approved, so '{host}' is dry-run. " +
                "Approve it in settings to let this request send.");
        }

        // Exact host match only. Suffix matching would make 'notexample.com' satisfy an approval
        // for 'example.com', and a wildcard would let one approval cover hosts the owner never saw.
        foreach (var candidate in approved)
        {
            if (string.Equals(candidate?.Trim(), host, StringComparison.OrdinalIgnoreCase))
                return OutboundApprovalDecision.Approved();
        }

        return OutboundApprovalDecision.DryRun(
            $"Host '{host}' is not in the approved outbound list, so the request is dry-run.");
    }
}

/// <summary>
/// The preflight result. A dry run is not a failure — the automation did exactly what it was
/// configured to do — so it carries a reason rather than an error.
/// </summary>
public sealed record OutboundApprovalDecision(bool MaySend, string? Reason)
{
    public static OutboundApprovalDecision Approved() => new(true, null);
    public static OutboundApprovalDecision DryRun(string reason) => new(false, reason);
}

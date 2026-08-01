using System.Text.Json;
using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Automation.Policy;

/// <summary>
/// The queryable record of an outbound call the <see cref="OutboundApprovalGate"/> refused to let
/// leave the process. Same <c>outbound-denial/v1</c> shape the <c>httpRequest</c> action writes
/// inline (see <c>ActionExecutor.WriteOutboundDenialReceiptAsync</c>) — P3's contract is that a
/// denial produces a receipt, so any second outbound surface must produce the same one rather than
/// invent a private format.
/// </summary>
public sealed record OutboundReceipt(
    string Agent,
    string Action,
    string Target,
    string Host,
    string Reason,
    string Rule = "outbound-approval",
    string EnforcementMode = "dry-run")
{
    public const string Schema = "outbound-denial/v1";

    /// <summary>The serialized comment body, byte-compatible with the httpRequest receipt.</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schema = Schema,
        agent = Agent,
        action = Action,
        target = Target,
        host = Host,
        rule = Rule,
        reason = Reason,
        enforcementMode = EnforcementMode,
    });
}

/// <summary>
/// Where outbound denial receipts go. Abstracted (unlike the httpRequest action's inline writer)
/// because the GitHub surface denies calls that have no firing ticket to comment on — an issue
/// import is refused before any ticket exists — and a receipt that can only be a ticket comment
/// would silently vanish in exactly that case.
/// </summary>
public interface IOutboundReceiptSink
{
    Task WriteAsync(string projectSlug, int? ticketId, OutboundReceipt receipt, CancellationToken ct = default);
}

/// <summary>
/// Default sink: the log line always, plus the ticket comment when the denial belongs to a ticket.
/// Mirrors <c>ActionExecutor.WriteOutboundDenialReceiptAsync</c> exactly, so a reader grepping for
/// <c>OUTBOUND DRY-RUN</c> or for <c>outbound-denial/v1</c> finds both surfaces.
/// </summary>
public sealed class TicketOutboundReceiptSink : IOutboundReceiptSink
{
    private readonly TicketService _tickets;
    private readonly ILogger<TicketOutboundReceiptSink> _logger;

    public TicketOutboundReceiptSink(TicketService tickets, ILogger<TicketOutboundReceiptSink> logger)
    {
        _tickets = tickets;
        _logger = logger;
    }

    public async Task WriteAsync(string projectSlug, int? ticketId, OutboundReceipt receipt, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "OUTBOUND DRY-RUN agent={Agent} action={Action} target={Target} rule={Rule} reason={Reason}",
            receipt.Agent, receipt.Action, receipt.Target, receipt.Rule, receipt.Reason);

        if (ticketId is not int id) return;
        try { await _tickets.AddCommentAsync(projectSlug, id, receipt.ToJson(), "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write outbound-denial receipt for ticket #{Id}", id);
        }
    }
}

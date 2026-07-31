using System.Net.Http.Headers;
using System.Text;
using GigaClaw.Core.Automation.Policy;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Github;

/// <summary>One outbound GitHub call, before the policy layer has had its say.</summary>
/// <param name="Actor">Who is credited on the receipt if the call is refused.</param>
/// <param name="TicketId">Ticket the call belongs to, when there is one — receipts land there.</param>
public sealed record GitHubRequest(
    string ProjectSlug,
    HttpMethod Method,
    string Url,
    string Token,
    string? JsonBody = null,
    int? TicketId = null,
    string Actor = "github-sync");

/// <summary>
/// The outcome of a GitHub call. <see cref="DryRun"/> is not a failure: it means the policy layer
/// refused to let the request leave the process and wrote a receipt saying which host to approve.
/// </summary>
public sealed record GitHubResponse(
    bool Sent,
    int Status,
    string Body,
    bool DryRun = false,
    string? Error = null)
{
    public bool Success => Sent && Status is >= 200 and < 300;

    public static GitHubResponse Refused(string reason) => new(false, 0, "", DryRun: true, Error: reason);
    public static GitHubResponse Failed(string error) => new(true, 0, "", Error: error);
}

/// <summary>
/// The single door every GitHub call goes through (C7 acceptance criterion: "network calls pass the
/// P3 policy layer"). There is no other outbound path in <c>GigaClaw.Core/Github/</c> — the sync
/// service and both triggers hold a <see cref="GitHubApiClient"/>, never an
/// <see cref="HttpClient"/>, so the <see cref="OutboundApprovalGate"/> preflight cannot be bypassed
/// by adding one more caller.
/// <para>
/// <b>Token discipline.</b> The PAT is attached to the <c>Authorization</c> header of the outgoing
/// message and nowhere else. It is never placed in a URL (which would put it in the receipt's
/// <c>target</c> and in every log line), and <see cref="Redact"/> scrubs it from any exception
/// message before that message becomes a <see cref="GitHubResponse.Error"/> — errors do end up on
/// tickets.
/// </para>
/// </summary>
public sealed class GitHubApiClient
{
    /// <summary>Named <see cref="HttpClient"/>; tests swap its primary handler for a fake.</summary>
    public const string HttpClientName = "github-surface";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OutboundApprovalGate _gate;
    private readonly IOutboundReceiptSink _receipts;
    private readonly ILogger<GitHubApiClient> _logger;

    public GitHubApiClient(
        IHttpClientFactory httpClientFactory,
        OutboundApprovalGate gate,
        IOutboundReceiptSink receipts,
        ILogger<GitHubApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _receipts = receipts;
        _logger = logger;
    }

    public async Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = request.Url?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return GitHubResponse.Failed($"'{url}' is not an absolute URL");

        // P3 preflight, identical in effect to the httpRequest action's: no trusted approval for
        // this host means nothing leaves the process, and the refusal is receipted rather than
        // swallowed. Read per call, so an owner approval takes effect on the next poll.
        var approval = _gate.Evaluate(url);
        if (!approval.MaySend)
        {
            var reason = approval.Reason ?? "no trusted outbound approval";
            await _receipts.WriteAsync(
                request.ProjectSlug,
                request.TicketId,
                new OutboundReceipt(
                    Agent: request.Actor,
                    Action: "githubRequest",
                    Target: url,
                    Host: uri.Host,
                    Reason: reason),
                ct);
            return GitHubResponse.Refused(reason);
        }

        try
        {
            using var message = new HttpRequestMessage(request.Method, uri);
            // The PAT lives here and only here.
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Token);
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.UserAgent.ParseAdd("GigaClaw");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (request.JsonBody is not null)
                message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            return new GitHubResponse(
                Sent: true,
                Status: status,
                Body: body,
                Error: status is >= 200 and < 300 ? null : $"GitHub returned {status}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var scrubbed = Redact(exception.Message, request.Token);
            _logger.LogWarning("GitHub {Method} {Url} failed: {Error}", request.Method, uri, scrubbed);
            return GitHubResponse.Failed(scrubbed);
        }
    }

    /// <summary>
    /// Replaces the token with a fixed marker wherever it appears. Applied to every error string
    /// that can reach a ticket, because a transport exception can echo back a request header and a
    /// receipt on a ticket is exactly the place the token must never be.
    /// </summary>
    internal static string Redact(string? text, string? token)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (string.IsNullOrEmpty(token)) return text;
        return text.Replace(token, "[redacted]", StringComparison.Ordinal);
    }
}

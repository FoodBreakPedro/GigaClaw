using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Runners;
using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

internal sealed partial class ActionExecutor
{
    // ── httpRequest ─────────────────────────────────────────────────────────

    /// <summary>Upper bound on the raw body published as <c>{http.body}</c>, so a large response
    /// can't be pasted wholesale into a ticket comment.</summary>
    private const int MaxCapturedBodyChars = 8192;

    /// <summary>
    /// Performs the outbound request and publishes <c>http.status</c>, <c>http.body</c> and the
    /// flattened <c>http.body.&lt;field&gt;</c> values into the chain. When <see cref="HttpRequestActionSpec.BodyTemplate"/>
    /// references <c>{draft.*}</c>, the firing ticket's description is fetched and parsed as
    /// <see cref="DraftFrontmatter"/> first — a parse failure fails the action the same way a
    /// failed request does, without ever sending it. Returns true when the caller should abort
    /// the rest of the chain (failure + AbortOnFailure).
    /// </summary>
    private async Task<bool> ExecuteHttpRequestAsync(
        HttpRequestActionSpec spec,
        string slug,
        TriggerFiring firing,
        ActionState? state,
        string actor,
        CancellationToken ct)
    {
        // Substitution always targets locals — the spec objects are the shared, mutable instances
        // held by the chain snapshot and by the on-disk config, and must never be written to.
        var commonValues = ActionTemplate.Values(
            state,
            ("ticketId", firing.TicketId?.ToString() ?? ""),
            ("ticketTitle", firing.TicketTitle ?? ""),
            ("slug", slug ?? ""),
            ("projectSlug", slug ?? ""));
        string Render(string? s) => ActionTemplate.Render(s, commonValues);

        void Publish(string key, string value)
        {
            if (state is not null) state.ChainValues[key] = value;
        }

        // Deterministic values so a template referencing {http.status} never renders the raw
        // placeholder when the request never produced a response.
        Publish("http.status", "0");
        Publish("http.body", "");

        // Posts the failure receipt (comment + status move) this spec was configured with, then
        // returns whether the caller should abort the rest of the chain. Shared by every failure
        // exit below — non-2xx, transport/timeout, bad URL, and frontmatter parse failure — so
        // FailureComment/FailureStatus behave identically regardless of which one fired.
        async Task<bool> FailAsync(string httpError)
        {
            Publish("http.error", httpError);
            if (firing.TicketId is int failedTicketId)
            {
                if (!string.IsNullOrWhiteSpace(spec.FailureComment))
                {
                    var content = ActionTemplate.Render(spec.FailureComment, state, firing);
                    try { await _tickets.AddCommentAsync(slug!, failedTicketId, content, "automation"); }
                    catch (Exception ex) { _logger.LogWarning(ex, "httpRequest: failed to post FailureComment for ticket #{Id}", failedTicketId); }
                }
                if (!string.IsNullOrWhiteSpace(spec.FailureStatus))
                {
                    try { await _tickets.MoveTicketAsync(slug!, failedTicketId, spec.FailureStatus, "automation"); }
                    catch (Exception ex) { _logger.LogWarning(ex, "httpRequest: failed to apply FailureStatus for ticket #{Id}", failedTicketId); }
                }
            }
            return spec.AbortOnFailure;
        }

        if (TryFindUnresolvedTemplateToken(spec.Url, commonValues, out var unresolvedUrlToken))
        {
            _logger.LogWarning(
                "httpRequest: URL still contains unresolved placeholder '{Token}' — request not sent",
                unresolvedUrlToken);
            return await FailAsync($"unresolved URL placeholder {unresolvedUrlToken}");
        }
        var url = Render(spec.Url).Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("httpRequest: '{Url}' is not an absolute http(s) URL — skipping", url);
            return await FailAsync($"invalid URL '{url}'");
        }

        // U17/R3 host-side preflight. The trust anchor is the owner's app-level settings.json —
        // never a ticket label, which agents holding board-write can set themselves. Without a
        // trusted approval for this host, the action is a dry run: logged and receipted, but
        // nothing leaves the process. The approved-host list is read per execution, so an owner
        // edit takes effect on the next firing without an engine restart.
        var approval = _outboundGate.Evaluate(url);
        if (!approval.MaySend)
        {
            var reason = approval.Reason ?? "no trusted outbound approval";
            Publish("http.dryRun", "true");
            Publish("http.error", reason);
            await WriteOutboundDenialReceiptAsync(slug!, firing, actor, url, uri.Host, reason);
            // Not sent means no response: actions downstream that assume a successful send must
            // not run when the spec opted into abort-on-failure. The receipt above — not the
            // spec's FailureComment/FailureStatus — is the record, because a dry run is the
            // configured behavior of an unapproved host, not a dispatch failure.
            return spec.AbortOnFailure;
        }

        var timeout = TimeSpan.FromSeconds(spec.TimeoutSeconds > 0 ? spec.TimeoutSeconds : 30);

        try
        {
            // {draft.*} is only recognized in BodyTemplate — a frontmatter parse failure must
            // block dispatch (never POST a malformed draft) rather than silently leaving the
            // placeholders un-rendered.
            var bodyTemplate = spec.BodyTemplate ?? "";
            string body;
            if (bodyTemplate.Contains("{draft.", StringComparison.Ordinal))
            {
                var ticket = firing.TicketId is int draftTicketId
                    ? await _tickets.GetTicketAsync(slug!, draftTicketId)
                    : null;
                if (!DraftFrontmatter.TryParse(ticket?.Description, out var draft, out var parseError))
                {
                    _logger.LogWarning(
                        "httpRequest: draft frontmatter parse failed for ticket #{Id}: {Error} — request not sent",
                        firing.TicketId, parseError);
                    return await FailAsync($"frontmatter: {parseError}");
                }

                var values = new Dictionary<string, string?>(commonValues, StringComparer.Ordinal);
                foreach (var (key, value) in draft!.ToJsonEscapedValues())
                    values[key] = value;
                if (TryFindUnresolvedTemplateToken(bodyTemplate, values, out var unresolvedBodyToken))
                {
                    _logger.LogWarning(
                        "httpRequest: body contains unresolved placeholder '{Token}' — request not sent",
                        unresolvedBodyToken);
                    return await FailAsync($"unresolved body placeholder {unresolvedBodyToken}");
                }
                body = ActionTemplate.Render(bodyTemplate, values);
            }
            else
            {
                if (TryFindUnresolvedTemplateToken(bodyTemplate, commonValues, out var unresolvedBodyToken))
                {
                    _logger.LogWarning(
                        "httpRequest: body contains unresolved placeholder '{Token}' — request not sent",
                        unresolvedBodyToken);
                    return await FailAsync($"unresolved body placeholder {unresolvedBodyToken}");
                }
                body = Render(bodyTemplate);
            }

            var methodTemplate = string.IsNullOrWhiteSpace(spec.Method) ? "POST" : spec.Method.Trim();
            if (TryFindUnresolvedTemplateToken(methodTemplate, commonValues, out var unresolvedMethodToken))
                return await FailAsync($"unresolved method placeholder {unresolvedMethodToken}");
            using var request = new HttpRequestMessage(
                new HttpMethod(Render(methodTemplate).ToUpperInvariant()),
                uri);

            string? contentType = null;
            var hasAuthorization = false;
            var renderedHeaders = new List<(string Name, string Value)>();
            foreach (var (name, rawValue) in spec.Headers)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (TryFindUnresolvedTemplateToken(name, commonValues, out var unresolvedHeaderNameToken))
                    return await FailAsync($"unresolved header name placeholder {unresolvedHeaderNameToken}");
                if (TryFindUnresolvedTemplateToken(rawValue, commonValues, out var unresolvedHeaderToken))
                    return await FailAsync($"unresolved header placeholder {unresolvedHeaderToken}");

                var renderedName = Render(name);
                var value = Render(rawValue);
                if (string.Equals(renderedName, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = value;
                    continue;
                }
                if (string.Equals(renderedName, "Authorization", StringComparison.OrdinalIgnoreCase))
                    hasAuthorization = true;
                renderedHeaders.Add((renderedName, value));
            }

            foreach (var (name, value) in renderedHeaders)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    _logger.LogWarning("httpRequest: header '{Header}' was rejected — skipping it", name);
            }

            if (!string.IsNullOrWhiteSpace(spec.SecretRef) && hasAuthorization)
            {
                _logger.LogDebug("httpRequest: explicit Authorization header present — ignoring secretRef '{Name}'", spec.SecretRef);
            }
            else if (!string.IsNullOrWhiteSpace(spec.SecretRef))
            {
                // Only the variable NAME is ever logged; the token itself is never written anywhere.
                var token = Environment.GetEnvironmentVariable(spec.SecretRef);
                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning(
                        "httpRequest: secretRef '{Name}' is not set on the server — request not sent",
                        spec.SecretRef);
                    return await FailAsync($"secretRef '{spec.SecretRef}' is not set");
                }
                else
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(
                    body, System.Text.Encoding.UTF8, contentType ?? "application/json");
            }

            var client = _httpClientFactory.CreateClient(HttpRequestActionSpec.HttpClientName);
            client.Timeout = timeout;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            var status = (int)response.StatusCode;
            Publish("http.status", status.ToString());

            var raw = (await response.Content.ReadAsStringAsync(timeoutCts.Token)).Trim();
            CaptureResponseBody(raw, Publish);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "httpRequest {Method} {Url} returned {Status}; abortOnFailure={Abort}",
                    request.Method, uri, status, spec.AbortOnFailure);
                return await FailAsync($"HTTP {status}");
            }

            _logger.LogInformation("httpRequest {Method} {Url} returned {Status}", request.Method, uri, status);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Engine shutdown, not a dispatch failure — leave the ticket alone.
            _logger.LogWarning("httpRequest to {Url} cancelled (engine shutdown)", uri);
            return spec.AbortOnFailure;
        }
        catch (OperationCanceledException)
        {
            // Both HttpClient.Timeout and the linked CTS surface here.
            _logger.LogWarning("httpRequest to {Url} timed out after {Timeout}s; abortOnFailure={Abort}",
                uri, timeout.TotalSeconds, spec.AbortOnFailure);
            return await FailAsync($"timed out after {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "httpRequest to {Url} failed; abortOnFailure={Abort}", uri, spec.AbortOnFailure);
            return await FailAsync(ex.Message);
        }
    }

    private static bool TryFindUnresolvedTemplateToken(
        string? template,
        IReadOnlyDictionary<string, string?> values,
        out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(template)) return false;

        foreach (Match match in UnresolvedTemplateTokenRegex().Matches(template))
        {
            var key = match.Value[1..^1];
            if (values.ContainsKey(key)) continue;

            token = match.Value;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9_.-]{0,127}\}", RegexOptions.CultureInvariant)]
    private static partial Regex UnresolvedTemplateTokenRegex();

    /// <summary>
    /// Writes the queryable denial receipt for an outbound dry run: a structured
    /// <c>outbound-denial/v1</c> ticket comment naming agent, action, target, and rule —
    /// the same "denials produce receipts just like warnings" contract as the R2
    /// <c>policy-violation/v1</c> run events. Firings without a ticket still get the log line.
    /// </summary>
    private async Task WriteOutboundDenialReceiptAsync(
        string slug, TriggerFiring firing, string actor, string url, string host, string reason)
    {
        _logger.LogWarning(
            "OUTBOUND DRY-RUN agent={Agent} action=httpRequest target={Target} rule=outbound-approval reason={Reason}",
            actor, url, reason);

        if (firing.TicketId is not int ticketId) return;

        var receipt = JsonSerializer.Serialize(new
        {
            schema = "outbound-denial/v1",
            agent = actor,
            action = "httpRequest",
            target = url,
            host,
            rule = "outbound-approval",
            reason,
            enforcementMode = "dry-run",
        });

        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "httpRequest: failed to write outbound-denial receipt for ticket #{Id}", ticketId);
        }
    }

    /// <summary>
    /// Publishes the response body into the chain: always the raw trimmed text as
    /// <c>http.body</c>, plus one <c>http.body.&lt;field&gt;</c> per first-level field when the
    /// response is a JSON object. Malformed JSON is not an error — only the raw value is stored.
    /// </summary>
    private static void CaptureResponseBody(string raw, Action<string, string> publish)
    {
        publish("http.body", raw.Length <= MaxCapturedBodyChars ? raw : raw[..MaxCapturedBodyChars] + "…");
        if (raw.Length == 0 || raw[0] != '{') return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                publish($"http.body.{prop.Name}", prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null   => "",
                    // Objects and arrays keep their JSON text so nothing is silently lost.
                    _                    => prop.Value.GetRawText(),
                });
            }
        }
        catch (JsonException)
        {
            // Body claimed to be JSON but isn't — the raw capture above is all we can offer.
        }
    }
}

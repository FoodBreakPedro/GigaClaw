using System.Net;
using System.Net.Http;

namespace GigaClaw.Core.Tests.Helpers;

/// <summary>
/// Records every outbound request and replies with a scripted response, so httpRequest action
/// tests never touch the network. Handing the handler to <see cref="FakeHttpClientFactory"/>
/// gives ActionExecutor a fully in-memory IHttpClientFactory.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responder = responder;

    /// <summary>Always replies with the given status and body.</summary>
    public static FakeHttpMessageHandler Respond(HttpStatusCode status, string body, string contentType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        }));

    /// <summary>Never replies until cancelled — exercises the timeout path.</summary>
    public static FakeHttpMessageHandler Hang() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        });

    /// <summary>Fails at the transport layer.</summary>
    public static FakeHttpMessageHandler Throw(Exception ex) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(ex));

    /// <summary>Requests seen by this handler, in order. Bodies are buffered before dispatch.</summary>
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public HttpRequestMessage LastRequest => Requests[^1].Request;
    public string? LastBody => Requests[^1].Body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Read the content now: it is disposed with the request once SendAsync returns.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return await _responder(request, ct);
    }

    private sealed class UnreachableException : Exception;
}

/// <summary>Minimal IHttpClientFactory returning a client over a fixed handler.</summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>Factory for the many tests that never issue a request.</summary>
    public static FakeHttpClientFactory Unused =>
        new(FakeHttpMessageHandler.Throw(new InvalidOperationException(
            "This test's ActionExecutor was not expected to make an HTTP request.")));

    // disposeHandler:false — the shared handler outlives each per-call client.
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

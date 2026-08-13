using System.Net;

namespace McpConsultant.Tests;

/// <summary>Test seam for OpenRouterClient: canned responses, delays, or thrown exceptions.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler(HttpStatusCode status, string body)
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(CancellationToken.None);

        return await _responder(request, cancellationToken);
    }
}

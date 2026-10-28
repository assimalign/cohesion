using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// Answers every request with a scripted response and records the last request it saw.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public HttpMethod? Method { get; private set; }

    public Uri? RequestUri { get; private set; }

    public string? MediaType { get; private set; }

    public byte[] Body { get; private set; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        MediaType = request.Content?.Headers.ContentType?.MediaType;
        Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return _respond.Invoke(request);
    }
}

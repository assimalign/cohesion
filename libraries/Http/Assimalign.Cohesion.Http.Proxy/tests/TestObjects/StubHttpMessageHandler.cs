using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A message handler that answers in process: it records the forwarded request and its body, then
/// returns what the test's responder builds.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public HttpRequestMessage? Request { get; private set; }

    public byte[]? RequestBody { get; private set; }

    public static StubHttpMessageHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new StubHttpMessageHandler((request, _) => Task.FromResult(responder(request)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;

        if (request.Content is not null)
        {
            using MemoryStream body = new();
            await request.Content.CopyToAsync(body, cancellationToken);
            RequestBody = body.ToArray();
        }

        HttpResponseMessage response = await _responder(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

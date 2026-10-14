using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpContext"/> double for driving <c>UseWebSockets</c> without a server: real
/// header and feature collections, a configurable wire scheme and host, and, for a handshake, a
/// <see cref="FakeUpgradeFeature"/> standing in for the protocol-upgrade interceptor.
/// </summary>
internal sealed class PolicyTestContext : IHttpContext
{
    private readonly CancellationTokenSource _requestCancelled = new();

    public PolicyTestContext(HttpMethod method, HttpScheme scheme = HttpScheme.Https, string host = "app.example")
    {
        Request = new TestRequest(this, method, scheme, host);
        Response = new TestResponse(this);
    }

    /// <summary>
    /// Creates a valid HTTP/1.1 opening handshake carrying <paramref name="origin"/> (none when
    /// <see langword="null"/>), whose upgrade surrenders <paramref name="transport"/>.
    /// </summary>
    public static PolicyTestContext CreateHandshake(
        string? origin = null,
        HttpScheme scheme = HttpScheme.Https,
        string host = "app.example",
        Stream? transport = null)
    {
        PolicyTestContext context = new(HttpMethod.Get, scheme, host);
        context.Request.Headers[HttpHeaderKey.Connection] = "Upgrade";
        context.Request.Headers[HttpHeaderKey.Upgrade] = "websocket";
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = RawWebSocketClient.SampleKey;
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";

        if (origin is not null)
        {
            context.Request.Headers[HttpHeaderKey.Origin] = origin;
        }

        context.Upgrade = new FakeUpgradeFeature(transport ?? new MemoryStream(), context.Response.Headers);
        context.Features.Set(context.Upgrade);
        return context;
    }

    /// <summary>Gets the upgrade the context surfaces, for a handshake.</summary>
    public FakeUpgradeFeature? Upgrade { get; private set; }

    public HttpVersion Version => HttpVersion.Http11;

    public IHttpRequest Request { get; }

    public IHttpResponse Response { get; }

    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public CancellationToken RequestCancelled => _requestCancelled.Token;

    /// <summary>Gets a response header value, or <see langword="null"/> when it is absent.</summary>
    public string? ResponseHeader(HttpHeaderKey key)
        => Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? value.Value : null;

    public void Cancel() => _requestCancelled.Cancel();

    public Task CancelAsync()
    {
        Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _requestCancelled.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class TestRequest : IHttpRequest
    {
        public TestRequest(IHttpContext context, HttpMethod method, HttpScheme scheme, string host)
        {
            HttpContext = context;
            Method = method;
            Scheme = scheme;
            Host = new HttpHost(host);
        }

        public HttpHost Host { get; }

        public HttpPath Path { get; } = new("/ws");

        public HttpMethod Method { get; }

        public HttpScheme Scheme { get; }

        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();

        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

        public IHttpContext HttpContext { get; }

        public Stream Body => Stream.Null;
    }

    private sealed class TestResponse : IHttpResponse
    {
        public TestResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

        public IHttpContext HttpContext { get; }

        public Stream Body { get; set; } = new MemoryStream();
    }
}

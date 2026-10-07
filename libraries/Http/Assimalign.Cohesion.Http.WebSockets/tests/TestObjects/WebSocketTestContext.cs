using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

/// <summary>
/// A configurable <see cref="IHttpContext"/> double with real header and feature collections. A
/// handshake context also carries a <see cref="FakeProtocolUpgrade"/>, standing in for the
/// protocol-upgrade interceptor, so <c>context.Upgrade</c> reads what the transport would surface.
/// </summary>
internal sealed class WebSocketTestContext : IHttpContext
{
    /// <summary>The sample key of RFC 6455 §1.3.</summary>
    public const string SampleKey = "dGhlIHNhbXBsZSBub25jZQ==";

    /// <summary>The <c>Sec-WebSocket-Accept</c> RFC 6455 §1.3 derives from <see cref="SampleKey"/>.</summary>
    public const string SampleAccept = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

    public WebSocketTestContext(HttpMethod method, HttpVersion version)
    {
        Version = version;
        Request = new TestRequest(this, method);
        Response = new TestResponse(this);
    }

    /// <summary>
    /// Creates a valid HTTP/1.1 opening handshake: a <c>GET</c> upgrade to <c>websocket</c> with the
    /// RFC's sample key and version 13, whose upgrade surrenders <paramref name="transport"/>.
    /// </summary>
    public static WebSocketTestContext CreateHandshake(Stream? transport = null, string upgradeProtocol = "websocket")
    {
        WebSocketTestContext context = new(HttpMethod.Get, HttpVersion.Http11);
        context.Request.Headers[HttpHeaderKey.Connection] = "Upgrade";
        context.Request.Headers[HttpHeaderKey.Upgrade] = upgradeProtocol;
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = SampleKey;
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";
        context.InstallUpgrade(upgradeProtocol, transport ?? new MemoryStream());
        return context;
    }

    /// <summary>Gets the upgrade the context surfaces, when one was installed.</summary>
    public FakeProtocolUpgrade? Upgrade { get; private set; }

    public HttpVersion Version { get; }

    public IHttpRequest Request { get; }

    public IHttpResponse Response { get; }

    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public CancellationToken RequestCancelled => CancellationToken.None;

    /// <summary>Installs an upgrade to <paramref name="protocol"/> that surrenders <paramref name="transport"/>.</summary>
    public void InstallUpgrade(string protocol, Stream transport)
    {
        Upgrade = new FakeProtocolUpgrade(protocol, transport, Response.Headers);
        Features.Set(Upgrade);
    }

    /// <summary>Gets a response header value, or <see langword="null"/> when it is absent.</summary>
    public string? ResponseHeader(HttpHeaderKey key)
        => Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? value.Value : null;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class TestRequest : IHttpRequest
    {
        public TestRequest(IHttpContext context, HttpMethod method)
        {
            HttpContext = context;
            Method = method;
        }

        public HttpHost Host { get; } = new("api.test");

        public HttpPath Path { get; } = new("/socket");

        public HttpMethod Method { get; }

        public HttpScheme Scheme => HttpScheme.Http;

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

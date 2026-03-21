using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    public override HttpHost Host { get; set; } = HttpHost.Empty;

    public override HttpPath Path { get; set; } = HttpPath.Root;

    public override HttpMethod Method { get; set; } = HttpMethod.Get;

    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public override IHttpQueryCollection Query { get; } = new HttpQueryCollection();

    public override IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override IHttpCookieCollection Cookies { get; } = new HttpCookieCollection();

    public override IHttpFormCollection Form { get; } = new HttpFormCollection();

    public override Stream Body { get; set; } = Stream.Null;

    public override ClaimsPrincipal ClaimsPrincipal { get; set; } = new(new ClaimsIdentity());
}

internal sealed class TestHttpResponse : HttpResponse
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public override IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override IHttpCookieCollection Cookies { get; } = new HttpCookieCollection();

    public override Stream Body { get; set; } = new MemoryStream();
}

internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(
        HttpVersion version,
        IHttpSession session,
        TestHttpRequest request,
        TestHttpResponse response,
        IHttpConnectionInfo? connectionInfo = null,
        CancellationToken requestAborted = default)
    {
        Version = version;
        Session = session;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo ?? HttpConnectionInfo.Empty;
        RequestAborted = requestAborted;
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public override HttpVersion Version { get; }

    public override IHttpSession Session { get; }

    public override TestHttpRequest Request { get; }

    public override TestHttpResponse Response { get; }

    public override IHttpConnectionInfo ConnectionInfo { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    public bool IsDisposed { get; private set; }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

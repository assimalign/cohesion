using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

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
    private TestHttpContext(
        HttpVersion version,
        HttpSession session,
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
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
    }

    public override HttpVersion Version { get; }

    public override HttpSession Session { get; }

    public override TestHttpRequest Request { get; }

    public override TestHttpResponse Response { get; }

    public override IHttpConnectionInfo ConnectionInfo { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public static TestHttpContext Create(HttpMethod method, HttpPath path)
    {
        return new TestHttpContext(
            HttpVersion.Http11,
            new HttpSession("routing-test-session"),
            new TestHttpRequest
            {
                Method = method,
                Path = path,
                Scheme = HttpScheme.Http,
            },
            new TestHttpResponse());
    }
}

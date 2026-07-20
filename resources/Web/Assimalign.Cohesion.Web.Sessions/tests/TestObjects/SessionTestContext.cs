using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> that drives the session
/// middleware directly with a chosen request scheme and optional inbound session
/// cookie — cases the plaintext in-memory HTTP/1.1 test factory cannot express
/// (an <c>https</c> request in particular). Request and response cookies work
/// through the real <c>Http.Cookies</c> extensions because both header
/// collections are genuine <see cref="HttpHeaderCollection"/> instances.
/// </summary>
internal sealed class SessionTestContext : IHttpContext
{
    public SessionTestContext(HttpScheme scheme = HttpScheme.Http, string? requestCookieHeader = null)
    {
        Request = new SessionTestRequest(this) { Scheme = scheme };
        if (requestCookieHeader is not null)
        {
            Request.Headers[HttpHeaderKey.Cookie] = requestCookieHeader;
        }

        Response = new SessionTestResponse(this);
    }

    public HttpVersion Version => default;

    public SessionTestRequest Request { get; }

    public SessionTestResponse Response { get; }

    IHttpRequest IHttpContext.Request => Request;

    IHttpResponse IHttpContext.Response => Response;

    public IHttpConnectionInfo ConnectionInfo => null!;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The request half of <see cref="SessionTestContext"/>.</summary>
internal sealed class SessionTestRequest : IHttpRequest
{
    public SessionTestRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host { get; set; } = new("localhost");

    public HttpPath Path { get; set; } = new("/");

    public HttpMethod Method { get; set; } = HttpMethod.Get;

    public HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public HttpQueryCollection Query { get; set; } = new();

    IHttpQueryCollection IHttpRequest.Query => Query;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>The response half of <see cref="SessionTestContext"/>.</summary>
internal sealed class SessionTestResponse : IHttpResponse
{
    public SessionTestResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

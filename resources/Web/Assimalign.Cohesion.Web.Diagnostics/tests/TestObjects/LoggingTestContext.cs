using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

// The test project enables implicit usings, which import System.Net.Http.HttpMethod.
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> for driving the HTTP logging middleware directly
/// with an explicit transport connection — something the in-memory test factory cannot express, since
/// its peer is a non-IP endpoint. Used to model a plaintext request arriving from a proxy address.
/// Only the members the logging and forwarded-headers middleware touch are functional.
/// </summary>
internal sealed class LoggingTestContext : IHttpContext
{
    public LoggingTestContext(IHttpConnectionInfo connectionInfo)
    {
        ConnectionInfo = connectionInfo;
        Request = new LoggingTestRequest(this);
        Response = new LoggingTestResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;

    public LoggingTestRequest Request { get; }

    public LoggingTestResponse Response { get; }

    IHttpRequest IHttpContext.Request => Request;

    IHttpResponse IHttpContext.Response => Response;

    public IHttpConnectionInfo ConnectionInfo { get; }

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The request half of <see cref="LoggingTestContext"/>.</summary>
internal sealed class LoggingTestRequest : IHttpRequest
{
    public LoggingTestRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host { get; set; } = new("backend.internal:8080");

    public HttpPath Path { get; set; } = new("/orders");

    public HttpMethod Method { get; set; } = HttpMethod.Get;

    public HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public HttpQueryCollection Query { get; } = new();

    IHttpQueryCollection IHttpRequest.Query => Query;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>The response half of <see cref="LoggingTestContext"/>.</summary>
internal sealed class LoggingTestResponse : IHttpResponse
{
    public LoggingTestResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

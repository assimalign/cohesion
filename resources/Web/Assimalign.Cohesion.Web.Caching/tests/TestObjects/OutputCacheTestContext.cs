using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> for driving the output-cache middleware and key
/// builder directly, with a settable request line, query, headers, and a response whose body is a
/// readable <see cref="MemoryStream"/> so captured/served bytes can be asserted.
/// </summary>
internal sealed class OutputCacheTestContext : IHttpContext
{
    public OutputCacheTestContext()
    {
        Request = new TestRequest(this);
        Response = new TestResponse(this);
    }

    public HttpVersion Version => default;

    public TestRequest Request { get; }

    public TestResponse Response { get; }

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

    internal sealed class TestRequest : IHttpRequest
    {
        public TestRequest(IHttpContext context) => HttpContext = context;

        public HttpHost Host { get; set; } = new("localhost");

        public HttpPath Path { get; set; } = new("/");

        public HttpMethod Method { get; set; } = HttpMethod.Get;

        public HttpScheme Scheme { get; set; } = HttpScheme.Http;

        public HttpQueryCollection QueryCollection { get; } = new();

        public IHttpQueryCollection Query => QueryCollection;

        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

        public IHttpContext HttpContext { get; }

        public Stream Body { get; set; } = new MemoryStream();
    }

    internal sealed class TestResponse : IHttpResponse
    {
        public TestResponse(IHttpContext context) => HttpContext = context;

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

        public IHttpContext HttpContext { get; }

        public Stream Body { get; set; } = new MemoryStream();
    }
}

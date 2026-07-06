using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Results.Tests.TestObjects;

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes registered middleware in
/// registration order — the same shape the real <c>WebApplication</c> builder produces — without
/// pulling in the hosting/DI stack.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => throw new NotSupportedException();

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        return new TestPipeline(pipeline);
    }
}

internal sealed class TestPipeline : IWebApplicationPipeline
{
    private readonly WebApplicationMiddleware _middleware;

    public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        => _middleware.Invoke(context);
}

/// <summary>
/// A concrete <see cref="IHttpContext"/> double with a real feature collection, a seekable response
/// body (so reset/bodyless detection behaves as it would over a buffered transport), and a settable
/// <see cref="RequestCancelled"/> token for cancellation tests.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(HttpPath? path = null)
    {
        Request = new TestHttpRequest(this, path ?? HttpPath.Root);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled { get; set; } = CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Reads the response body as a UTF-8 string, independent of stream position.</summary>
    public string ResponseBodyText() => ((TestHttpResponse)Response).BodyText();
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, HttpPath path)
    {
        HttpContext = context;
        Path = path;
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method => HttpMethod.Get;
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; } = Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();

    public string BodyText()
    {
        if (Body is MemoryStream memory)
        {
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        long position = Body.CanSeek ? Body.Position : 0;
        if (Body.CanSeek)
        {
            Body.Position = 0;
        }

        using var reader = new StreamReader(Body, Encoding.UTF8, leaveOpen: true);
        string text = reader.ReadToEnd();
        if (Body.CanSeek)
        {
            Body.Position = position;
        }

        return text;
    }
}

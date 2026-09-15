using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

/// <summary>
/// A concrete <see cref="IHttpContext"/> double with a real feature collection and a seekable
/// response body, so hook invocations write and assert as they would over a buffered transport.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext()
    {
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled { get; set; } = CancellationToken.None;

    /// <summary>Whether <see cref="Cancel"/>/<see cref="CancelAsync"/> was invoked (the transport reset path).</summary>
    public bool CancelRequested { get; private set; }

    public void Cancel() => CancelRequested = true;

    public Task CancelAsync()
    {
        CancelRequested = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Reads the response body as a UTF-8 string, independent of stream position.</summary>
    public string ResponseBodyText() => ((TestHttpResponse)Response).BodyText();
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path => HttpPath.Root;
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

/// <summary>
/// An <see cref="IWebApplicationBuilder"/> double that records the features registered by
/// builder-time composition verbs.
/// </summary>
internal sealed class TestWebApplicationBuilder : IWebApplicationBuilder
{
    public List<IHttpFeature> Features { get; } = [];

    public IWebApplicationBuilder AddFeature(IHttpFeature feature)
    {
        Features.Add(feature);
        return this;
    }

    public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure) => this;

    public IWebApplicationBuilder AddServer(IWebApplicationServer server) => this;

    public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => this;

    public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => this;

    public IWebApplication Build() => throw new NotSupportedException("The test builder does not build applications.");
}

/// <summary>
/// A chain-order probe: records that it was consulted and answers with a configured verdict.
/// </summary>
internal sealed class RecordingErrorHandler : IErrorHandler
{
    private readonly bool _handles;

    public RecordingErrorHandler(bool handles) => _handles = handles;

    public bool WasConsulted { get; private set; }

    public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        WasConsulted = true;
        return ValueTask.FromResult(_handles);
    }
}

/// <summary>
/// A fake response-streaming feature reporting a started (head-committed) response, as the streaming
/// feature package would after the handler's first streamed write — the boundary's no-clobber signal.
/// </summary>
internal sealed class FakeResponseStreamingFeature : IHttpResponseStreamingFeature
{
    public FakeResponseStreamingFeature(bool hasStarted) => HasStarted = hasStarted;

    public string Name => nameof(IHttpResponseStreamingFeature);
    public bool HasStarted { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order — the same shape the real <c>WebApplication</c> builder produces, with the same silent
/// <c>Task.CompletedTask</c> terminal — so the pipeline verbs (<c>UseErrorHandling</c>,
/// <c>UseStatusCodePages</c>) can be driven without the hosting/DI stack.
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

    /// <summary>Appends an inline terminal-style middleware (does not call <c>next</c>).</summary>
    public IWebApplicationPipelineBuilder Run(Func<IHttpContext, Task> handler)
        => Use(next => context => handler.Invoke(context));

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        return new TestPipeline(pipeline);
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

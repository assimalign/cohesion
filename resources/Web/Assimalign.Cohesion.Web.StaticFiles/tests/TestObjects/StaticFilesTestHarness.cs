using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// Builds populated <see cref="InMemoryFileSystem"/> content roots for static-file tests.
/// </summary>
internal static class StaticSite
{
    public static InMemoryFileSystem Create(params (string Path, string Content)[] files)
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            Name = "static-site",
        });

        foreach ((string path, string content) in files)
        {
            IFileSystemFile file = fileSystem.CreateFile(path);
            using Stream stream = file.Open(FileMode.Open, FileAccess.Write);
            byte[] payload = Encoding.UTF8.GetBytes(content);
            stream.Write(payload, 0, payload.Length);
        }

        return fileSystem;
    }
}

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order — the same shape the real <c>WebApplication</c> builder produces — without pulling in
/// the hosting/DI stack. Mirrors the Web.Health test harness.
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

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

/// <summary>
/// A configurable <see cref="IHttpContext"/> test double with a readable response body, for
/// driving the middleware with raw paths an <see cref="System.Net.Http.HttpClient"/> would
/// normalize away (literal dot segments, backslashes).
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(string path, HttpMethod method)
    {
        Request = new TestHttpRequest(this, path, method);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => CancellationToken.None;
    public void Cancel() { }
    public Task CancelAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string ReadResponseBody()
    {
        var stream = (MemoryStream)Response.Body;
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, string path, HttpMethod method)
    {
        HttpContext = context;
        Path = new HttpPath(path);
        Method = method;
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method { get; }
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body => Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();
}

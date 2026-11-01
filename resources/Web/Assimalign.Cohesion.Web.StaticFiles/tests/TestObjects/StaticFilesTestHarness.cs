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
/// Builds the static-files middleware through <see cref="StaticFilesBuilder"/>, as
/// <c>builder.Services.AddStaticFiles(files =&gt; ...)</c> does, over an explicit mount.
/// </summary>
internal static class StaticFilesMount
{
    public static IWebApplicationMiddleware Build(IFileSystem fileSystem, Action<StaticFilesOptions>? configure = null)
    {
        StaticFilesBuilder files = new StaticFilesBuilder().UseFileSystem(fileSystem);
        configure?.Invoke(files.Options);

        return files.Build();
    }
}

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order — the same shape the real <c>WebApplication</c> builder produces — without pulling in
/// the hosting/DI stack. Mirrors the Web.Health test harness. A context-aware verb such as
/// <c>UseStaticFiles()</c> composes over an application context whose
/// <see cref="IWebApplicationContext.Middleware"/> lists what the test registered, as
/// <c>builder.Services.AddStaticFiles(...)</c> would, and whose web root the test chooses.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();
    private readonly TestApplicationContext _context = new();

    /// <summary>Gets the middleware the application registered.</summary>
    public List<IWebApplicationMiddleware> Registered => _context.Registered;

    /// <summary>Gets or sets the application's web root.</summary>
    public FileSystemPath? WebRootPath
    {
        get => _context.WebRootPath;
        set => _context.WebRootPath = value;
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use(next => middleware.Invoke(_context, next));

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

    private sealed class TestApplicationContext : IWebApplicationContext
    {
        public List<IWebApplicationMiddleware> Registered { get; } = [];

        public FileSystemPath? ContentRootPath => null;

        public FileSystemPath? WebRootPath { get; set; }

        public IEnumerable<IWebApplicationMiddleware> Middleware => Registered;

        public IEnumerable<IWebApplicationServer> Servers => [];

        public IEnumerable<IHttpFeature> Features => [];

        public IEnumerable<IWebApplicationSetting> Settings => [];
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
    public static readonly DateTimeOffset DefaultDate = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _defaultDateHeader = HttpDate.Format(DefaultDate);

    public TestHttpResponse(IHttpContext context)
    {
        HttpContext = context;
        Headers[HttpHeaderKey.Date] = _defaultDateHeader;
    }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();
}

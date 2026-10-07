using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

/// <summary>
/// A composing <see cref="IWebApplicationPipelineBuilder"/> that ends in the standard
/// <see cref="WebApplicationTerminal"/> and supplies a fixed application context to component factories, as
/// the Web host does, so <c>UseRewrite</c>, <c>Map</c> and inline middleware compose exactly as they do on
/// the application, without the hosting stack.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    public TestApplicationContext Context { get; } = new();

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use((IWebApplicationContext _, WebApplicationMiddleware next) => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use((IWebApplicationContext _, WebApplicationMiddleware next) => middleware(next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = WebApplicationTerminal.InvokeAsync;

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i](Context, pipeline);
        }

        return new Pipeline(pipeline);
    }

    /// <summary>
    /// Runs <paramref name="context"/> through the composed pipeline.
    /// </summary>
    public async Task<TestHttpContext> SendAsync(TestHttpContext context)
    {
        await Build().ExecuteAsync(context, CancellationToken.None);
        return context;
    }

    private sealed class Pipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public Pipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) => _middleware(context);
    }
}

/// <summary>
/// The application context <see cref="TestPipelineBuilder"/> hands component factories: no content root,
/// no web root, nothing registered.
/// </summary>
internal sealed class TestApplicationContext : IWebApplicationContext
{
    public FileSystemPath? ContentRootPath => null;

    public FileSystemPath? WebRootPath => null;

    public IEnumerable<IWebApplicationMiddleware> Middleware => Array.Empty<IWebApplicationMiddleware>();

    public IEnumerable<IWebApplicationServer> Servers => Array.Empty<IWebApplicationServer>();

    public IEnumerable<IHttpFeature> Features => Array.Empty<IHttpFeature>();
}

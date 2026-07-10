using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Results.Tests.TestObjects;

/// <summary>
/// A minimal in-process web-application double that supports the real <c>AddRouting</c> /
/// <c>UseRouting</c> / <c>MapGet</c> extension chain end-to-end without the hosting/DI stack.
/// Mirrors the production wiring that matters here: features registered at build time are seeded
/// onto each request's <see cref="IHttpContext.Features"/> (as <c>WebApplication.Init</c> does), so
/// route resolution and result execution behave as they do in production. Adapted from the
/// Web.Routing test double of the same name.
/// </summary>
internal sealed class TestWebApplication : IWebApplicationBuilder, IWebApplicationPipelineBuilder, IWebApplication
{
    private readonly List<IHttpFeature> _features = new();
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();
    private readonly TestWebApplicationContext _context;

    public TestWebApplication()
    {
        _context = new TestWebApplicationContext(_features);
    }

    public IWebApplicationContext Context => _context;

    // IWebApplicationBuilder ---------------------------------------------------------------------

    public IWebApplicationBuilder AddFeature(IHttpFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        _features.Add(feature);
        return this;
    }

    public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _features.Add(configure(_context));
        return this;
    }

    public IWebApplicationBuilder AddServer(IWebApplicationServer server) => this;

    public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => this;

    public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => this;

    IWebApplication IWebApplicationBuilder.Build() => this;

    // IWebApplicationPipelineBuilder -------------------------------------------------------------

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use(next => middleware.Invoke(_context, next));

    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        // Seed the request feature collection from the application features, mirroring
        // WebApplication.Init() so route-feature resolution behaves as it does in production.
        WebApplicationMiddleware composed = pipeline;
        WebApplicationMiddleware seeded = context =>
        {
            foreach (IHttpFeature feature in _features)
            {
                context.Features.Set(feature);
            }

            return composed.Invoke(context);
        };

        return new TestPipeline(seeded);
    }

    /// <summary>Builds and executes the pipeline against a single request context.</summary>
    public Task ExecuteAsync(IHttpContext context)
        => ((IWebApplicationPipelineBuilder)this).Build().ExecuteAsync(context);

    // IWebApplication ----------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class TestWebApplicationContext : IWebApplicationContext
    {
        private readonly IEnumerable<IHttpFeature> _features;

        public TestWebApplicationContext(IEnumerable<IHttpFeature> features) => _features = features;

        public IEnumerable<IWebApplicationMiddleware> Middleware => Array.Empty<IWebApplicationMiddleware>();

        public IEnumerable<IWebApplicationServer> Servers => Array.Empty<IWebApplicationServer>();

        public IEnumerable<IHttpFeature> Features => _features;
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

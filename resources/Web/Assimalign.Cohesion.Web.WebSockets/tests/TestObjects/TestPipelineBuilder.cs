using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order, the shape the real builder produces, so <c>UseWebSockets</c> is driven through its public
/// verb without the hosting stack. Its application context lists in
/// <see cref="IWebApplicationContext.Middleware"/> what a test registered, as
/// <c>builder.Services.AddWebSockets(...)</c> would, and <c>UseWebSockets()</c> reads it when the
/// pipeline is built.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();
    private readonly TestApplicationContext _context = new();

    /// <summary>Gets the middleware the application registered.</summary>
    public List<IWebApplicationMiddleware> Registered => _context.Registered;

    /// <summary>
    /// Builds a pipeline of <c>UseWebSockets</c> followed by <paramref name="downstream"/>, runs it
    /// for <paramref name="context"/>, and reports whether the request reached the downstream stage.
    /// A <paramref name="configure"/> callback is registered first, as
    /// <c>builder.Services.AddWebSockets(webSockets =&gt; configure(webSockets.Options))</c> would; without
    /// one nothing is registered and <c>UseWebSockets()</c> places the default policy.
    /// </summary>
    public static async Task<bool> RunAsync(
        IHttpContext context,
        Action<WebSocketOptions>? configure,
        Func<IHttpContext, Task>? downstream = null)
    {
        bool continued = false;
        TestPipelineBuilder builder = new();
        if (configure is not null)
        {
            builder.Registered.Add(BuildWebSockets(configure));
        }

        builder.UseWebSockets();
        builder.Use(next => async inner =>
        {
            continued = true;
            if (downstream is not null)
            {
                await downstream(inner);
            }
        });

        await builder.Build().ExecuteAsync(context, CancellationToken.None);
        return continued;
    }

    /// <summary>Builds the policy middleware <c>builder.Services.AddWebSockets(...)</c> would register.</summary>
    public static IWebApplicationMiddleware BuildWebSockets(Action<WebSocketOptions>? configure = null)
    {
        WebSocketsBuilder webSockets = new();
        configure?.Invoke(webSockets.Options);

        return webSockets.Build();
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

        public FileSystemPath? WebRootPath => null;

        public IEnumerable<IWebApplicationMiddleware> Middleware => Registered;

        public IEnumerable<IWebApplicationServer> Servers => [];

        public IEnumerable<IHttpFeature> Features => [];

        public IEnumerable<IWebApplicationSetting> Settings => [];
    }
}

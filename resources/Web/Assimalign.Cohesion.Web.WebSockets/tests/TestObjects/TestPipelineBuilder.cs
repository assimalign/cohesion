using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order, the shape the real builder produces, so <c>UseWebSockets</c> is driven through its public
/// verb without the hosting stack.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    /// <summary>
    /// Builds a pipeline of <c>UseWebSockets</c> followed by <paramref name="downstream"/>, runs it
    /// for <paramref name="context"/>, and reports whether the request reached the downstream stage.
    /// </summary>
    public static async Task<bool> RunAsync(
        IHttpContext context,
        Action<WebSocketOptions>? configure,
        Func<IHttpContext, Task>? downstream = null)
    {
        bool continued = false;
        TestPipelineBuilder builder = new();
        builder.UseWebSockets(configure);
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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Verifies that the <c>UseRouting()</c> middleware dispatches through the router and that the
/// route-match state it stores (via the #150 Features-based <c>SetRouteMatch</c>) is resolvable
/// downstream — the middleware codepath that shares the storage rewritten under it. Composed with
/// a minimal pipeline builder double that mirrors the real register-order execution.
/// </summary>
public class UseRoutingMiddlewareTests
{
    private sealed class AuthMetadata
    {
        public AuthMetadata(string policy) => Policy = policy;

        public string Policy { get; }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: installs a resolvable route-match feature on a match")]
    public async Task UseRouting_OnMatch_ShouldInstallResolvableFeatureAndInvokeHandler()
    {
        // Arrange
        AuthMetadata auth = new("admin");
        RecordingRouterRouteHandler handler = new();
        Route route = new(HttpMethod.Get, "/users/{id:int}", handler, new RouterRouteMetadataCollection(auth));
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        context.Features.Set<IRouterFeature>(new TestRouterFeature(new Router(route)));

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.UseRouting();
        builder.Use((ctx, next) =>
        {
            downstreamRan = true;
            return next.Invoke(ctx);
        });

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeTrue();
        downstreamRan.ShouldBeFalse(); // a match is terminal
        context.TryGetRoute(out IRouterRoute? matched).ShouldBeTrue();
        matched.ShouldBeSameAs(route);
        context.TryGetRouteValues(out RouteValueDictionary? values).ShouldBeTrue();
        values!["id"].ShouldBe("42");
        context.GetEndpointMetadata<AuthMetadata>().ShouldBeSameAs(auth);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: falls through with no feature when nothing matches")]
    public async Task UseRouting_OnNoMatch_ShouldFallThroughWithoutFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/nope");
        context.Features.Set<IRouterFeature>(new TestRouterFeature(new Router(new Route(HttpMethod.Get, "/users/{id:int}"))));

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.UseRouting();
        builder.Use((ctx, next) =>
        {
            downstreamRan = true;
            return next.Invoke(ctx);
        });

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        downstreamRan.ShouldBeTrue();
        context.GetRouteMatch().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: short-circuits 405 with no route-match feature")]
    public async Task UseRouting_OnMethodMismatch_ShouldShortCircuitWithoutFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/42");
        context.Features.Set<IRouterFeature>(new TestRouterFeature(new Router(new Route(HttpMethod.Get, "/users/{id:int}"))));

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.UseRouting();
        builder.Use((ctx, next) =>
        {
            downstreamRan = true;
            return next.Invoke(ctx);
        });

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        downstreamRan.ShouldBeFalse(); // a 405 short-circuits
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        context.GetRouteMatch().ShouldBeNull(); // no match feature installed on a 405
    }

    private sealed class TestRouterFeature : IRouterFeature
    {
        public TestRouterFeature(IRouter router) => Router = router;

        public IRouter Router { get; }

        public IRouterBuilder Builder { get; } = new RouterBuilder();

        public string Name => nameof(IRouterFeature);
    }

    /// <summary>
    /// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes registered middleware in
    /// registration order — the same shape the real <c>WebApplication</c> builder produces.
    /// </summary>
    private sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
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

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

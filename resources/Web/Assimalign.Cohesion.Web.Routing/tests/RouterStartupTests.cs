using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// The router is built once, when the application's request pipeline is built (#1051). Before the
/// fix it was built lazily, and without synchronization, on the first request: route-table errors
/// surfaced on every request and routes mapped after the first request were silently ignored. These
/// tests pin the startup build, its failure mode, the closed route table afterwards, and build-once
/// under concurrency. <see cref="TestWebApplication"/> composes the pipeline the way the Web host
/// does, so building its pipeline is the startup boundary here.
/// </summary>
public class RouterStartupTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: The router is built when the pipeline is built, before any request")]
    public async Task BuildPipeline_WithUseRouting_ShouldBuildRouterBeforeFirstRequest()
    {
        // Arrange
        TokenRecordingRouterRouteHandler handler = new();
        BuildCountingRoute route = new(new Route(HttpMethod.Get, "/orders/{id:int}", handler));

        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(route);

        // Act
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();
        int buildsAtStartup = route.BuildCount;

        await pipeline.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/orders/1"), CancellationToken.None);

        // Assert
        buildsAtStartup.ShouldBe(1);
        route.BuildCount.ShouldBe(1);
        handler.InvocationCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: A duplicate route name fails the pipeline build, not a request")]
    public void BuildPipeline_WithDuplicateRouteName_ShouldThrowBeforeAnyRequest()
    {
        // Arrange — route names compare case-insensitively.
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting()
            .Map(NamedRoute("orders", "/orders/{id}"))
            .Map(NamedRoute("ORDERS", "/archive/{id}"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ((IWebApplicationPipelineBuilder)app).Build());

        // Assert
        exception.Message.ShouldContain("'orders'");
        exception.Message.ShouldContain("more than once");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: Mapping a route after the pipeline is built throws")]
    public void Map_AfterPipelineBuild_ShouldThrowInvalidOperationException()
    {
        // Arrange
        TestWebApplication app = new();
        app.AddRouting();
        IRouterBuilder routes = app.UseRouting();
        routes.Map(new Route(HttpMethod.Get, "/orders"));
        ((IWebApplicationPipelineBuilder)app).Build();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => routes.Map(new Route(HttpMethod.Get, "/late")));

        // Assert
        exception.Message.ShouldContain("'/late'", Case.Sensitive);
        exception.Message.ShouldContain("already been built");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: Mapping through a route group after the pipeline is built throws")]
    public void MapGroupChild_AfterPipelineBuild_ShouldThrowInvalidOperationException()
    {
        // Arrange
        TestWebApplication app = new();
        app.AddRouting();
        IRouterBuilder routes = app.UseRouting();
        IRouterGroupBuilder group = routes.MapGroup("api");
        group.Map(HttpMethod.Get, "orders", new TokenRecordingRouterRouteHandler());
        ((IWebApplicationPipelineBuilder)app).Build();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => group.Map(HttpMethod.Get, "late", new TokenRecordingRouterRouteHandler()));

        // Assert
        exception.Message.ShouldContain("'api/late'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: Concurrent first requests use the one router built at startup")]
    public async Task ExecuteAsync_ConcurrentFirstRequests_ShouldNotBuildAgain()
    {
        // Arrange
        const int requestCount = 32;
        TokenRecordingRouterRouteHandler handler = new();
        BuildCountingRoute route = new(new Route(HttpMethod.Get, "/orders/{id:int}", handler), TimeSpan.FromMilliseconds(20));

        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(route);
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();

        // Act
        Task[] requests = Enumerable.Range(0, requestCount)
            .Select(index => Task.Run(() => pipeline.ExecuteAsync(
                TestHttpContext.Create(HttpMethod.Get, $"/orders/{index}"),
                CancellationToken.None)))
            .ToArray();
        await Task.WhenAll(requests);

        // Assert
        route.BuildCount.ShouldBe(1);
        handler.InvocationCount.ShouldBe(requestCount);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Startup: Concurrent first reads of the router feature build it once")]
    public async Task Router_ConcurrentFirstAccess_ShouldBuildOnce()
    {
        // Arrange — request-time readers (link generation, output caching) read the feature's
        // router directly, so its first read must also build exactly once.
        const int readerCount = 16;
        BuildCountingRoute route = new(new Route(HttpMethod.Get, "/orders/{id:int}"), TimeSpan.FromMilliseconds(50));

        TestWebApplication app = new();
        app.AddRouting();
        IRouterFeature feature = app.Context.Features.OfType<IRouterFeature>().Single();
        feature.Builder.Map(route);

        using Barrier barrier = new(readerCount);

        // Act
        Task<IRouter>[] readers = Enumerable.Range(0, readerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    return feature.Router;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        IRouter[] routers = await Task.WhenAll(readers);

        // Assert
        route.BuildCount.ShouldBe(1);
        routers.ShouldAllBe(router => ReferenceEquals(router, routers[0]));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouterBuilder: Build returns the same router on every call")]
    public void Build_CalledTwice_ShouldReturnSameRouter()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(new Route(HttpMethod.Get, "/orders"));

        // Act
        IRouter first = builder.Build();
        IRouter second = builder.Build();

        // Assert
        second.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouterBuilder: Map after Build throws and leaves the router unchanged")]
    public void Map_AfterBuild_ShouldThrowAndLeaveRouterUnchanged()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(new Route(HttpMethod.Get, "/orders"));
        IRouter router = builder.Build();

        // Act
        Should.Throw<InvalidOperationException>(() => builder.Map(new Route(HttpMethod.Get, "/late")));

        // Assert
        router.Routes.Count().ShouldBe(1);
        builder.Build().ShouldBeSameAs(router);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouterBuilder: A failed build keeps the route table closed and fails again")]
    public void Build_AfterFailedBuild_ShouldStayClosedAndThrowAgain()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(NamedRoute("orders", "/orders/{id}"));
        builder.Map(NamedRoute("orders", "/archive/{id}"));
        Should.Throw<InvalidOperationException>(() => builder.Build());

        // Act / Assert — the table stays closed, and the same table fails the same way.
        Should.Throw<InvalidOperationException>(() => builder.Map(new Route(HttpMethod.Get, "/late")))
            .Message.ShouldContain("already been built");
        Should.Throw<InvalidOperationException>(() => builder.Build())
            .Message.ShouldContain("'orders'", Case.Sensitive);
    }

    private static Route NamedRoute(string name, string template)
    {
        return new Route(
            new[] { HttpMethod.Get },
            RoutePatternParser.Parse(template),
            RouteParameterPolicyMap.CreateDefault(),
            new TokenRecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(new RouteNameMetadata(name)));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Request cancellation reaches route handlers (#1051). <c>UseRouting</c> hands a matched route's
/// handler the request's own <see cref="IHttpContext.RequestCancelled"/> token (it used to allocate a
/// linked token source per request that nothing needed), and <see cref="RouterRouteHandler"/> no
/// longer ignores the token it is given.
/// </summary>
public class RoutingCancellationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: UseRouting passes the request's cancellation token to the handler")]
    public async Task UseRouting_OnMatch_ShouldPassRequestCancelledToHandler()
    {
        // Arrange
        using CancellationTokenSource request = new();
        TokenRecordingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/orders/7", request.Token);

        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/orders/{id:int}", handler));

        // Act
        await app.ExecuteAsync(context);

        // Assert
        handler.InvocationCount.ShouldBe(1);
        handler.LastToken.ShouldBe(context.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: A running handler observes the request being cancelled")]
    public async Task UseRouting_WhenRequestCancelledDuringHandler_HandlerShouldObserveCancellation()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using CancellationTokenSource request = new();
        WaitingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/slow", request.Token);

        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/slow", handler));

        // Act
        Task execution = app.ExecuteAsync(context);
        await handler.Started.WaitAsync(timeout.Token);
        await request.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => execution.WaitAsync(timeout.Token));
        handler.ObservedCancellation.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: A request cancelled before dispatch does not start its middleware handler")]
    public async Task UseRouting_WhenRequestAlreadyCancelled_ShouldNotStartMiddlewareHandler()
    {
        // Arrange
        using CancellationTokenSource request = new();
        await request.CancelAsync();
        bool middlewareRan = false;
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/orders", request.Token);

        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/orders", new RouterRouteHandler(_ =>
        {
            middlewareRan = true;
            return Task.CompletedTask;
        })));

        // Act
        Task execution = app.ExecuteAsync(context);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => execution);
        execution.IsCanceled.ShouldBeTrue();
        middlewareRan.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: RouterRouteHandler does not start its middleware for a cancelled token")]
    public void InvokeAsync_WithCancelledToken_ShouldReturnCanceledTaskWithoutInvokingMiddleware()
    {
        // Arrange
        bool middlewareRan = false;
        RouterRouteHandler handler = new(_ =>
        {
            middlewareRan = true;
            return Task.CompletedTask;
        });

        // Act
        Task invocation = handler.InvokeAsync(TestHttpContext.Create(HttpMethod.Get, "/"), new CancellationToken(canceled: true));

        // Assert
        invocation.IsCanceled.ShouldBeTrue();
        middlewareRan.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: RouterRouteHandler invokes its middleware for an active token")]
    public async Task InvokeAsync_WithActiveToken_ShouldInvokeMiddleware()
    {
        // Arrange
        using CancellationTokenSource request = new();
        IHttpContext? served = null;
        RouterRouteHandler handler = new(context =>
        {
            served = context;
            return Task.CompletedTask;
        });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/", request.Token);

        // Act
        await handler.InvokeAsync(context, context.RequestCancelled);

        // Assert
        served.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: RouteAsync passes its caller's token to the handler")]
    public async Task RouteAsync_OnMatch_ShouldPassCallerTokenToHandler()
    {
        // Arrange
        using CancellationTokenSource caller = new();
        TokenRecordingRouterRouteHandler handler = new();
        Router router = new(new Route(HttpMethod.Get, "/orders", handler));

        // Act
        await router.RouteAsync(TestHttpContext.Create(HttpMethod.Get, "/orders"), caller.Token);

        // Assert
        handler.LastToken.ShouldBe(caller.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Cancellation: RouterRouteHandler rejects a null middleware")]
    public void Constructor_WithNullMiddleware_ShouldThrowArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new RouterRouteHandler(null!));
    }

    /// <summary>
    /// Signals when it starts, then waits on the token it was handed until that token is cancelled.
    /// </summary>
    private sealed class WaitingRouterRouteHandler : IRouterRouteHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public bool ObservedCancellation { get; private set; }

        public async Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }
}

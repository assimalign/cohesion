using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for #1051 through the real Web runtime: the application's router is built
/// when the host builds its request pipeline at startup, so route-table errors fail the start, a
/// route mapped after the start throws, and an aborted request cancels the token its route handler
/// was given.
/// </summary>
public class RouterStartupEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private static readonly WebApplicationMiddleware _ok = context =>
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        return Task.CompletedTask;
    };

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Router startup: a duplicate route name fails the start before any request")]
    public async Task StartAsync_DuplicateRouteName_ShouldFailStartup()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting()
            .Map(NamedRoute("orders", "/orders/{id}"))
            .Map(NamedRoute("orders", "/archive/{id}"));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await factory.StartAsync(CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("'orders'");
        factory.IsStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Router startup: a duplicate route name fails the host's start and leaves it Failed")]
    public async Task HostStartAsync_DuplicateRouteName_ShouldFailHostStart()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting()
            .Map(NamedRoute("orders", "/orders/{id}"))
            .Map(NamedRoute("orders", "/archive/{id}"));
        IWebApplication application = factory.Application;

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => application.StartAsync(cancellation.Token));

        // Assert
        exception.Message.ShouldContain("'orders'");
        factory.Application.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Router startup: an invalid route template fails with a message naming the problem")]
    public async Task MapGet_InvalidTemplate_ShouldThrowSpecificMessage()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting();

        // Act
        RoutePatternException exception = Should.Throw<RoutePatternException>(
            () => factory.Application.MapGet("/orders/{id", _ok));

        // Assert
        exception.Pattern.ShouldBe("/orders/{id");
        exception.Message.ShouldBe(
            "The route template '/orders/{id' is invalid. The parameter '{id' is not closed: the template ends before its closing '}'. End the parameter with '}'.");
        factory.IsStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Router startup: mapping a route after the application starts throws")]
    public async Task MapGet_AfterStart_ShouldThrowAndLeaveRouteUnserved()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders", _ok);

        using HttpClient client = factory.CreateClient();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => factory.Application.MapGet("/late", _ok));
        using HttpResponseMessage late = await client.GetAsync("/late", cancellation.Token);
        using HttpResponseMessage orders = await client.GetAsync("/orders", cancellation.Token);

        // Assert
        exception.Message.ShouldContain("'/late'", Case.Sensitive);
        late.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        orders.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Router cancellation: an aborted request cancels its route handler's token")]
    public async Task Request_AbortedByClient_ShouldCancelHandlerToken()
    {
        // Arrange — HTTP/2, so the client's abort resets just the stream and reaches the exchange.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });
        factory.Builder.Services.AddRouting();

        WaitingRouterRouteHandler handler = new();
        factory.Application.UseRouting().Map(new Route(HttpMethod.Get, "/slow", handler));

        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        // Act
        Task<HttpResponseMessage> request = client.GetAsync("/slow", abort.Token);
        await handler.Started.WaitAsync(timeout.Token);
        await abort.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => request);
        (await handler.Cancelled.WaitAsync(timeout.Token)).ShouldBeTrue();
    }

    private static Route NamedRoute(string name, string template)
    {
        return new Route(
            new[] { HttpMethod.Get },
            RoutePatternParser.Parse(template),
            RouteParameterPolicyMap.CreateDefault(),
            new RouterRouteHandler(_ok),
            new RouterRouteMetadataCollection(new RouteNameMetadata(name)));
    }

    /// <summary>
    /// Signals when it starts, then waits on the token it was handed and reports whether that token
    /// was cancelled.
    /// </summary>
    private sealed class WaitingRouterRouteHandler : IRouterRouteHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<bool> Cancelled => _cancelled.Task;

        public async Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancelled.TrySetResult(true);
                throw;
            }
        }
    }
}

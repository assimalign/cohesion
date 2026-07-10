using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Testing.Tests;

/// <summary>
/// Parallel test isolation: multiple factories in one process must not share state. This is
/// the end-to-end regression guard for the per-application router state fix (#789) — before
/// that fix, <c>UseRouting</c> returned a process-wide shared <c>RouterBuilder</c>, so routes
/// mapped through one application leaked into every other application in the process. Here two
/// live factories each map their own route and serve real requests over their own in-memory
/// listeners; neither observes the other's routes, middleware, or connections.
/// </summary>
public class WebApplicationTestFactoryIsolationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - Isolation: Two factories in one process keep isolated router state (#789 regression)")]
    public async Task TwoFactories_InOneProcess_ShouldKeepIsolatedRouterState()
    {
        // Arrange — factory A knows only /alpha; factory B knows only /beta. Each terminates
        // unmatched requests with 404 (the pre-#881 application-authored terminal).
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factoryA = CreateRoutedFactory("/alpha", "alpha payload");
        await using WebApplicationTestFactory factoryB = CreateRoutedFactory("/beta", "beta payload");

        using HttpClient clientA = factoryA.CreateClient();
        using HttpClient clientB = factoryB.CreateClient();

        // Act & Assert — each application serves its own route...
        using (HttpResponseMessage response = await clientA.GetAsync("/alpha", cancellationToken))
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("alpha payload");
        }

        using (HttpResponseMessage response = await clientB.GetAsync("/beta", cancellationToken))
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("beta payload");
        }

        // ...and the other application's route never leaked into its table.
        using (HttpResponseMessage response = await clientA.GetAsync("/beta", cancellationToken))
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        }

        using (HttpResponseMessage response = await clientB.GetAsync("/alpha", cancellationToken))
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - Isolation: Concurrent requests against two factories do not cross-talk")]
    public async Task TwoFactories_ConcurrentRequests_ShouldNotCrossTalk()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factoryA = CreateRoutedFactory("/alpha", "alpha payload");
        await using WebApplicationTestFactory factoryB = CreateRoutedFactory("/beta", "beta payload");

        using HttpClient clientA = factoryA.CreateClient();
        using HttpClient clientB = factoryB.CreateClient();

        // Act — interleave both applications from one process concurrently.
        Task<string> alpha = clientA.GetStringAsync("/alpha", cancellationToken);
        Task<string> beta = clientB.GetStringAsync("/beta", cancellationToken);

        string[] payloads = await Task.WhenAll(alpha, beta);

        // Assert
        payloads[0].ShouldBe("alpha payload");
        payloads[1].ShouldBe("beta payload");
    }

    /// <summary>
    /// Composes a factory whose application maps exactly one GET route writing
    /// <paramref name="payload"/>, with a trailing 404 terminal for unmatched requests.
    /// </summary>
    private static WebApplicationTestFactory CreateRoutedFactory(string pattern, string payload)
    {
        WebApplicationTestFactory factory = new();

        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(CohesionHttpMethod.Get, pattern, new RouterRouteHandler(async context =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), context.RequestCancelled);
        })));

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NotFound;
            return next.Invoke(context);
        });

        return factory;
    }
}

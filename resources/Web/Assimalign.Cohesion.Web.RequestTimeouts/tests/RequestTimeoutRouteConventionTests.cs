using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

/// <summary>
/// The endpoint convention verbs (#1055) over the real router: <c>WithRequestTimeout</c> on a mapped
/// route and on a route group, and <c>DisableRequestTimeout</c> exempting one route of a bounded group.
/// </summary>
public class RequestTimeoutRouteConventionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _shortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _neverInTestBudget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan _pastShortTimeout = TimeSpan.FromMilliseconds(600);

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Conventions: A timeout declared on a route should bound that route")]
    public async Task WithRequestTimeout_OnRoute_ShouldBoundTheRoute()
    {
        // Arrange — no global default: only the route's own declaration can answer inside the budget.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/slow", NeverCompletes()).WithRequestTimeout(_shortTimeout);

        factory.Application.UseRequestTimeouts();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/slow", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Conventions: A group timeout should bound its routes unless a route disables it")]
    public async Task WithRequestTimeout_OnGroup_ShouldBoundRoutesExceptDisabledOnes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api")
            .WithRequestTimeout(new RequestTimeoutPolicy { Timeout = _shortTimeout });
        api.Map(CohesionHttpMethod.Get, "slow", NeverCompletes());
        api.Map(CohesionHttpMethod.Get, "unhurried", new RouterRouteHandler(async context =>
        {
            await Task.Delay(_pastShortTimeout, context.RequestCancelled);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("unhurried"), context.RequestCancelled);
        })).DisableRequestTimeout();

        factory.Application.UseRequestTimeouts();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage slow = await client.GetAsync("/api/slow", cancellationToken);
        using HttpResponseMessage unhurried = await client.GetAsync("/api/unhurried", cancellationToken);

        // Assert
        slow.StatusCode.ShouldBe(NetHttpStatusCode.GatewayTimeout);
        unhurried.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await unhurried.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("unhurried");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Conventions: A non-positive timeout should be rejected when declared")]
    public void WithRequestTimeout_NonPositiveTimeout_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder route = new RouterBuilder().Map(CohesionHttpMethod.Get, "/x", NeverCompletes());

        // Act
        Action act = () => route.WithRequestTimeout(TimeSpan.Zero);

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    private static RouterRouteHandler NeverCompletes()
        => new(context => Task.Delay(_neverInTestBudget, context.RequestCancelled));
}

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

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// The endpoint convention verbs (#1055) over the real router: <c>CacheOutput</c> in its three forms on
/// mapped routes and a route group, and <c>DisableOutputCache</c> opting one route out of a cached group.
/// </summary>
public class OutputCacheRouteConventionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _longDuration = TimeSpan.FromMinutes(30);

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Conventions: CacheOutput on a route should cache only that route")]
    public async Task CacheOutput_OnRoute_ShouldCacheOnlyThatRoute()
    {
        // Arrange — opt-in mode (no base policy).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        int[] hits = new int[3];

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/base", Counting("base", hits, 0)).CacheOutput();
        routes.Map(CohesionHttpMethod.Get, "/inline", Counting("inline", hits, 1))
            .CacheOutput(new OutputCachePolicy { Duration = _longDuration });
        routes.Map(CohesionHttpMethod.Get, "/plain", Counting("plain", hits, 2));

        factory.Application.UseOutputCache();

        using HttpClient client = factory.CreateClient();

        // Act
        string base1 = await client.GetStringAsync("/base", cancellationToken);
        string base2 = await client.GetStringAsync("/base", cancellationToken);
        string inline1 = await client.GetStringAsync("/inline", cancellationToken);
        string inline2 = await client.GetStringAsync("/inline", cancellationToken);
        string plain1 = await client.GetStringAsync("/plain", cancellationToken);
        string plain2 = await client.GetStringAsync("/plain", cancellationToken);

        // Assert
        base1.ShouldBe("base-1");
        base2.ShouldBe("base-1");
        inline1.ShouldBe("inline-1");
        inline2.ShouldBe("inline-1");
        plain1.ShouldBe("plain-1");
        plain2.ShouldBe("plain-2");
        hits[0].ShouldBe(1);
        hits[1].ShouldBe(1);
        hits[2].ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Conventions: A named group policy should cache its routes unless a route opts out")]
    public async Task CacheOutput_NamedPolicyOnGroup_ShouldCacheRoutesExceptDisabledOnes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        int[] hits = new int[2];

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder catalog = routes.MapGroup("/catalog").CacheOutput("catalog");
        catalog.Map(CohesionHttpMethod.Get, "items", Counting("items", hits, 0));
        catalog.Map(CohesionHttpMethod.Get, "live", Counting("live", hits, 1)).DisableOutputCache();

        factory.Application.UseOutputCache(options => options.AddPolicy("catalog", policy => policy.Duration = _longDuration));

        using HttpClient client = factory.CreateClient();

        // Act
        string items1 = await client.GetStringAsync("/catalog/items", cancellationToken);
        string items2 = await client.GetStringAsync("/catalog/items", cancellationToken);
        string live1 = await client.GetStringAsync("/catalog/live", cancellationToken);
        string live2 = await client.GetStringAsync("/catalog/live", cancellationToken);

        // Assert
        items1.ShouldBe("items-1");
        items2.ShouldBe("items-1");
        live1.ShouldBe("live-1");
        live2.ShouldBe("live-2");
        hits[0].ShouldBe(1);
        hits[1].ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Conventions: An empty policy name should be rejected when declared")]
    public void CacheOutput_EmptyPolicyName_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder route = new RouterBuilder().Map(CohesionHttpMethod.Get, "/x", Counting("x", new int[1], 0));

        // Act
        Action act = () => route.CacheOutput(string.Empty);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    private static RouterRouteHandler Counting(string name, int[] hits, int slot) => new(async context =>
    {
        int n = Interlocked.Increment(ref hits[slot]);
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{name}-{n}"), context.RequestCancelled);
    });
}

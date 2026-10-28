using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>Checks that deferred CORS grants compose with the output cache's earlier response snapshot.</summary>
public class CorsOutputCacheTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Output cache: Origin variants stay separate and cache hits receive the current CORS grant")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseCors_OutputCacheAcrossOrigins_ShouldPartitionBodiesAndReapplyGrantOnHit(WebApplicationTestProtocol protocol)
    {
        // Arrange — Vary must be available when the cache stores the response, before the head commits.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddCors(cors => cors.Options.AddDefaultPolicy(policy => policy
            .WithOrigins("https://alpha.example", "https://beta.example")
            .AllowCredentials()));
        int invocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = TimeSpan.FromMinutes(30)));
        routes.Map(CohesionHttpMethod.Get, "/variant", new RouterRouteHandler(async context =>
        {
            int invocation = Interlocked.Increment(ref invocations);
            string origin = context.Request.Headers[HttpHeaderKey.Origin].Value;
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            // The cache captures this stale grant before CORS replaces it at head commit. A hit must
            // receive the policy's grant too, even though the endpoint does not run again.
            context.Response.Headers[HttpHeaderKey.AccessControlAllowOrigin] = "https://stale.example";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{origin}:{invocation}"), context.RequestCancelled);
        }));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage alpha = await SendAsync(client, "https://alpha.example", cancellation.Token);
        using HttpResponseMessage beta = await SendAsync(client, "https://beta.example", cancellation.Token);
        using HttpResponseMessage alphaHit = await SendAsync(client, "https://alpha.example", cancellation.Token);
        using HttpResponseMessage betaHit = await SendAsync(client, "https://beta.example", cancellation.Token);

        // Assert — neither origin receives the other's stored body, and both repeats bypass the endpoint.
        (await alpha.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("https://alpha.example:1");
        (await beta.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("https://beta.example:2");
        (await alphaHit.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("https://alpha.example:1");
        (await betaHit.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("https://beta.example:2");
        invocations.ShouldBe(2);
        alphaHit.Headers.Contains("Age").ShouldBeTrue();
        betaHit.Headers.Contains("Age").ShouldBeTrue();

        foreach ((HttpResponseMessage response, string origin) in new[]
        {
            (alpha, "https://alpha.example"),
            (beta, "https://beta.example"),
            (alphaHit, "https://alpha.example"),
            (betaHit, "https://beta.example"),
        })
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(origin);
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
            response.Headers.Vary.ShouldContain("Origin");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string origin, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/variant")
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, cancellationToken);
    }
}

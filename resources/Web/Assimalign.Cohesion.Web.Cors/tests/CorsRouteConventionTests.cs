using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// The endpoint convention verbs over the real router: <c>RequireCors</c> by name, by policy and by
/// configuration, on a route and on a group; <c>DisableCors</c> exempting one route of a group, and a route
/// re-enabling CORS that its group disabled.
/// </summary>
public class CorsRouteConventionTests
{
    private const string App = "https://app.example";
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: A named policy required on a route should govern only that route")]
    public async Task RequireCors_NamedPolicyOnRoute_ShouldGovernOnlyThatRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddPolicy("spa", policy => policy.WithOrigins(App)));
        routes.Map(CohesionHttpMethod.Get, "/shared", Ok()).RequireCors("spa");
        routes.Map(CohesionHttpMethod.Get, "/internal", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage shared = await client.SendAsync(Request("/shared"), cancellation.Token);
        using HttpResponseMessage @internal = await client.SendAsync(Request("/internal"), cancellation.Token);

        // Assert
        AllowOrigin(shared).ShouldBe(App);
        AllowOrigin(@internal).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: A group policy should govern routes mapped before and after it")]
    public async Task RequireCors_PolicyOnGroup_ShouldGovernRoutesMappedBeforeAndAfter()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        IRouterGroupBuilder api = routes.MapGroup("/api");
        api.Map(CohesionHttpMethod.Get, "early", Ok());
        api.RequireCors(new CorsPolicyBuilder().WithOrigins(App).Build());
        api.Map(CohesionHttpMethod.Get, "late", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage early = await client.SendAsync(Request("/api/early"), cancellation.Token);
        using HttpResponseMessage late = await client.SendAsync(Request("/api/late"), cancellation.Token);

        // Assert
        AllowOrigin(early).ShouldBe(App);
        AllowOrigin(late).ShouldBe(App);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: DisableCors on a route should exempt it from its group's policy")]
    public async Task DisableCors_OnRouteInGroupWithPolicy_ShouldExemptTheRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        IRouterGroupBuilder api = routes.MapGroup("/api").RequireCors(policy => policy.AllowAnyOrigin());
        api.Map(CohesionHttpMethod.Get, "open", Ok());
        api.Map(CohesionHttpMethod.Get, "closed", Ok()).DisableCors();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage open = await client.SendAsync(Request("/api/open"), cancellation.Token);
        using HttpResponseMessage closed = await client.SendAsync(Request("/api/closed"), cancellation.Token);

        // Assert
        AllowOrigin(open).ShouldBe("*");
        AllowOrigin(closed).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: A route should re-enable CORS that its group disabled")]
    public async Task RequireCors_OnRouteInDisabledGroup_ShouldApplyTheRoutePolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));
        IRouterGroupBuilder admin = routes.MapGroup("/admin").DisableCors();
        admin.Map(CohesionHttpMethod.Get, "status", Ok()).RequireCors(policy => policy.WithOrigins(App));
        admin.Map(CohesionHttpMethod.Get, "secrets", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage status = await client.SendAsync(Request("/admin/status"), cancellation.Token);
        using HttpResponseMessage secrets = await client.SendAsync(Request("/admin/secrets"), cancellation.Token);

        // Assert
        AllowOrigin(status).ShouldBe(App);
        AllowOrigin(secrets).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: An invalid inline policy should fail where the route is mapped")]
    public void RequireCors_InvalidConfiguredPolicy_ShouldThrowWhenMapped()
    {
        // Arrange
        RouterBuilder routes = new();
        IRouterRouteBuilder route = routes.Map(CohesionHttpMethod.Get, "/items", Ok());

        // Act
        Action act = () => route.RequireCors(policy => policy.AllowAnyOrigin().AllowCredentials());

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("AllowCredentials");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: A verb on a null builder should throw ArgumentNullException")]
    public void RequireCors_NullBuilder_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder builder = null!;

        // Act
        Action named = () => builder.RequireCors("spa");
        Action disabled = () => builder.DisableCors();

        // Assert
        named.ShouldThrow<ArgumentNullException>();
        disabled.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Conventions: A verb should attach the CORS metadata to the route")]
    public void RequireCors_OnRoute_ShouldAttachMetadata()
    {
        // Arrange
        RouterBuilder routes = new();
        CorsPolicy policy = new CorsPolicyBuilder().WithOrigins(App).Build();

        // Act
        routes.Map(CohesionHttpMethod.Get, "/named", Ok()).RequireCors("spa");
        routes.Map(CohesionHttpMethod.Get, "/inline", Ok()).RequireCors(policy);
        routes.Map(CohesionHttpMethod.Get, "/disabled", Ok()).DisableCors();
        IEnumerable<IRouterRoute> built = routes.Build().Routes;

        // Assert
        Metadata(built, "/named").PolicyName.ShouldBe("spa");
        Metadata(built, "/inline").Policy.ShouldBeSameAs(policy);
        Metadata(built, "/disabled").ShouldBeSameAs(CorsMetadata.Disabled);
    }

    private static CorsMetadata Metadata(IEnumerable<IRouterRoute> routes, string pattern)
    {
        foreach (IRouterRoute route in routes)
        {
            if (route.Pattern?.RawText == pattern)
            {
                return route.Metadata.GetMetadata<CorsMetadata>().ShouldNotBeNull();
            }
        }

        throw new InvalidOperationException($"No route '{pattern}' was mapped.");
    }

    private static RouterRouteHandler Ok() => new(context =>
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        return Task.CompletedTask;
    });

    private static HttpRequestMessage Request(string path)
    {
        HttpRequestMessage request = new(NetHttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Origin", App).ShouldBeTrue();
        return request;
    }

    private static string? AllowOrigin(HttpResponseMessage response)
        => response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values) ? string.Join(", ", values) : null;
}

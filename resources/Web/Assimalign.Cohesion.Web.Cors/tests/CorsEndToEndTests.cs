using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> with the real router: wildcard,
/// credentialed and denied cross-origin requests on the wire; a preflight answered through its candidate
/// endpoint, which never runs; named, group-level and route-level policies; <c>DisableCors</c>; preflight
/// grants and denials for methods and headers; exposed headers and max age; the preflight with no
/// candidate; the explicit <c>OPTIONS</c> route; ordering against <c>UseRouting</c> and the fail-closed
/// dispatch; and an exception boundary behind <c>UseCors</c>.
/// </summary>
public class CorsEndToEndTests
{
    private const string App = "https://app.example";
    private const string Other = "https://other.example";
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A wildcard policy should answer '*' on the wire without Vary")]
    public async Task UseCors_WildcardPolicy_ShouldAnswerWildcard()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));
        routes.Map(CohesionHttpMethod.Get, "/items", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.SendAsync(Request(NetHttpMethod.Get, "/items", App), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(response, "Access-Control-Allow-Origin").ShouldBe("*");
        Header(response, "Access-Control-Allow-Credentials").ShouldBeNull();
        Header(response, "Vary").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A credentialed policy should echo the origin with credentials and Vary: Origin on the wire")]
    public async Task UseCors_CredentialedPolicy_ShouldEchoOrigin()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowCredentials()));
        routes.Map(CohesionHttpMethod.Get, "/items", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.SendAsync(Request(NetHttpMethod.Get, "/items", App), cancellation.Token);

        // Assert
        Header(response, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(response, "Access-Control-Allow-Credentials").ShouldBe("true");
        Header(response, "Vary").ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A denied origin should get the response without any CORS header")]
    public async Task UseCors_DeniedOrigin_ShouldSendNoCorsHeaders()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int invocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowCredentials()));
        routes.Map(CohesionHttpMethod.Get, "/items", Ok(() => Interlocked.Increment(ref invocations)));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.SendAsync(Request(NetHttpMethod.Get, "/items", Other), cancellation.Token);

        // Assert — the server answers; the browser withholds the response from the calling script.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        invocations.ShouldBe(1);
        Header(response, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(response, "Access-Control-Allow-Credentials").ShouldBeNull();
        Header(response, "Vary").ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A preflight should be answered through its candidate endpoint, which never runs")]
    public async Task UseCors_PreflightThroughCandidate_ShouldAnswerWithoutRunningEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int invocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        routes.Map(CohesionHttpMethod.Delete, "/items/{id:int}", Ok(() => Interlocked.Increment(ref invocations)))
            .RequireCors(policy => policy.WithOrigins(App).WithMethods("DELETE").AllowCredentials());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage preflight = await client.SendAsync(Preflight("/items/7", App, "DELETE"), cancellation.Token);
        int invocationsAfterPreflight = invocations;
        using HttpResponseMessage actual = await client.SendAsync(Request(NetHttpMethod.Delete, "/items/7", App), cancellation.Token);

        // Assert
        preflight.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        invocationsAfterPreflight.ShouldBe(0);
        Header(preflight, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(preflight, "Access-Control-Allow-Methods").ShouldBe("DELETE");
        Header(preflight, "Access-Control-Allow-Credentials").ShouldBe("true");
        actual.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        invocations.ShouldBe(1);
        Header(actual, "Access-Control-Allow-Origin").ShouldBe(App);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A named policy should govern the route that requires it")]
    public async Task UseCors_NamedPolicy_ShouldApplyToRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options
            .AddDefaultPolicy(policy => policy.WithOrigins(App))
            .AddPolicy("partners", policy => policy.WithOrigins(Other)));
        routes.Map(CohesionHttpMethod.Get, "/partners", Ok()).RequireCors("partners");
        routes.Map(CohesionHttpMethod.Get, "/items", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage partners = await client.SendAsync(Request(NetHttpMethod.Get, "/partners", Other), cancellation.Token);
        using HttpResponseMessage items = await client.SendAsync(Request(NetHttpMethod.Get, "/items", Other), cancellation.Token);

        // Assert
        Header(partners, "Access-Control-Allow-Origin").ShouldBe(Other);
        Header(items, "Access-Control-Allow-Origin").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A route's policy should override its group's, and the group's should govern the rest")]
    public async Task UseCors_GroupPolicyWithRouteOverride_ShouldApplyMostSpecific()
    {
        // Arrange — the group policy is attached after a route is mapped; composition happens at build.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options
            .AddPolicy("group", policy => policy.WithOrigins(App))
            .AddPolicy("route", policy => policy.WithOrigins(Other)));

        IRouterGroupBuilder api = routes.MapGroup("/api");
        api.Map(CohesionHttpMethod.Get, "special", Ok()).RequireCors("route");
        api.RequireCors("group");
        api.Map(CohesionHttpMethod.Get, "plain", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage specialFromOther = await client.SendAsync(Request(NetHttpMethod.Get, "/api/special", Other), cancellation.Token);
        using HttpResponseMessage specialFromApp = await client.SendAsync(Request(NetHttpMethod.Get, "/api/special", App), cancellation.Token);
        using HttpResponseMessage plainFromApp = await client.SendAsync(Request(NetHttpMethod.Get, "/api/plain", App), cancellation.Token);
        using HttpResponseMessage plainFromOther = await client.SendAsync(Request(NetHttpMethod.Get, "/api/plain", Other), cancellation.Token);

        // Assert
        Header(specialFromOther, "Access-Control-Allow-Origin").ShouldBe(Other);
        Header(specialFromApp, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(plainFromApp, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(plainFromOther, "Access-Control-Allow-Origin").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: DisableCors should keep CORS out of a route's responses and leave its preflight unanswered")]
    public async Task UseCors_DisableCorsOnRoute_ShouldSendNoCorsHeaders()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int invocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod()));
        IRouterGroupBuilder admin = routes.MapGroup("/admin").RequireCors(policy => policy.AllowAnyOrigin().AllowAnyMethod());
        admin.Map(CohesionHttpMethod.Delete, "cache", Ok(() => Interlocked.Increment(ref invocations))).DisableCors();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage preflight = await client.SendAsync(Preflight("/admin/cache", App, "DELETE"), cancellation.Token);
        using HttpResponseMessage actual = await client.SendAsync(Request(NetHttpMethod.Delete, "/admin/cache", App), cancellation.Token);

        // Assert — the unanswered preflight is the plain OPTIONS request it is.
        preflight.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        Header(preflight, "Access-Control-Allow-Origin").ShouldBeNull();
        actual.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        invocations.ShouldBe(1);
        Header(actual, "Access-Control-Allow-Origin").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A preflight within the policy should be granted its headers, and one outside it denied")]
    public async Task UseCors_PreflightMethodsAndHeaders_ShouldGrantOrDeny()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithHeaders("Content-Type", "X-Request-Id")));
        routes.Map([CohesionHttpMethod.Put, CohesionHttpMethod.Patch], "/items/{id:int}", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage granted = await client.SendAsync(Preflight("/items/1", App, "PUT", "content-type,x-request-id"), cancellation.Token);
        using HttpResponseMessage deniedMethod = await client.SendAsync(Preflight("/items/1", App, "PATCH", "content-type"), cancellation.Token);
        using HttpResponseMessage deniedHeader = await client.SendAsync(Preflight("/items/1", App, "PUT", "content-type,x-secret"), cancellation.Token);

        // Assert
        granted.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(granted, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(granted, "Access-Control-Allow-Methods").ShouldBe("PUT");
        Header(granted, "Access-Control-Allow-Headers").ShouldBe("Content-Type, X-Request-Id");

        deniedMethod.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(deniedMethod, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(deniedMethod, "Access-Control-Allow-Methods").ShouldBeNull();

        deniedHeader.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(deniedHeader, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(deniedHeader, "Access-Control-Allow-Headers").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: Exposed headers and the preflight max age should reach the wire")]
    public async Task UseCors_ExposedHeadersAndMaxAge_ShouldReachTheWire()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithExposedHeaders("ETag", "X-Total-Count")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
        routes.Map([CohesionHttpMethod.Get, CohesionHttpMethod.Put], "/items", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage preflight = await client.SendAsync(Preflight("/items", App, "PUT"), cancellation.Token);
        using HttpResponseMessage actual = await client.SendAsync(Request(NetHttpMethod.Get, "/items", App), cancellation.Token);

        // Assert — max age belongs to the preflight; exposed headers to the actual response.
        Header(preflight, "Access-Control-Max-Age").ShouldBe("3600");
        Header(preflight, "Access-Control-Expose-Headers").ShouldBeNull();
        Header(actual, "Access-Control-Expose-Headers").ShouldBe("ETag, X-Total-Count");
        Header(actual, "Access-Control-Max-Age").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A preflight for a method no route serves should be answered by the default policy, else 405")]
    public async Task UseCors_PreflightWithoutCandidate_ShouldUseDefaultPolicyOrFallThrough()
    {
        // Arrange — two applications: one with a default policy, one with only a named policy.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory withDefault = CreateItemsApplication(options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PUT")));
        await using WebApplicationTestFactory withoutDefault = CreateItemsApplication(options => options.AddPolicy("named", policy => policy.WithOrigins(App).WithMethods("PUT")));

        using HttpClient defaultClient = withDefault.CreateClient();
        using HttpClient namedClient = withoutDefault.CreateClient();

        // Act — /items serves GET only, so routing publishes no candidate for PUT.
        using HttpResponseMessage answered = await defaultClient.SendAsync(Preflight("/items", App, "PUT"), cancellation.Token);
        using HttpResponseMessage actual = await defaultClient.SendAsync(Request(NetHttpMethod.Put, "/items", App), cancellation.Token);
        using HttpResponseMessage unanswered = await namedClient.SendAsync(Preflight("/items", App, "PUT"), cancellation.Token);

        // Assert — the actual request cannot reach a handler, and its 405 is readable by the caller.
        answered.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(answered, "Access-Control-Allow-Origin").ShouldBe(App);
        actual.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        Header(actual, "Access-Control-Allow-Origin").ShouldBe(App);
        unanswered.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        Header(unanswered, "Access-Control-Allow-Origin").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A preflight for 'patch' should not resolve the PATCH route, because methods are case-sensitive")]
    public async Task UseCors_PreflightForMethodInAnotherCase_ShouldNotUseTheRoutePolicy()
    {
        // Arrange — Fetch sends 'patch' as written, and the actual 'patch' request matches no PATCH route.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        routes.Map(CohesionHttpMethod.Patch, "/items", Ok()).RequireCors(policy => policy.WithOrigins(App).AllowAnyMethod());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage exact = await client.SendAsync(Preflight("/items", App, "PATCH"), cancellation.Token);
        using HttpResponseMessage otherCase = await client.SendAsync(Preflight("/items", App, "patch"), cancellation.Token);

        // Assert
        exact.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(exact, "Access-Control-Allow-Origin").ShouldBe(App);
        otherCase.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        Header(otherCase, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(otherCase, "Access-Control-Allow-Methods").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: An explicit OPTIONS route should answer its path's preflights itself")]
    public async Task UseCors_PreflightToExplicitOptionsRoute_ShouldLetTheRouteAnswer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int optionsInvocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod()));
        routes.Map(CohesionHttpMethod.Options, "/items", Ok(() => Interlocked.Increment(ref optionsInvocations)));
        routes.Map(CohesionHttpMethod.Delete, "/items", Ok()).RequireCors(policy => policy.WithOrigins(Other).WithMethods("DELETE"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage preflight = await client.SendAsync(Preflight("/items", App, "DELETE"), cancellation.Token);

        // Assert — the permissive default never approves the DELETE endpoint's preflight on its behalf.
        preflight.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        optionsInvocations.ShouldBe(1);
        Header(preflight, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(preflight, "Access-Control-Allow-Methods").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A same-origin request to an endpoint that requires CORS should run")]
    public async Task UseCors_RequestWithoutOriginToEndpointWithPolicy_ShouldRun()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int invocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors();
        routes.Map(CohesionHttpMethod.Get, "/items", Ok(() => Interlocked.Increment(ref invocations)))
            .RequireCors(policy => policy.WithOrigins(App));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/items", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        invocations.ShouldBe(1);
        Header(response, "Access-Control-Allow-Origin").ShouldBeNull();
        Header(response, "Vary").ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: Registered before UseRouting, an endpoint that declares CORS should fail at dispatch instead of running under the default policy")]
    public async Task UseCors_RegisteredBeforeRouting_ShouldFailEndpointWithCorsMetadata()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        InvalidOperationException? dispatchFailure = null;
        int invocations = 0;

        ObserveDispatchFailures(factory, exception => dispatchFailure = exception);
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/private", Ok(() => Interlocked.Increment(ref invocations))).DisableCors();
        routes.Map(CohesionHttpMethod.Get, "/public", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage publicResponse = await client.SendAsync(Request(NetHttpMethod.Get, "/public", App), cancellation.Token);
        using HttpResponseMessage privateResponse = await client.SendAsync(Request(NetHttpMethod.Get, "/private", App), cancellation.Token);

        // Assert — the endpoint without CORS metadata runs under the default policy; the one that opted out
        // is not run with the default policy's '*'.
        publicResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(publicResponse, "Access-Control-Allow-Origin").ShouldBe("*");
        privateResponse.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseCors()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: Without UseCors, an endpoint that declares CORS should fail at dispatch")]
    public async Task UseCors_Missing_ShouldFailEndpointThatDeclaresCors()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        InvalidOperationException? dispatchFailure = null;

        ObserveDispatchFailures(factory, exception => dispatchFailure = exception);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/items", Ok()).RequireCors(policy => policy.WithOrigins(App));
        routes.Map(CohesionHttpMethod.Get, "/plain", Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage plain = await client.SendAsync(Request(NetHttpMethod.Get, "/plain", App), cancellation.Token);
        InvalidOperationException? plainFailure = dispatchFailure;
        using HttpResponseMessage items = await client.SendAsync(Request(NetHttpMethod.Get, "/items", App), cancellation.Token);

        // Assert
        plain.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        plainFailure.ShouldBeNull();
        items.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("CorsMetadata", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: An exception boundary behind UseCors should keep the CORS headers on the fault response")]
    public async Task UseCors_ExceptionBoundaryBehindCors_ShouldKeepCorsHeadersOnFault()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowCredentials()));
        factory.Application.UseErrorHandling();
        routes.Map(CohesionHttpMethod.Get, "/boom", new RouterRouteHandler(_ => throw new InvalidOperationException("fault")));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.SendAsync(Request(NetHttpMethod.Get, "/boom", App), cancellation.Token);

        // Assert — the boundary cleared the response before writing its problem details; the caller can read it.
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        Header(response, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(response, "Access-Control-Allow-Credentials").ShouldBe("true");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - E2E: A preflight over HTTP/2 should be answered like one over HTTP/1.1")]
    public async Task UseCors_PreflightOverHttp2_ShouldAnswer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = WebApplicationTestProtocol.Http2 });
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PUT").WithHeaders("Content-Type")));
        routes.Map(CohesionHttpMethod.Put, "/items", Ok());

        using HttpClient client = factory.CreateClient();

        // A hand-built message carries its own version; the client's prior-knowledge HTTP/2 default applies
        // only to the requests the client builds itself.
        using HttpRequestMessage request = Preflight("/items", App, "PUT", "content-type");
        request.Version = client.DefaultRequestVersion;
        request.VersionPolicy = client.DefaultVersionPolicy;

        // Act
        using HttpResponseMessage preflight = await client.SendAsync(request, cancellation.Token);

        // Assert
        preflight.Version.Major.ShouldBe(2);
        preflight.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        Header(preflight, "Access-Control-Allow-Origin").ShouldBe(App);
        Header(preflight, "Access-Control-Allow-Headers").ShouldBe("Content-Type");
    }

    private static WebApplicationTestFactory CreateItemsApplication(Action<CorsOptions> configure)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseCors(configure);
        routes.Map(CohesionHttpMethod.Get, "/items", Ok());

        return factory;
    }

    // Observes routing's dispatch failure the way an exception boundary would, ahead of everything else.
    private static void ObserveDispatchFailures(WebApplicationTestFactory factory, Action<InvalidOperationException> observe)
    {
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                observe(exception);
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });
    }

    private static RouterRouteHandler Ok(Action? onInvoke = null) => new(context =>
    {
        onInvoke?.Invoke();
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        return Task.CompletedTask;
    });

    private static HttpRequestMessage Request(NetHttpMethod method, string path, string origin)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.TryAddWithoutValidation("Origin", origin).ShouldBeTrue();
        return request;
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method, string? headers = null)
    {
        HttpRequestMessage request = Request(NetHttpMethod.Options, path, origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method).ShouldBeTrue();

        if (headers is not null)
        {
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers).ShouldBeTrue();
        }

        return request;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values) : null;
}

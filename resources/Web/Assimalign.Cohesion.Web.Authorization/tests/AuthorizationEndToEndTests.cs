using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// Full-pipeline coverage over the in-memory <see cref="WebApplicationTestFactory"/> with two
/// header-driven test schemes (<see cref="TestAuthenticationHandler"/>): challenge and forbid, role,
/// claim and delegate requirements, the default, named and fallback policies, how a group's and a
/// route's items combine, <c>AllowAnonymous</c>, per-endpoint scheme selection, the fail-closed
/// dispatch check, and CORS preflights. Requests are sequential on one client.
/// </summary>
public class AuthorizationEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: An anonymous request to a protected endpoint should be challenged")]
    public async Task UseAuthorization_AnonymousRequest_ShouldChallengeWithoutRunningTheEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/profile", TestEndpoints.Ok(invocations)).RequireAuthorization();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/profile", cancellation.Token);

        // Assert — RFC 9110 §15.5.2: the 401 carries the scheme's challenge.
        response.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe(TestEndpoints.Primary);
        invocations.Value.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: An authenticated user without the required role should be forbidden")]
    public async Task UseAuthorization_AuthenticatedUserMissingRole_ShouldForbidWithoutRunningTheEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok(invocations))
            .RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestEndpoints.Get("/admin", TestEndpoints.Primary, "alice;role=reader");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — RFC 9110 §15.5.4: authenticated but insufficient is a 403, answered by the scheme.
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        response.Headers.GetValues(TestAuthenticationHandler.ForbiddenHeader).ShouldBe(new[] { TestEndpoints.Primary });
        invocations.Value.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A user in the required role should reach the endpoint")]
    public async Task UseAuthorization_UserInRequiredRole_ShouldRunTheEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.RequireRole("admin", "ops"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestEndpoints.Get("/admin", TestEndpoints.Primary, "alice;role=ops");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A claim requirement should admit only the allowed values")]
    public async Task UseAuthorization_ClaimRequirement_ShouldAdmitOnlyAllowedValues()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/sales", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.RequireClaim("department", "sales"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage sales = TestEndpoints.Get("/sales", TestEndpoints.Primary, "alice;claim=department:sales");
        using HttpRequestMessage finance = TestEndpoints.Get("/sales", TestEndpoints.Primary, "bob;claim=department:finance");

        // Act
        using HttpResponseMessage admitted = await client.SendAsync(sales, cancellation.Token);
        using HttpResponseMessage refused = await client.SendAsync(finance, cancellation.Token);

        // Assert
        admitted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        refused.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Delegate requirements should decide over the principal and the exchange")]
    public async Task UseAuthorization_AssertionRequirements_ShouldDecideOverTheExchange()
    {
        // Arrange — a synchronous owner check over the route values, and an asynchronous check.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/documents/{owner}", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.RequireAssertion(context =>
                context.HttpContext.TryGetRouteValues(out RouteValueDictionary? values)
                && values!["owner"] is string owner
                && owner == context.User.Identity?.Name));
        routes.Map(CohesionHttpMethod.Get, "/reports", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.RequireAssertion(async (context, cancellationToken) =>
            {
                await Task.Delay(1, cancellationToken);
                return context.User.HasClaim("tier", "gold");
            }));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage ownDocument = TestEndpoints.Get("/documents/alice", TestEndpoints.Primary, "alice");
        using HttpRequestMessage otherDocument = TestEndpoints.Get("/documents/alice", TestEndpoints.Primary, "bob");
        using HttpRequestMessage gold = TestEndpoints.Get("/reports", TestEndpoints.Primary, "carol;claim=tier:gold");
        using HttpRequestMessage silver = TestEndpoints.Get("/reports", TestEndpoints.Primary, "dave;claim=tier:silver");

        // Act
        using HttpResponseMessage ownResponse = await client.SendAsync(ownDocument, cancellation.Token);
        using HttpResponseMessage otherResponse = await client.SendAsync(otherDocument, cancellation.Token);
        using HttpResponseMessage goldResponse = await client.SendAsync(gold, cancellation.Token);
        using HttpResponseMessage silverResponse = await client.SendAsync(silver, cancellation.Token);

        // Assert
        ownResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        otherResponse.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        goldResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        silverResponse.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A named policy should be resolved from the registered policies")]
    public async Task RequireAuthorization_NamedPolicy_ShouldApplyTheRegisteredPolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok()).RequireAuthorization("admins");

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage admin = TestEndpoints.Get("/admin", TestEndpoints.Primary, "alice;role=admin");
        using HttpRequestMessage reader = TestEndpoints.Get("/admin", TestEndpoints.Primary, "bob;role=reader");

        // Act
        using HttpResponseMessage admitted = await client.SendAsync(admin, cancellation.Token);
        using HttpResponseMessage refused = await client.SendAsync(reader, cancellation.Token);
        using HttpResponseMessage anonymous = await client.GetAsync("/admin", cancellation.Token);

        // Assert
        admitted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        refused.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: RequireAuthorization() should apply the default policy")]
    public async Task RequireAuthorization_NoArguments_ShouldApplyTheDefaultPolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/profile", TestEndpoints.Ok()).RequireAuthorization();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage signedIn = TestEndpoints.Get("/profile", TestEndpoints.Primary, "bob");

        // Act
        using HttpResponseMessage anonymous = await client.GetAsync("/profile", cancellation.Token);
        using HttpResponseMessage authenticated = await client.SendAsync(signedIn, cancellation.Token);

        // Assert
        anonymous.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        authenticated.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await authenticated.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:bob");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A reconfigured default policy should apply to RequireAuthorization()")]
    public async Task RequireAuthorization_ReconfiguredDefaultPolicy_ShouldApplyIt()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options =>
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireRole("member").Build());

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/profile", TestEndpoints.Ok()).RequireAuthorization();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage member = TestEndpoints.Get("/profile", TestEndpoints.Primary, "alice;role=member");
        using HttpRequestMessage guest = TestEndpoints.Get("/profile", TestEndpoints.Primary, "bob");

        // Act
        using HttpResponseMessage admitted = await client.SendAsync(member, cancellation.Token);
        using HttpResponseMessage refused = await client.SendAsync(guest, cancellation.Token);

        // Assert
        admitted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        refused.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: The fallback policy should protect endpoints with no authorization metadata")]
    public async Task UseAuthorization_FallbackPolicy_ShouldProtectUnannotatedEndpoints()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/home", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage signedIn = TestEndpoints.Get("/home", TestEndpoints.Primary, "alice");

        // Act
        using HttpResponseMessage anonymous = await client.GetAsync("/home", cancellation.Token);
        using HttpResponseMessage authenticated = await client.SendAsync(signedIn, cancellation.Token);

        // Assert
        anonymous.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        authenticated.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: The fallback policy should challenge requests that select no endpoint")]
    public async Task UseAuthorization_FallbackPolicyAndNoEndpoint_ShouldChallengeInsteadOfAnswering()
    {
        // Arrange — an unknown path (404) and a known path with the wrong method (405).
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/home", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage signedIn = TestEndpoints.Get("/missing", TestEndpoints.Primary, "alice");

        // Act
        using HttpResponseMessage anonymousMissing = await client.GetAsync("/missing", cancellation.Token);
        using HttpResponseMessage anonymousWrongMethod = await client.PostAsync("/home", content: null, cancellation.Token);
        using HttpResponseMessage authenticatedMissing = await client.SendAsync(signedIn, cancellation.Token);

        // Assert — an anonymous caller learns nothing about which paths exist.
        anonymousMissing.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        anonymousWrongMethod.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        authenticatedMissing.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Without a fallback policy, endpoints with no authorization metadata should stay open")]
    public async Task UseAuthorization_NoFallbackPolicy_ShouldLeaveUnannotatedEndpointsOpen()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/home", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/home", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: AllowAnonymous should exempt an endpoint from the fallback policy")]
    public async Task AllowAnonymous_WithFallbackPolicy_ShouldExemptTheEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/login", TestEndpoints.Ok()).AllowAnonymous();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/login", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A route's AllowAnonymous should exempt it from its group's requirement")]
    public async Task AllowAnonymous_OnRouteInProtectedGroup_ShouldExemptOnlyThatRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        IRouterGroupBuilder api = routes.MapGroup("/api").RequireAuthorization();
        api.Map(CohesionHttpMethod.Get, "public", TestEndpoints.Ok()).AllowAnonymous();
        api.Map(CohesionHttpMethod.Get, "private", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage publicResponse = await client.GetAsync("/api/public", cancellation.Token);
        using HttpResponseMessage privateResponse = await client.GetAsync("/api/private", cancellation.Token);

        // Assert
        publicResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        privateResponse.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A route's requirement should still apply inside a group that allows anonymous access")]
    public async Task RequireAuthorization_OnRouteInAnonymousGroup_ShouldStillApply()
    {
        // Arrange — the most specific item wins: the group's AllowAnonymous, although called after the
        // route was mapped, composes ahead of the route's requirement and so does not clear it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        IRouterGroupBuilder open = routes.MapGroup("/open");
        open.Map(CohesionHttpMethod.Get, "admin", TestEndpoints.Ok()).RequireAuthorization("admins");
        open.Map(CohesionHttpMethod.Get, "about", TestEndpoints.Ok());
        open.AllowAnonymous();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage admin = TestEndpoints.Get("/open/admin", TestEndpoints.Primary, "alice;role=admin");

        // Act
        using HttpResponseMessage anonymousResponse = await client.GetAsync("/open/admin", cancellation.Token);
        using HttpResponseMessage adminResponse = await client.SendAsync(admin, cancellation.Token);
        using HttpResponseMessage aboutResponse = await client.GetAsync("/open/about", cancellation.Token);

        // Assert
        anonymousResponse.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        adminResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        aboutResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A nested group's AllowAnonymous should clear only the requirements above it")]
    public async Task AllowAnonymous_OnNestedGroup_ShouldClearOnlyRequirementsAboveIt()
    {
        // Arrange — the outer group requires a sales claim; the nested group opens its routes; one route
        // then requires the admin role. An admin outside sales passes only if the outer claim was cleared.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        IRouterGroupBuilder sales = routes.MapGroup("/sales").RequireAuthorization(policy => policy.RequireClaim("department", "sales"));
        IRouterGroupBuilder open = sales.MapGroup("/open").AllowAnonymous();
        open.Map(CohesionHttpMethod.Get, "prices", TestEndpoints.Ok());
        open.Map(CohesionHttpMethod.Get, "settings", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage outsideAdmin = TestEndpoints.Get("/sales/open/settings", TestEndpoints.Primary, "bob;role=admin");
        using HttpRequestMessage salesUser = TestEndpoints.Get("/sales/open/settings", TestEndpoints.Primary, "carol;claim=department:sales");

        // Act
        using HttpResponseMessage pricesResponse = await client.GetAsync("/sales/open/prices", cancellation.Token);
        using HttpResponseMessage anonymousSettings = await client.GetAsync("/sales/open/settings", cancellation.Token);
        using HttpResponseMessage outsideAdminResponse = await client.SendAsync(outsideAdmin, cancellation.Token);
        using HttpResponseMessage salesUserResponse = await client.SendAsync(salesUser, cancellation.Token);

        // Assert
        pricesResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        anonymousSettings.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        outsideAdminResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        salesUserResponse.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: On one builder the later of AllowAnonymous and a requirement should win")]
    public async Task AllowAnonymous_AndRequirementOnOneRoute_ShouldFollowCallOrder()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/opened", TestEndpoints.Ok()).RequireAuthorization().AllowAnonymous();
        routes.Map(CohesionHttpMethod.Get, "/closed", TestEndpoints.Ok()).AllowAnonymous().RequireAuthorization();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage openedResponse = await client.GetAsync("/opened", cancellation.Token);
        using HttpResponseMessage closedResponse = await client.GetAsync("/closed", cancellation.Token);

        // Assert
        openedResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        closedResponse.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: An anonymous endpoint should keep the default scheme's principal")]
    public async Task AllowAnonymous_InGroupThatSelectsSchemes_ShouldSkipSchemeAuthentication()
    {
        // Arrange — the group selects Secondary; the route opts out, so nothing is authenticated for it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        IRouterGroupBuilder partners = routes.MapGroup("/partners")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(TestEndpoints.Secondary).RequireAuthenticatedUser());
        partners.Map(CohesionHttpMethod.Get, "about", TestEndpoints.Ok()).AllowAnonymous();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestEndpoints.Get("/partners/about", TestEndpoints.Primary, "alice");
        request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader(TestEndpoints.Secondary), "bob");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — context.User is still the principal UseAuthentication established.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A group's and a route's requirements should both have to pass")]
    public async Task RequireAuthorization_OnGroupAndRoute_ShouldRequireBoth()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        IRouterGroupBuilder staff = routes.MapGroup("/staff").RequireAuthorization(policy => policy.RequireRole("employee"));
        staff.Map(CohesionHttpMethod.Get, "payroll", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage employee = TestEndpoints.Get("/staff/payroll", TestEndpoints.Primary, "alice;role=employee");
        using HttpRequestMessage contractorAdmin = TestEndpoints.Get("/staff/payroll", TestEndpoints.Primary, "bob;role=admin");
        using HttpRequestMessage both = TestEndpoints.Get("/staff/payroll", TestEndpoints.Primary, "carol;role=employee;role=admin");

        // Act
        using HttpResponseMessage employeeResponse = await client.SendAsync(employee, cancellation.Token);
        using HttpResponseMessage contractorResponse = await client.SendAsync(contractorAdmin, cancellation.Token);
        using HttpResponseMessage bothResponse = await client.SendAsync(both, cancellation.Token);

        // Assert
        employeeResponse.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        contractorResponse.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        bothResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: An endpoint's schemes should replace the default scheme's principal")]
    public async Task RequireAuthorization_WithSchemes_ShouldEvaluateOnlyThoseSchemes()
    {
        // Arrange — Primary is the default scheme; the endpoint selects Secondary.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/partner", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(TestEndpoints.Secondary).RequireAuthenticatedUser());
        routes.Map(CohesionHttpMethod.Get, "/home", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage primaryOnly = TestEndpoints.Get("/partner", TestEndpoints.Primary, "alice");
        using HttpRequestMessage both = TestEndpoints.Get("/partner", TestEndpoints.Primary, "alice");
        both.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader(TestEndpoints.Secondary), "bob");
        using HttpRequestMessage home = TestEndpoints.Get("/home", TestEndpoints.Primary, "alice");
        home.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader(TestEndpoints.Secondary), "bob");

        // Act
        using HttpResponseMessage primaryOnlyResponse = await client.SendAsync(primaryOnly, cancellation.Token);
        using HttpResponseMessage bothResponse = await client.SendAsync(both, cancellation.Token);
        using HttpResponseMessage homeResponse = await client.SendAsync(home, cancellation.Token);

        // Assert — the default scheme's principal does not satisfy the endpoint and is challenged
        // through the endpoint's scheme; with a Secondary credential the endpoint runs as that principal,
        // while an endpoint that selects no scheme keeps the default scheme's.
        primaryOnlyResponse.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        primaryOnlyResponse.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe(TestEndpoints.Secondary);
        bothResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await bothResponse.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:bob");
        (await homeResponse.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Several schemes should be evaluated as one combined principal")]
    public async Task RequireAuthorization_WithTwoSchemes_ShouldCombineTheirPrincipals()
    {
        // Arrange — the role arrives through one scheme and the claim through the other.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/reports", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(TestEndpoints.Primary, TestEndpoints.Secondary)
                .RequireRole("admin")
                .RequireClaim("department", "sales"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestEndpoints.Get("/reports", TestEndpoints.Primary, "alice;role=admin");
        request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader(TestEndpoints.Secondary), "alice-partner;claim=department:sales");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — the first scheme's identity is the combined principal's primary identity.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A failure should be answered through every scheme the policy names")]
    public async Task RequireAuthorization_WithTwoSchemes_ShouldChallengeAndForbidThroughEach()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/reports", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(TestEndpoints.Primary, TestEndpoints.Secondary)
                .RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage reader = TestEndpoints.Get("/reports", TestEndpoints.Secondary, "bob;role=reader");

        // Act
        using HttpResponseMessage anonymous = await client.GetAsync("/reports", cancellation.Token);
        using HttpResponseMessage forbidden = await client.SendAsync(reader, cancellation.Token);

        // Assert
        anonymous.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme).ShouldBe(new[] { TestEndpoints.Primary, TestEndpoints.Secondary });
        forbidden.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        forbidden.Headers.GetValues(TestAuthenticationHandler.ForbiddenHeader).Single()
            .ShouldBe(TestEndpoints.Primary + ", " + TestEndpoints.Secondary);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Without UseAuthorization a protected endpoint should fail at dispatch")]
    public async Task UseAuthorization_Missing_ShouldFailProtectedEndpointsClosed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();
        StrongBox<InvalidOperationException?> dispatchFailure = ObserveDispatchFailures(factory);

        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseAuthentication();
        routes.Map(CohesionHttpMethod.Get, "/protected", TestEndpoints.Ok(invocations)).RequireAuthorization();
        routes.Map(CohesionHttpMethod.Get, "/home", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage home = await client.GetAsync("/home", cancellation.Token);
        InvalidOperationException? homeFailure = dispatchFailure.Value;
        using HttpResponseMessage protectedResponse = await client.GetAsync("/protected", cancellation.Token);

        // Assert
        home.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        homeFailure.ShouldBeNull();
        protectedResponse.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.Value.ShouldBe(0);
        dispatchFailure.Value.ShouldNotBeNull().Message.ShouldContain("UseAuthorization()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: UseAuthorization ahead of UseRouting should fail a protected endpoint at dispatch")]
    public async Task UseAuthorization_RegisteredBeforeRouting_ShouldFailProtectedEndpointsClosed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();
        StrongBox<InvalidOperationException?> dispatchFailure = ObserveDispatchFailures(factory);

        factory.Application.UseAuthentication();
        factory.Application.UseAuthorization();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/protected", TestEndpoints.Ok(invocations)).RequireAuthorization();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage signedIn = TestEndpoints.Get("/protected", TestEndpoints.Primary, "alice");

        // Act
        using HttpResponseMessage response = await client.SendAsync(signedIn, cancellation.Token);

        // Assert — even an authenticated caller is refused: the endpoint was never authorized.
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.Value.ShouldBe(0);
        dispatchFailure.Value.ShouldNotBeNull().Message.ShouldContain("UseAuthorization()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Without UseAuthorization a route that allows anonymous access in a protected group should still run")]
    public async Task AllowAnonymous_OnRouteInProtectedGroupWithoutMiddleware_ShouldRun()
    {
        // Arrange — the route's AllowAnonymous follows the group's requirement, so it requires nothing.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<InvalidOperationException?> dispatchFailure = ObserveDispatchFailures(factory);

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api").RequireAuthorization();
        api.Map(CohesionHttpMethod.Get, "public", TestEndpoints.Ok()).AllowAnonymous();
        api.Map(CohesionHttpMethod.Get, "private", TestEndpoints.Ok());

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage publicResponse = await client.GetAsync("/api/public", cancellation.Token);
        InvalidOperationException? publicFailure = dispatchFailure.Value;
        using HttpResponseMessage privateResponse = await client.GetAsync("/api/private", cancellation.Token);

        // Assert
        publicResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        publicFailure.ShouldBeNull();
        privateResponse.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        dispatchFailure.Value.ShouldNotBeNull().Message.ShouldContain("UseAuthorization()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: Without UseAuthorization a protected route in an anonymous group should fail at dispatch")]
    public async Task RequireAuthorization_OnRouteInAnonymousGroupWithoutMiddleware_ShouldFailAtDispatch()
    {
        // Arrange — the route's requirement is the last item, so dispatch and evaluation agree: protected.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();
        StrongBox<InvalidOperationException?> dispatchFailure = ObserveDispatchFailures(factory);

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder open = routes.MapGroup("/open").AllowAnonymous();
        open.Map(CohesionHttpMethod.Get, "settings", TestEndpoints.Ok(invocations)).RequireAuthorization();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/open/settings", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.Value.ShouldBe(0);
        dispatchFailure.Value.ShouldNotBeNull().Message.ShouldContain("UseAuthorization()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: A CORS preflight should not be authorized, challenged or answered")]
    public async Task UseAuthorization_CorsPreflight_ShouldNotAuthorizeTheCandidateEndpoint()
    {
        // Arrange — nothing answers the preflight, so the terminal answers the plain OPTIONS request.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        StrongBox<int> invocations = new();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Delete, "/items/{id:int}", TestEndpoints.Ok(invocations)).RequireAuthorization();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage preflight = new(NetHttpMethod.Options, "/items/7");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://app.example").ShouldBeTrue();
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE").ShouldBeTrue();

        // Act
        using HttpResponseMessage preflightResponse = await client.SendAsync(preflight, cancellation.Token);
        using HttpResponseMessage actual = await client.DeleteAsync("/items/7", cancellation.Token);

        // Assert
        preflightResponse.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        preflightResponse.Headers.WwwAuthenticate.ShouldBeEmpty();
        actual.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        invocations.Value.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: An unregistered policy name should fail the request instead of authorizing it")]
    public async Task RequireAuthorization_UnknownPolicyName_ShouldFailTheRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        StrongBox<int> invocations = new();
        StrongBox<InvalidOperationException?> dispatchFailure = ObserveDispatchFailures(factory);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok(invocations)).RequireAuthorization("missing");

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage signedIn = TestEndpoints.Get("/admin", TestEndpoints.Primary, "alice;role=admin");

        // Act
        using HttpResponseMessage response = await client.SendAsync(signedIn, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.Value.ShouldBe(0);
        dispatchFailure.Value.ShouldNotBeNull().Message.ShouldContain("'missing'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - E2E: UseAuthorization without AddAuthorization should fail when the pipeline is built")]
    public async Task UseAuthorization_WithoutAddAuthorization_ShouldFailPipelineBuild()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();
        factory.Application.UseAuthorization();

        // Act — building the pipeline is the application-start boundary.
        Action act = () => ((IWebApplicationPipelineBuilder)factory.Application).Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("AddAuthorization()", Case.Sensitive);
    }

    private static WebApplicationTestFactory CreateFactory(Action<AuthorizationOptions>? configure = null)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddAuthentication(TestEndpoints.Primary)
            .AddScheme(TestEndpoints.Scheme(TestEndpoints.Primary))
            .AddScheme(TestEndpoints.Scheme(TestEndpoints.Secondary));
        factory.Builder.AddAuthorization(configure);
        return factory;
    }

    // The supported order: routing publishes the endpoint, authentication establishes context.User from
    // the default scheme, authorization runs before the endpoint.
    private static IRouterBuilder UseAuthorizedRouting(WebApplicationTestFactory factory)
    {
        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseAuthentication();
        factory.Application.UseAuthorization();
        return routes;
    }

    // Registered first, so it observes a dispatch failure the way an exception boundary would.
    private static StrongBox<InvalidOperationException?> ObserveDispatchFailures(WebApplicationTestFactory factory)
    {
        StrongBox<InvalidOperationException?> failure = new();

        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                failure.Value = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });

        return failure;
    }
}

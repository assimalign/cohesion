using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// <see cref="AuthorizationOptions.GetEffectivePolicy"/> read directly, the way a component that describes
/// endpoints (Web.OpenApi) reads it: the fallback policy for an endpoint without authorization metadata,
/// named and default policies resolved with their schemes, group and route items combined in order, and the
/// most specific <c>AllowAnonymous</c> clearing what precedes it. <see cref="AuthorizationEndToEndTests"/>
/// covers the same rules through the middleware, which runs this computation.
/// </summary>
public class AuthorizationEffectivePolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: An endpoint without authorization metadata should get the fallback policy")]
    public void GetEffectivePolicy_NoAuthorizationMetadata_ShouldReturnTheFallbackPolicy()
    {
        // Arrange — metadata of other kinds does not count as authorization metadata.
        AuthorizationOptions options = new();
        options.FallbackPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser().Build();
        RouterRouteMetadataCollection metadata = new(new RouteNameMetadata("home"));

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert
        policy.ShouldBeSameAs(options.FallbackPolicy);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: Without a fallback policy an endpoint without authorization metadata should get none")]
    public void GetEffectivePolicy_NoAuthorizationMetadataWithoutFallback_ShouldReturnNull()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(RouterRouteMetadataCollection.Empty);

        // Assert
        policy.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: A named policy should resolve to its registered requirements and schemes")]
    public async Task GetEffectivePolicy_NamedPolicy_ShouldResolveItsRequirementsAndSchemes()
    {
        // Arrange
        AuthorizationOptions options = new();
        options.AddPolicy("partners", policy => policy.AddAuthenticationSchemes("ApiKey").RequireRole("partner"));
        RouterRouteMetadataCollection metadata = new(new AuthorizationMetadata("partners"));

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert
        policy.ShouldNotBeNull().AuthenticationSchemes.ShouldBe(new[] { "ApiKey" });
        (await policy.EvaluateAsync(CreateContext("partner"))).ShouldBeTrue();
        (await policy.EvaluateAsync(CreateContext("reader"))).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: An item that names no policy and no roles should get the configured default policy")]
    public void GetEffectivePolicy_ItemNamingNothing_ShouldUseTheDefaultPolicy()
    {
        // Arrange
        AuthorizationOptions options = new();
        options.DefaultPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("Cookies").RequireAuthenticatedUser().Build();
        RouterRouteMetadataCollection metadata = new(new AuthorizationMetadata());

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert
        policy.ShouldNotBeNull().AuthenticationSchemes.ShouldBe(new[] { "Cookies" });
        policy.Requirements.ShouldBe(options.DefaultPolicy.Requirements);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: A group's and a route's items should both apply, their schemes in declaration order")]
    public async Task GetEffectivePolicy_GroupAndRouteItems_ShouldCombineInOrder()
    {
        // Arrange — the group requires the employee role through Bearer; the route names a policy through ApiKey.
        AuthorizationOptions options = new();
        options.AddPolicy("partners", policy => policy.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser());
        RouterRouteMetadataCollection metadata = new(
            new AuthorizationMetadata { Roles = ["employee"], AuthenticationSchemes = ["Bearer"] },
            new AuthorizationMetadata("partners"));

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert — the group's role still applies to the route.
        policy.ShouldNotBeNull().AuthenticationSchemes.ShouldBe(new[] { "Bearer", "ApiKey" });
        (await policy.EvaluateAsync(CreateContext("employee"))).ShouldBeTrue();
        (await policy.EvaluateAsync(CreateContext("contractor"))).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: A route's AllowAnonymous should clear its group's requirement and keep the fallback policy away")]
    public void GetEffectivePolicy_LastItemAllowsAnonymous_ShouldReturnNull()
    {
        // Arrange
        AuthorizationOptions options = new();
        options.FallbackPolicy = options.DefaultPolicy;
        RouterRouteMetadataCollection metadata = new(new AuthorizationMetadata(), AuthorizationMetadata.AllowAnonymous);

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert
        policy.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: A requirement declared after AllowAnonymous should apply on its own")]
    public async Task GetEffectivePolicy_RequirementAfterAllowAnonymous_ShouldApplyOnlyLaterItems()
    {
        // Arrange — the outer group requires a sales claim through Bearer, a nested group opens its routes,
        // and the route requires the admin role through ApiKey.
        AuthorizationOptions options = new();
        RouterRouteMetadataCollection metadata = new(
            new AuthorizationMetadata(new AuthorizationPolicyBuilder().AddAuthenticationSchemes("Bearer").RequireClaim("department", "sales").Build()),
            AuthorizationMetadata.AllowAnonymous,
            new AuthorizationMetadata { Roles = ["admin"], AuthenticationSchemes = ["ApiKey"] });

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert — only the route's item applies: its scheme, and an admin outside sales passes.
        policy.ShouldNotBeNull().AuthenticationSchemes.ShouldBe(new[] { "ApiKey" });
        (await policy.EvaluateAsync(CreateContext("admin"))).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: An unregistered policy name should throw instead of resolving to nothing")]
    public void GetEffectivePolicy_UnregisteredPolicyName_ShouldThrowInvalidOperationException()
    {
        // Arrange
        AuthorizationOptions options = new();
        RouterRouteMetadataCollection metadata = new(new AuthorizationMetadata("missing"));

        // Act
        Action act = () => options.GetEffectivePolicy(metadata);

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'missing'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: A policy name AllowAnonymous cleared should not be resolved")]
    public void GetEffectivePolicy_UnregisteredNameBeforeAllowAnonymous_ShouldNotThrow()
    {
        // Arrange — the cleared item never applies, so its name is never looked up; the route's own
        // requirement (the default policy) still applies.
        AuthorizationOptions options = new();
        RouterRouteMetadataCollection metadata = new(
            new AuthorizationMetadata("missing"),
            AuthorizationMetadata.AllowAnonymous,
            new AuthorizationMetadata());

        // Act
        AuthorizationPolicy? policy = options.GetEffectivePolicy(metadata);

        // Assert
        policy.ShouldNotBeNull().Requirements.ShouldBe(options.DefaultPolicy.Requirements);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Effective policy: Null metadata should throw")]
    public void GetEffectivePolicy_NullMetadata_ShouldThrowArgumentNullException()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        Action act = () => options.GetEffectivePolicy(null!);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    // An authenticated principal in one role.
    private static AuthorizationContext CreateContext(string role)
        => new(TestHttpContext.Create(), new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test")));
}

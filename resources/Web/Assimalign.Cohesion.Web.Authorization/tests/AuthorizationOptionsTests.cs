using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// The builder-time options and their registration: the default and fallback policies, named policies,
/// and the read-only snapshot <c>AddAuthorization</c> hands to the pipeline.
/// </summary>
public class AuthorizationOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: The default policy should require an authenticated user")]
    public async Task DefaultPolicy_Default_ShouldRequireAuthenticatedUser()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        bool anonymous = await options.DefaultPolicy.EvaluateAsync(new AuthorizationContext(TestHttpContext.Create(), new ClaimsPrincipal(new ClaimsIdentity())));
        bool authenticated = await options.DefaultPolicy.EvaluateAsync(new AuthorizationContext(TestHttpContext.Create(), new ClaimsPrincipal(new ClaimsIdentity("Test"))));

        // Assert
        anonymous.ShouldBeFalse();
        authenticated.ShouldBeTrue();
        options.DefaultPolicy.AuthenticationSchemes.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: The fallback policy should be null by default")]
    public void FallbackPolicy_Default_ShouldBeNull()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        AuthorizationPolicy? fallback = options.FallbackPolicy;

        // Assert
        fallback.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: Registering a policy name twice should throw")]
    public void AddPolicy_DuplicateName_ShouldThrowInvalidOperationException()
    {
        // Arrange
        AuthorizationOptions options = new();
        options.AddPolicy("admins", policy => policy.RequireRole("admin"));

        // Act
        Action act = () => options.AddPolicy("admins", policy => policy.RequireRole("root"));

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'admins'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: A configured policy without a requirement should be rejected at registration")]
    public void AddPolicy_ConfiguredWithoutRequirement_ShouldThrowInvalidOperationException()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        Action act = () => options.AddPolicy("bearer", policy => policy.AddAuthenticationSchemes("Bearer"));

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: Setting a null default policy should throw")]
    public void DefaultPolicy_SetNull_ShouldThrowArgumentNullException()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        Action act = () => options.DefaultPolicy = null!;

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: AddAuthorization should register one application feature")]
    public void AddAuthorization_WithPolicies_ShouldRegisterOneFeature()
    {
        // Arrange
        StubWebApplicationBuilder builder = new();

        // Act
        IWebApplicationBuilder result = builder.AddAuthorization(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));

        // Assert
        result.ShouldBeSameAs(builder);
        builder.Features.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: Options should become read-only once AddAuthorization returns")]
    public void AddAuthorization_OptionsMutatedAfterward_ShouldThrowInvalidOperationException()
    {
        // Arrange
        StubWebApplicationBuilder builder = new();
        AuthorizationOptions? captured = null;
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireRole("admin").Build();

        builder.AddAuthorization(options => captured = options);

        // Act
        Action addPolicy = () => captured!.AddPolicy("late", policy);
        Action setDefault = () => captured!.DefaultPolicy = policy;
        Action setFallback = () => captured!.FallbackPolicy = policy;

        // Assert
        addPolicy.ShouldThrow<InvalidOperationException>();
        setDefault.ShouldThrow<InvalidOperationException>();
        setFallback.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: AddAuthorization on a null builder should throw")]
    public void AddAuthorization_NullBuilder_ShouldThrowArgumentNullException()
    {
        // Arrange
        IWebApplicationBuilder builder = null!;

        // Act
        Action act = () => builder.AddAuthorization();

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: UseAuthorization on a null pipeline builder should throw")]
    public void UseAuthorization_NullBuilder_ShouldThrowArgumentNullException()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act
        Action act = () => builder.UseAuthorization();

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }
}

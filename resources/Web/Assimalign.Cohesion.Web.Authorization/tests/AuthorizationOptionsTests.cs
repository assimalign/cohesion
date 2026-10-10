using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// The builder-time options and their registration: the default and fallback policies, named policies,
/// the read-only snapshot <c>builder.Services.AddAuthorization</c> hands to the pipeline, and reading that snapshot back
/// from the application context.
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

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: AddAuthorization should register one IHttpFeature singleton")]
    public void AddAuthorization_WithPolicies_ShouldRegisterOneFeatureSingleton()
    {
        // Arrange
        ServiceProviderBuilder builder = new();

        // Act
        IServiceProviderBuilder result = builder.AddAuthorization(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));

        // Assert — one singleton the host stamps onto every exchange (owner decision 35).
        result.ShouldBeSameAs(builder);
        ServiceDescriptor descriptor = builder.Container.ShouldHaveSingleItem();
        descriptor.ServiceType.ShouldBe(typeof(IHttpFeature));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        Resolve(builder).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Registration: Options should become read-only once AddAuthorization returns")]
    public void AddAuthorization_OptionsMutatedAfterward_ShouldThrowInvalidOperationException()
    {
        // Arrange
        ServiceProviderBuilder builder = new();
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

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: A registered policy should be resolvable by its exact name")]
    public void TryGetPolicy_RegisteredName_ShouldReturnThePolicy()
    {
        // Arrange
        AuthorizationPolicy admins = new AuthorizationPolicyBuilder().AddAuthenticationSchemes("Bearer").RequireRole("admin").Build();
        AuthorizationOptions options = new();
        options.AddPolicy("admins", admins);

        // Act
        bool found = options.TryGetPolicy("admins", out AuthorizationPolicy? policy);

        // Assert
        found.ShouldBeTrue();
        policy.ShouldNotBeNull().ShouldBeSameAs(admins);
        policy.AuthenticationSchemes.ShouldBe(new[] { "Bearer" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: An unregistered or differently cased name should not resolve")]
    public void TryGetPolicy_UnregisteredOrDifferentlyCasedName_ShouldReturnFalse()
    {
        // Arrange — names compare ordinal, so a casing slip does not resolve to another policy.
        AuthorizationOptions options = new();
        options.AddPolicy("admins", policy => policy.RequireRole("admin"));

        // Act
        bool unregistered = options.TryGetPolicy("auditors", out AuthorizationPolicy? unregisteredPolicy);
        bool differentCase = options.TryGetPolicy("Admins", out AuthorizationPolicy? differentCasePolicy);

        // Assert
        unregistered.ShouldBeFalse();
        unregisteredPolicy.ShouldBeNull();
        differentCase.ShouldBeFalse();
        differentCasePolicy.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Options: Resolving a null policy name should throw")]
    public void TryGetPolicy_NullName_ShouldThrowArgumentNullException()
    {
        // Arrange
        AuthorizationOptions options = new();

        // Act
        Action act = () => options.TryGetPolicy(null!, out _);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Read access: The application context should hand back the read-only registered options")]
    public void TryGetAuthorizationOptions_AfterAddAuthorization_ShouldReturnTheReadOnlyRegistration()
    {
        // Arrange
        ServiceProviderBuilder builder = new();
        AuthorizationOptions? configured = null;
        builder.AddAuthorization(options =>
        {
            options.AddPolicy("admins", policy => policy.RequireRole("admin"));
            options.FallbackPolicy = options.DefaultPolicy;
            configured = options;
        });

        IWebApplicationContext context = new StubWebApplicationContext(Resolve(builder));

        // Act
        bool found = context.TryGetAuthorizationOptions(out AuthorizationOptions? registered);
        Action clearFallback = () => registered!.FallbackPolicy = null;
        Action addPolicy = () => registered!.AddPolicy("late", policy => policy.RequireRole("late"));

        // Assert — the very instance the callback configured, and still read-only.
        found.ShouldBeTrue();
        registered.ShouldNotBeNull().ShouldBeSameAs(configured);
        registered.FallbackPolicy.ShouldBeSameAs(registered.DefaultPolicy);
        registered.TryGetPolicy("admins", out _).ShouldBeTrue();
        clearFallback.ShouldThrow<InvalidOperationException>();
        addPolicy.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Read access: An application without AddAuthorization should report no options")]
    public void TryGetAuthorizationOptions_WithoutAddAuthorization_ShouldReturnFalse()
    {
        // Arrange
        IWebApplicationContext context = new StubWebApplicationContext([]);

        // Act
        bool found = context.TryGetAuthorizationOptions(out AuthorizationOptions? options);

        // Assert
        found.ShouldBeFalse();
        options.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Read access: The last AddAuthorization should be the one read back, as UseAuthorization reads it")]
    public void TryGetAuthorizationOptions_RegisteredTwice_ShouldReturnTheLastRegistration()
    {
        // Arrange
        ServiceProviderBuilder builder = new();
        builder.AddAuthorization(options => options.AddPolicy("first", policy => policy.RequireRole("first")));
        builder.AddAuthorization(options => options.AddPolicy("second", policy => policy.RequireRole("second")));

        IWebApplicationContext context = new StubWebApplicationContext(Resolve(builder));

        // Act
        context.TryGetAuthorizationOptions(out AuthorizationOptions? options);

        // Assert
        options.ShouldNotBeNull();
        options.TryGetPolicy("second", out _).ShouldBeTrue();
        options.TryGetPolicy("first", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Read access: The hosted application context should expose the registered options")]
    public async Task TryGetAuthorizationOptions_HostedApplication_ShouldReturnTheRegistration()
    {
        // Arrange — the real Web.Hosting context, whose features come from its service provider.
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddAuthorization(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));

        IWebApplicationContext context = ((IWebApplication)factory.Application).Context;

        // Act
        bool found = context.TryGetAuthorizationOptions(out AuthorizationOptions? options);

        // Assert
        found.ShouldBeTrue();
        options.ShouldNotBeNull().TryGetPolicy("admins", out AuthorizationPolicy? admins).ShouldBeTrue();
        admins.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Read access: Reading options from a null context should throw")]
    public void TryGetAuthorizationOptions_NullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        IWebApplicationContext context = null!;

        // Act
        Action act = () => context.TryGetAuthorizationOptions(out _);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    // Resolves the application features the way the host does when it composes the pipeline.
    private static IHttpFeature[] Resolve(IServiceProviderBuilder services)
    {
        IServiceProvider provider = services.Build();
        return provider.GetRequiredService<IEnumerable<IHttpFeature>>().ToArray();
    }
}

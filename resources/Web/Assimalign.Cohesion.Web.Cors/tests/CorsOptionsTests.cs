using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// The options surface: the default policy and named policies are registered once each, and an invalid
/// policy fails while <c>UseCors</c> is being configured.
/// </summary>
public class CorsOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddPolicy: A duplicate policy name should throw")]
    public void AddPolicy_DuplicateName_ShouldThrow()
    {
        // Arrange
        CorsOptions options = new CorsOptions().AddPolicy("spa", policy => policy.WithOrigins("https://app.example"));

        // Act
        Action act = () => options.AddPolicy("spa", policy => policy.AllowAnyOrigin());

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("spa");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddPolicy: Policy names should be case-sensitive")]
    public void AddPolicy_NamesDifferingInCase_ShouldRegisterBoth()
    {
        // Arrange
        CorsOptions options = new();

        // Act
        options
            .AddPolicy("spa", policy => policy.WithOrigins("https://app.example"))
            .AddPolicy("SPA", policy => policy.AllowAnyOrigin());

        // Assert
        options.Policies.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddPolicy: An empty name should throw ArgumentException")]
    public void AddPolicy_EmptyName_ShouldThrow()
    {
        // Arrange
        CorsOptions options = new();

        // Act
        Action act = () => options.AddPolicy(string.Empty, policy => policy.AllowAnyOrigin());

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddPolicy: An invalid policy should throw when it is registered")]
    public void AddPolicy_InvalidPolicy_ShouldThrowAtRegistration()
    {
        // Arrange
        CorsOptions options = new();

        // Act
        Action act = () => options.AddPolicy("open", policy => policy.AllowAnyOrigin().AllowCredentials());

        // Assert
        act.ShouldThrow<InvalidOperationException>();
        options.Policies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddDefaultPolicy: A second default policy should throw")]
    public void AddDefaultPolicy_Twice_ShouldThrow()
    {
        // Arrange
        CorsOptions options = new CorsOptions().AddDefaultPolicy(policy => policy.WithOrigins("https://app.example"));

        // Act
        Action act = () => options.AddDefaultPolicy(new CorsPolicyBuilder().AllowAnyOrigin().Build());

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddDefaultPolicy: A built policy should become the default")]
    public void AddDefaultPolicy_BuiltPolicy_ShouldBecomeDefault()
    {
        // Arrange
        CorsPolicy policy = new CorsPolicyBuilder().AllowAnyOrigin().Build();
        CorsOptions options = new();

        // Act
        options.AddDefaultPolicy(policy);

        // Assert
        options.DefaultPolicy.ShouldBeSameAs(policy);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - AddDefaultPolicy: A null configuration should throw ArgumentNullException")]
    public void AddDefaultPolicy_NullConfigure_ShouldThrow()
    {
        // Arrange
        CorsOptions options = new();

        // Act
        Action act = () => options.AddDefaultPolicy((Action<CorsPolicyBuilder>)null!);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - CorsMetadata: The carrier should require UseCors in every form, Disabled included")]
    public void CorsMetadata_EveryForm_ShouldRequireUseCors()
    {
        // Arrange
        CorsPolicy policy = new CorsPolicyBuilder().AllowAnyOrigin().Build();

        // Act
        CorsMetadata named = new("spa");
        CorsMetadata inline = new(policy);
        CorsMetadata disabled = CorsMetadata.Disabled;

        // Assert
        named.RequiredMiddleware.ShouldBe("UseCors");
        inline.RequiredMiddleware.ShouldBe("UseCors");
        disabled.RequiredMiddleware.ShouldBe("UseCors");
        named.PolicyName.ShouldBe("spa");
        inline.Policy.ShouldBeSameAs(policy);
        disabled.IsDisabled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - CorsMetadata: An empty policy name should throw ArgumentException")]
    public void CorsMetadata_EmptyPolicyName_ShouldThrow()
    {
        // Arrange / Act
        Action act = () => _ = new CorsMetadata(string.Empty);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }
}

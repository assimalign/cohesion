using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// The sealed endpoint-metadata carrier: what each form requires of the pipeline (the fail-closed
/// contract routing checks), its validation, and its immutability.
/// </summary>
public class AuthorizationMetadataTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: A requirement should name UseAuthorization as its required middleware")]
    public void RequiredMiddleware_Requirement_ShouldBeUseAuthorization()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireRole("admin").Build();

        // Act
        IRouteMiddlewareMetadata[] items =
        [
            new AuthorizationMetadata(),
            new AuthorizationMetadata("admins"),
            new AuthorizationMetadata(policy),
            new AuthorizationMetadata { Roles = ["admin"] },
        ];

        // Assert
        foreach (IRouteMiddlewareMetadata item in items)
        {
            item.RequiredMiddleware.ShouldBe("UseAuthorization");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: AllowAnonymous should require no middleware")]
    public void RequiredMiddleware_AllowAnonymous_ShouldBeNull()
    {
        // Act
        AuthorizationMetadata metadata = AuthorizationMetadata.AllowAnonymous;

        // Assert
        metadata.AllowsAnonymous.ShouldBeTrue();
        metadata.RequiredMiddleware.ShouldBeNull();
        metadata.ShouldBeSameAs(AuthorizationMetadata.AllowAnonymous);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: Allow-anonymous and requirement items should share one runtime type")]
    public void AllowAnonymous_RuntimeType_ShouldMatchRequirementType()
    {
        // Arrange — routing's fail-closed check reads the last item of each runtime type, so a route's
        // AllowAnonymous can only supersede its group's requirement when both are the same type.
        AuthorizationMetadata requirement = new();

        // Act
        Type anonymousType = AuthorizationMetadata.AllowAnonymous.GetType();

        // Assert
        anonymousType.ShouldBe(requirement.GetType());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: Each form should expose exactly what it declares")]
    public void Constructors_EachForm_ShouldExposeTheirValues()
    {
        // Arrange
        AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireRole("admin").Build();

        // Act
        AuthorizationMetadata defaultPolicy = new();
        AuthorizationMetadata named = new("admins") { AuthenticationSchemes = ["Bearer"] };
        AuthorizationMetadata inline = new(policy);
        AuthorizationMetadata roles = new() { Roles = ["admin", "ops"] };

        // Assert
        defaultPolicy.PolicyName.ShouldBeNull();
        defaultPolicy.Policy.ShouldBeNull();
        defaultPolicy.Roles.ShouldBeEmpty();
        defaultPolicy.AuthenticationSchemes.ShouldBeEmpty();
        defaultPolicy.AllowsAnonymous.ShouldBeFalse();
        named.PolicyName.ShouldBe("admins");
        named.AuthenticationSchemes.ShouldBe(new[] { "Bearer" });
        inline.Policy.ShouldBeSameAs(policy);
        roles.Roles.ShouldBe(new[] { "admin", "ops" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: Roles should be copied, so the source array cannot change them")]
    public void Roles_SourceMutatedAfterward_ShouldNotChange()
    {
        // Arrange
        string[] source = ["admin"];
        AuthorizationMetadata metadata = new() { Roles = source };

        // Act
        source[0] = "everyone";

        // Assert
        metadata.Roles.ShouldBe(new[] { "admin" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Metadata: Invalid names should be rejected")]
    public void Constructors_InvalidValues_ShouldThrow()
    {
        // Act
        Action emptyName = () => _ = new AuthorizationMetadata(string.Empty);
        Action nullPolicy = () => _ = new AuthorizationMetadata((AuthorizationPolicy)null!);
        Action blankRole = () => _ = new AuthorizationMetadata { Roles = ["admin", " "] };
        Action blankScheme = () => _ = new AuthorizationMetadata { AuthenticationSchemes = [""] };

        // Assert
        emptyName.ShouldThrow<ArgumentException>();
        nullPolicy.ShouldThrow<ArgumentNullException>();
        blankRole.ShouldThrow<ArgumentException>();
        blankScheme.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Conventions: A verb on a null builder should throw ArgumentNullException")]
    public void RequireAuthorization_NullBuilder_ShouldThrowArgumentNullException()
    {
        // Arrange
        IRouterRouteBuilder builder = null!;

        // Act
        Action require = () => builder.RequireAuthorization();
        Action allow = () => builder.AllowAnonymous();

        // Assert
        require.ShouldThrow<ArgumentNullException>();
        allow.ShouldThrow<ArgumentNullException>();
    }
}

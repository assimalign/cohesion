using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The Permissions-Policy builder: RFC 9651 dictionary serialization of each allowlist form, replacement
/// in place, and the feature-name and origin checks.
/// </summary>
public class PermissionsPolicyBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: Should serialize every allowlist form as an RFC 9651 dictionary")]
    public void Create_AllowlistForms_ShouldSerialize()
    {
        // Arrange & Act
        PermissionsPolicy policy = PermissionsPolicy.Create(permissions => permissions
            .Disable("camera")
            .AllowAll("fullscreen")
            .AllowSelf("geolocation", "https://maps.example.com")
            .AllowOrigins("payment", "https://pay.example.com", "https://*.bank.example:8443"));

        // Assert
        policy.ToString().ShouldBe(
            "camera=(), fullscreen=*, geolocation=(self \"https://maps.example.com\"), " +
            "payment=(\"https://pay.example.com\" \"https://*.bank.example:8443\")");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: Setting a feature again should replace its allowlist in place")]
    public void Create_FeatureSetTwice_ShouldReplaceInPlace()
    {
        // Arrange & Act
        PermissionsPolicy policy = PermissionsPolicy.Create(permissions => permissions
            .AllowAll("camera")
            .Disable("microphone")
            .AllowSelf("camera"));

        // Assert
        policy.ToString().ShouldBe("camera=(self), microphone=()");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: A repeated origin should serialize once")]
    public void AllowSelf_RepeatedOrigin_ShouldSerializeOnce()
    {
        // Arrange & Act
        PermissionsPolicy policy = PermissionsPolicy.Create(permissions => permissions
            .AllowSelf("geolocation", "https://maps.example.com", "https://maps.example.com"));

        // Assert
        policy.ToString().ShouldBe("geolocation=(self \"https://maps.example.com\")");
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: A name outside the feature grammar should be rejected")]
    [InlineData("")]
    [InlineData("Camera")]
    [InlineData("1camera")]
    [InlineData("camera=*")]
    [InlineData("camera, microphone")]
    public void Disable_InvalidFeatureName_ShouldThrow(string feature)
    {
        // Arrange & Act
        Action act = () => PermissionsPolicy.Create(permissions => permissions.Disable(feature));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: A value that is not a serialized origin should be rejected")]
    [InlineData("")]
    [InlineData("maps.example.com")]
    [InlineData("https://maps.example.com/")]
    [InlineData("https://maps.example.com/path")]
    [InlineData("https://*")]
    [InlineData("https://maps.example.com:port")]
    [InlineData("https://ma\"ps.example.com")]
    public void AllowOrigins_InvalidOrigin_ShouldThrow(string origin)
    {
        // Arrange & Act
        Action act = () => PermissionsPolicy.Create(permissions => permissions.AllowOrigins("payment", origin));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: An origin-only allowlist should require an origin")]
    public void AllowOrigins_NoOrigin_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => PermissionsPolicy.Create(permissions => permissions.AllowOrigins("payment"));

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: A policy with no feature should be rejected")]
    public void Build_NoFeature_ShouldThrow()
    {
        // Arrange
        PermissionsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Permissions: Null arguments should throw ArgumentNullException")]
    public void Create_NullArguments_ShouldThrow()
    {
        // Arrange & Act
        Action create = () => PermissionsPolicy.Create(null!);
        Action feature = () => PermissionsPolicy.Create(permissions => permissions.Disable(null!));
        Action origins = () => PermissionsPolicy.Create(permissions => permissions.AllowSelf("camera", null!));

        // Assert
        create.ShouldThrow<ArgumentNullException>();
        feature.ShouldThrow<ArgumentNullException>();
        origins.ShouldThrow<ArgumentNullException>();
    }
}

using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The framing policy: the paired <c>frame-ancestors</c> and <c>X-Frame-Options</c> values, and the
/// ancestor-source checks of <see cref="FramingPolicy.AllowFrom"/>.
/// </summary>
public class FramingPolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Framing: Deny and SameOrigin should pair both fields")]
    public void Presets_DenyAndSameOrigin_ShouldPairFrameAncestorsWithFrameOptions()
    {
        // Arrange & Act & Assert
        FramingPolicy.Deny.FrameAncestors.ShouldBe("'none'");
        FramingPolicy.Deny.XFrameOptions.ShouldBe("DENY");
        FramingPolicy.SameOrigin.FrameAncestors.ShouldBe("'self'");
        FramingPolicy.SameOrigin.XFrameOptions.ShouldBe("SAMEORIGIN");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Framing: AllowFrom should list the ancestors and emit no X-Frame-Options")]
    public void AllowFrom_Ancestors_ShouldEmitNoFrameOptions()
    {
        // Arrange & Act
        FramingPolicy policy = FramingPolicy.AllowFrom("'SELF'", "https://partner.example.com", "https://*.example.org:8443", "https:");

        // Assert
        policy.FrameAncestors.ShouldBe("'self' https://partner.example.com https://*.example.org:8443 https:");
        policy.XFrameOptions.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Framing: AllowFrom with only 'self' should be SameOrigin")]
    public void AllowFrom_OnlySelf_ShouldReturnSameOrigin()
    {
        // Arrange & Act
        FramingPolicy policy = FramingPolicy.AllowFrom("'self'", "'self'");

        // Assert
        policy.ShouldBeSameAs(FramingPolicy.SameOrigin);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Framing: A value outside the ancestor-source grammar should be rejected")]
    [InlineData("'none'")]
    [InlineData("'unsafe-inline'")]
    [InlineData("'nonce-abc'")]
    [InlineData("")]
    [InlineData("https://partner.example.com; script-src *")]
    [InlineData("self")]
    public void AllowFrom_InvalidAncestor_ShouldThrow(string source)
    {
        // Arrange & Act
        Action act = () => FramingPolicy.AllowFrom(source);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Framing: AllowFrom should require at least one ancestor")]
    public void AllowFrom_NoAncestor_ShouldThrow()
    {
        // Arrange & Act
        Action empty = () => FramingPolicy.AllowFrom();
        Action nullList = () => FramingPolicy.AllowFrom(null!);

        // Assert
        empty.ShouldThrow<ArgumentException>();
        nullList.ShouldThrow<ArgumentNullException>();
    }
}

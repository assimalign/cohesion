using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Cors.Tests;

public class CorsMethodValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithMethods: methods exceeding the preflight limit should fail at composition")]
    [InlineData(33)]
    [InlineData(128)]
    public void WithMethods_OverlongMethod_ShouldThrowAtComposition(int length)
    {
        // Arrange
        CorsPolicyBuilder builder = new();
        string method = new('A', length);

        // Act
        Action act = () => builder.WithMethods(method);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithMethods: extension methods within the preflight limit should retain their spelling")]
    [InlineData(1)]
    [InlineData(32)]
    public void WithMethods_ValidLength_ShouldPreserveMethod(int length)
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().AllowAnyOrigin();
        string method = new('a', length);

        // Act
        CorsPolicy policy = builder.WithMethods(method).Build();

        // Assert
        policy.Methods.ShouldBe([method]);
    }
}

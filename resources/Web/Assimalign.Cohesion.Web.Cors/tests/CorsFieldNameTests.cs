using System;

using Assimalign.Cohesion.Http;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Cors.Tests;

public class CorsFieldNameTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Cors] - WithHeaders: Default names remain refused by both lists")]
    public void WithHeaders_DefaultName_ShouldThrowForBothLists()
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => builder.WithHeaders(default(HttpFieldName)));
        Should.Throw<ArgumentException>(() => builder.WithExposedHeaders(default(HttpFieldName)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Typed lists retain case-insensitive identity and original spelling")]
    public void Build_HeaderNames_ShouldPreserveSpellingWithOrdinalIgnoreCaseIdentity()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().AllowAnyOrigin()
            .WithHeaders("X-Tenant", "x-tenant").WithExposedHeaders("X-Result", "x-result");

        // Act
        CorsPolicy policy = builder.Build();

        // Assert
        policy.Headers.ShouldHaveSingleItem().ShouldBe(new HttpFieldName("x-tenant"));
        policy.Headers[0].Value.ShouldBe("X-Tenant");
        policy.ExposedHeaders.ShouldHaveSingleItem().ShouldBe(new HttpFieldName("x-result"));
        policy.ExposedHeaders[0].Value.ShouldBe("X-Result");
    }
}

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;
using Shouldly;
using Xunit;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should capture constrained route values")]
    public void TryMatch_OnConstrainedRoute_ShouldCaptureRouteValues()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id:int}");
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        // Act
        bool matched = route.TryMatch(context, out RouteValueDictionary values);

        // Assert
        matched.ShouldBeTrue();
        values["id"].ShouldBe("42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should reject non-matching methods")]
    public void TryMatch_OnMethodMismatch_ShouldReturnFalse()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id:int}");
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/42");

        // Act
        bool matched = route.TryMatch(context, out _);

        // Assert
        matched.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should support optional trailing extensions")]
    public void TryMatch_OnOptionalTrailingExtension_ShouldMatchWithAndWithoutExtension()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/assets/{name}.{ext?}");
        TestHttpContext contextWithExtension = TestHttpContext.Create(HttpMethod.Get, "/assets/archive.tar.gz");
        TestHttpContext contextWithoutExtension = TestHttpContext.Create(HttpMethod.Get, "/assets/archive");

        // Act
        bool matchedWithExtension = route.TryMatch(contextWithExtension, out RouteValueDictionary valuesWithExtension);
        bool matchedWithoutExtension = route.TryMatch(contextWithoutExtension, out RouteValueDictionary valuesWithoutExtension);

        // Assert
        matchedWithExtension.ShouldBeTrue();
        valuesWithExtension["name"].ShouldBe("archive.tar");
        valuesWithExtension["ext"].ShouldBe("gz");

        matchedWithoutExtension.ShouldBeTrue();
        valuesWithoutExtension["name"].ShouldBe("archive");
        valuesWithoutExtension.ContainsKey("ext").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should reject values outside a range constraint")]
    public void TryMatch_OnRangeConstraintViolation_ShouldReturnFalse()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/orders/{id:range(1,10)}");
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/orders/11");

        // Act
        bool matched = route.TryMatch(context, out _);

        // Assert
        matched.ShouldBeFalse();
    }
}

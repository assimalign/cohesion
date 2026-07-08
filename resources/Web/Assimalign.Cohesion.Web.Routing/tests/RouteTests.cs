using System;

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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatchPath: Should match the path while ignoring the method")]
    public void TryMatchPath_OnMethodMismatch_ShouldStillMatchPath()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id:int}");
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/42");

        // Act
        bool pathMatched = route.TryMatchPath(context, out RouteValueDictionary values);
        bool fullMatch = route.TryMatch(context, out _);

        // Assert
        pathMatched.ShouldBeTrue();
        values["id"].ShouldBe("42");
        fullMatch.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should accept any of the mapped methods")]
    public void TryMatch_OnMultiMethodRoute_ShouldAcceptEachMappedMethod()
    {
        // Arrange
        Route route = new(new[] { HttpMethod.Get, HttpMethod.Post }, "/items/{id}");

        // Act
        bool getMatched = route.TryMatch(TestHttpContext.Create(HttpMethod.Get, "/items/1"), out _);
        bool postMatched = route.TryMatch(TestHttpContext.Create(HttpMethod.Post, "/items/1"), out _);
        bool deleteMatched = route.TryMatch(TestHttpContext.Create(HttpMethod.Delete, "/items/1"), out _);

        // Assert
        getMatched.ShouldBeTrue();
        postMatched.ShouldBeTrue();
        deleteMatched.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Methods: Should de-duplicate repeated methods")]
    public void Methods_WithDuplicateMethods_ShouldDeduplicate()
    {
        // Arrange
        Route route = new(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post }, "/items");

        // Act
        int methodCount = route.Methods.Count;

        // Assert
        methodCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - AcceptsMethod: Empty method set should accept any method")]
    public void AcceptsMethod_OnEmptyMethodSet_ShouldAcceptAnyMethod()
    {
        // Arrange
        Route route = new(Array.Empty<HttpMethod>(), "/any/{id}");

        // Act
        bool acceptsGet = route.AcceptsMethod(HttpMethod.Get);
        bool acceptsDelete = route.AcceptsMethod(HttpMethod.Delete);
        bool fullMatch = route.TryMatch(TestHttpContext.Create(HttpMethod.Patch, "/any/9"), out _);

        // Assert
        acceptsGet.ShouldBeTrue();
        acceptsDelete.ShouldBeTrue();
        fullMatch.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should capture a catch-all remainder")]
    public void TryMatch_OnCatchAllRoute_ShouldCaptureRemainder()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/files/{**path}");
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/files/images/logo.png");

        // Act
        bool matched = route.TryMatch(context, out RouteValueDictionary values);

        // Assert
        matched.ShouldBeTrue();
        values["path"].ShouldBe("images/logo.png");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - InboundPrecedence: Literal route outranks parameter route")]
    public void InboundPrecedence_LiteralRoute_ShouldOutrankParameterRoute()
    {
        // Arrange
        Route literalRoute = new(HttpMethod.Get, "/api/status");
        Route parameterRoute = new(HttpMethod.Get, "/api/{id}");

        // Act & Assert — lower inbound precedence is more specific and evaluated first.
        literalRoute.InboundPrecedence.ShouldBeLessThan(parameterRoute.InboundPrecedence);
    }
}

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RoutePathSplittingTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Path matching preserves empty-segment semantics")]
    [InlineData("/", "/")]
    [InlineData("/", "///")]
    [InlineData("/items/status", "/items/status/")]
    [InlineData("/items/status", "//items//status///")]
    public void TryMatchPath_EmptySegments_ShouldKeepLiteralMatch(string pattern, string path)
    {
        // Arrange
        var route = new Route(HttpMethod.Get, pattern);
        var context = TestHttpContext.Create(HttpMethod.Get, path);

        // Act
        bool matched = route.TryMatchPath(context, out var values);

        // Assert
        matched.ShouldBeTrue();
        values.Count.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Catch-all captures only the remaining nonempty segments")]
    [InlineData("/files/one/two", "one/two")]
    [InlineData("/files//one/two/three/", "one/two/three")]
    [InlineData("/files///one///", "one")]
    public void TryMatchPath_CatchAllWithEmptySegments_ShouldCaptureRemainder(string path, string expected)
    {
        // Arrange
        var route = new Route(HttpMethod.Get, "/files/{**path}");
        var context = TestHttpContext.Create(HttpMethod.Get, path);

        // Act
        bool matched = route.TryMatchPath(context, out var values);

        // Assert
        matched.ShouldBeTrue();
        values["path"].ShouldBe(expected);
    }
}

using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RoutePatternLiteralSegmentTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Literal constructor: query separators should fail as they do in the factory")]
    [InlineData("?")]
    [InlineData("orders?page=1")]
    [InlineData("?orders")]
    public void Constructor_QuerySeparator_ShouldThrowArgumentException(string content)
    {
        // Arrange
        ArgumentException expected = Should.Throw<ArgumentException>(() => RoutePatternFactory.LiteralPart(content));

        // Act
        Action act = () => new RoutePatternLiteralSegment(content);

        // Assert
        ArgumentException exception = act.ShouldThrow<ArgumentException>();
        exception.ParamName.ShouldBe("content");
        exception.Message.ShouldBe(expected.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Literal constructor: ordinary literal text should match the factory")]
    public void Constructor_ValidLiteral_ShouldPreserveContent()
    {
        // Arrange
        const string content = "orders";

        // Act
        RoutePatternLiteralSegment segment = new(content);

        // Assert
        segment.Content.ShouldBe(RoutePatternFactory.LiteralPart(content).Content);
    }
}

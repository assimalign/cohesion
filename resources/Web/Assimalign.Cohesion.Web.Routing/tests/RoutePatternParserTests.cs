using Assimalign.Cohesion.Web.Routing.Exceptions;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RoutePatternParserTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should create parameters from constrained and optional segments")]
    public void Parse_WithConstrainedAndOptionalParameters_ShouldCreateExpectedPattern()
    {
        // Arrange
        const string patternText = "/users/{id:int}/assets/{name}.{ext?}";

        // Act
        RoutePattern pattern = RoutePatternParser.Parse(patternText);

        // Assert
        pattern.RawText.ShouldBe(patternText);
        pattern.PathSegments.Count.ShouldBe(4);
        pattern.Parameters.Count.ShouldBe(3);

        RoutePatternParameterSegment? idParameter = pattern.GetParameter("id");
        idParameter.ShouldNotBeNull();
        idParameter.ParameterPolicies.Count.ShouldBe(1);
        idParameter.ParameterPolicies[0].Content.ShouldBe("int");

        RoutePatternParameterSegment? extensionParameter = pattern.GetParameter("ext");
        extensionParameter.ShouldNotBeNull();
        extensionParameter.IsOptional.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should recognize a catch-all parameter")]
    public void Parse_WithCatchAllParameter_ShouldMarkParameterAsCatchAll()
    {
        // Arrange
        const string patternText = "/files/{**path}";

        // Act
        RoutePattern pattern = RoutePatternParser.Parse(patternText);

        // Assert
        RoutePatternParameterSegment? pathParameter = pattern.GetParameter("path");
        pathParameter.ShouldNotBeNull();
        pathParameter.IsCatchAll.ShouldBeTrue();
        pathParameter.EncodeSlashes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should capture a parameter default value")]
    public void Parse_WithParameterDefault_ShouldCaptureDefaultValue()
    {
        // Arrange
        const string patternText = "/blog/{page=1}";

        // Act
        RoutePattern pattern = RoutePatternParser.Parse(patternText);

        // Assert
        RoutePatternParameterSegment? pageParameter = pattern.GetParameter("page");
        pageParameter.ShouldNotBeNull();
        pageParameter.Default.ShouldBe("1");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should capture multiple inline constraints")]
    public void Parse_WithMultipleConstraints_ShouldCaptureEachPolicy()
    {
        // Arrange
        const string patternText = "/orders/{id:int:min(1)}";

        // Act
        RoutePattern pattern = RoutePatternParser.Parse(patternText);

        // Assert
        RoutePatternParameterSegment? idParameter = pattern.GetParameter("id");
        idParameter.ShouldNotBeNull();
        idParameter.ParameterPolicies.Count.ShouldBe(2);
        idParameter.ParameterPolicies[0].Content.ShouldBe("int");
        idParameter.ParameterPolicies[1].Content.ShouldBe("min(1)");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should reject consecutive separators")]
    public void Parse_WithConsecutiveSeparators_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<RoutePatternException>(() => RoutePatternParser.Parse("/api//status"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should reject a catch-all that is not last")]
    public void Parse_WithCatchAllNotLast_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<RoutePatternException>(() => RoutePatternParser.Parse("/files/{**path}/extra"));
    }
}

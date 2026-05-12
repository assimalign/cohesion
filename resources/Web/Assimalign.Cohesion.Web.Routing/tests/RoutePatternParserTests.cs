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
}

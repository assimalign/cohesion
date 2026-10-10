using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Web.Routing.Patterns;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

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

    // One row per class of invalid template (#1051): every error path names the template, the part of
    // it that is wrong, and the fix, where they previously threw an empty message.
    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should explain every class of invalid template")]
    [InlineData("api//status", "contains an empty segment ('//')")]
    [InlineData("api/{", "ends with an unmatched '{'")]
    [InlineData("api{", "ends with an unmatched '{'")]
    [InlineData("orders}", "ends with an unmatched '}'")]
    [InlineData("ord}ers", "The literal text 'ord}' contains an unmatched '}'")]
    [InlineData("orders/{id:regex(^\\d{3}$)}", "The parameter '{id:regex(^\\d{3' contains an unescaped '{'")]
    [InlineData("orders/{id{", "The parameter '{id{' is not closed: the template ends after an unescaped '{'")]
    [InlineData("orders/{id:int", "The parameter '{id:int' is not closed: the template ends before its closing '}'")]
    [InlineData("orders/{}", "contains an empty parameter '{}'")]
    [InlineData("files/{*}", "The parameter '{*}' has no name")]
    [InlineData("orders/{a*b}", "The parameter name 'a*b' contains '*'")]
    [InlineData("orders/{id}/items/{ID}", "The parameter name 'ID' is used more than once")]
    [InlineData("files/{*path?}", "The catch-all parameter '{*path?}' cannot be optional")]
    [InlineData("orders/{id=5?}", "The parameter '{id=5?}' is both optional ('?') and has a default value ('=')")]
    [InlineData("files/{**path}/extra", "The catch-all parameter '{**path}' must be the last segment of the template")]
    [InlineData("files/x{*path}", "The segment 'x{*path}' combines the catch-all parameter '{*path}' with other text")]
    [InlineData("files/{name}{ext?}", "In the segment '{name}{ext?}', the optional parameter 'ext' directly follows '{name}'")]
    [InlineData("files/{name}-{ext?}", "In the segment '{name}-{ext?}', the optional parameter 'ext' follows '-'")]
    [InlineData("files/{name?}.{ext}", "In the segment '{name?}.{ext}', the optional parameter 'name' is followed by '.'")]
    [InlineData("files/{name}{ext}", "places the parameters '{name}' and '{ext}' next to each other")]
    [InlineData("orders?page=1", "The literal text 'orders?page=1' contains '?'")]
    [InlineData("~orders", "starts with '~' but not '~/'")]
    public void Parse_InvalidTemplate_ShouldThrowSpecificMessage(string template, string expected)
    {
        // Act
        RoutePatternException exception = Should.Throw<RoutePatternException>(() => RoutePatternParser.Parse(template));

        // Assert
        exception.Pattern.ShouldBe(template);
        exception.Message.ShouldStartWith($"The route template '{template}' is invalid. ", Case.Sensitive);
        exception.Message.ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should suggest a separator for adjacent parameters")]
    public void Parse_WithAdjacentParameters_ShouldSuggestSeparatedForm()
    {
        // Act
        RoutePatternException exception = Should.Throw<RoutePatternException>(() => RoutePatternParser.Parse("/files/{name}{ext}"));

        // Assert
        exception.Message.ShouldEndWith("Separate them with literal text, for example '{name}-{ext}'.", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Should report a group prefix and child that repeat a parameter name")]
    public void MapGroup_WithRepeatedParameterAcrossPrefixAndChild_ShouldExplain()
    {
        // Arrange
        IRouterGroupBuilder group = new RouterBuilder().MapGroup("tenants/{id}");

        // Act
        RoutePatternException exception = Should.Throw<RoutePatternException>(
            () => group.Map(HttpMethod.Get, "orders/{id:int}", new RouterRouteHandler(_ => Task.CompletedTask)));

        // Assert
        exception.Message.ShouldContain("The parameter name 'id' is used more than once", Case.Sensitive);
    }
}

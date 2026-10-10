using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - HttpQuery: Parse should skip a parameter with an empty name")]
    [InlineData("=1", "")]
    [InlineData("&=", "")]
    [InlineData("=", "")]
    [InlineData("a=1&=2&b=3", "a=1;b=3")]
    [InlineData("=&a=1&&=x&b", "a=1;b=")]
    public void Parse_OnParameterWithEmptyName_ShouldSkipParameter(string raw, string expected)
    {
        // Arrange — a query key is never empty, so a parameter without a name has nowhere to go.
        HttpQuery query = new(raw);

        // Act
        HttpQueryCollection parameters = query.Parse();

        // Assert — the parsed collection skips it; the raw query text still carries it.
        Describe(parameters).ShouldBe(expected);
        query.Value.ShouldBe(raw);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQuery: Parse should decode names and values")]
    public void Parse_OnEncodedParameters_ShouldDecodeNamesAndValues()
    {
        // Arrange
        HttpQuery query = new("?q=a%20b&p%2Fx=%2F");

        // Act
        HttpQueryCollection parameters = query.Parse();

        // Assert
        Describe(parameters).ShouldBe("p/x=/;q=a b");
    }

    /// <summary>The parameters as <c>name=value</c> pairs, ordered by name and joined with <c>;</c>.</summary>
    private static string Describe(HttpQueryCollection parameters)
    {
        return string.Join(
            ";",
            parameters
                .OrderBy(parameter => parameter.Key.Value, StringComparer.Ordinal)
                .Select(parameter => $"{parameter.Key.Value}={parameter.Value.Value}"));
    }
}

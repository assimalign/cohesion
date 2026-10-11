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

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQuery: Create should write nothing for an empty collection")]
    public void Create_OnEmptyCollection_ShouldReturnEmptyQuery()
    {
        // Arrange
        HttpQueryCollection parameters = new();

        // Act
        HttpQuery query = HttpQuery.Create(parameters);

        // Assert
        query.Value.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpQuery: Create should percent-encode every name and value except unreserved characters")]
    [InlineData("q", "a b", "q=a%20b")]
    [InlineData("q", "a&b=c+d", "q=a%26b%3Dc%2Bd")]
    [InlineData("return url", "/x?y#z", "return%20url=%2Fx%3Fy%23z")]
    [InlineData("name", "café", "name=caf%C3%A9")]
    [InlineData("pct", "100%", "pct=100%25")]
    [InlineData("safe-._~", "AZaz09-._~", "safe-._~=AZaz09-._~")]
    [InlineData("flag", "", "flag")]
    public void Create_OnSingleParameter_ShouldPercentEncodeNameAndValue(string name, string value, string expected)
    {
        // Arrange
        HttpQueryCollection parameters = new() { [name] = value };

        // Act
        HttpQuery query = HttpQuery.Create(parameters);

        // Assert
        query.Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpQuery: Create should be parsed back to the same parameters")]
    [InlineData("a=1&b=2")]
    [InlineData("q=a%20b&p%2Fx=%2F")]
    [InlineData("flag&empty=&x=caf%C3%A9")]
    [InlineData("amp=%26&eq=%3D&plus=%2B&pct=%25")]
    public void Create_OnParsedQuery_ShouldRoundTripThroughParse(string raw)
    {
        // Arrange
        HttpQueryCollection parsed = new HttpQuery(raw).Parse();

        // Act
        HttpQueryCollection reparsed = HttpQuery.Create(parsed).Parse();

        // Assert
        Describe(reparsed).ShouldBe(Describe(parsed));
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpQuery: Parse preserves repeated values and reads the first")]
    [InlineData("?a=1&a=2", "1", "2")]
    [InlineData("?a=1&b=other&a=2", "1", "2")]
    [InlineData("?a=1&a=&a=2", "1", "", "2")]
    [InlineData("?a=&b=other&a=2", "", "2")]
    [InlineData("?a=1&A=2&%61=3", "1", "2", "3")]
    [InlineData("?a=x%2Cy&a=z&a", "x,y", "z", "")]
    public void Parse_OnRepeatedKey_ShouldKeepEveryValueAndReadFirst(string raw, params string[] expected)
    {
        // Arrange
        HttpQuery query = new(raw);

        // Act
        IHttpQueryCollection parsed = query.Parse();
        bool found = parsed.TryGetValue("a", out HttpQueryValue values);

        // Assert
        found.ShouldBeTrue();
        values.ToArray().ShouldBe(expected);
        values.Value.ShouldBe(expected[0]);
        values.ToString().ShouldBe(expected[0]);
        ((string)values).ShouldBe(expected[0]);
        parsed["a"].ShouldBe(values);
        parsed.Single(pair => pair.Key == "a").Value.ShouldBe(values);
        parsed.Count.ShouldBe(raw.Contains("b=", StringComparison.Ordinal) ? 2 : 1);
        if (parsed.ContainsKey("b"))
        {
            parsed["b"].Value.ShouldBe("other");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQuery: Create writes every retained occurrence")]
    public void Create_OnRepeatedKey_ShouldWriteEveryValueInOrder()
    {
        // Arrange
        HttpQueryCollection parsed = new();
        parsed.Add("a", "x,y");
        parsed.Add("b", "other");
        parsed.Add("a", "");
        parsed.Add("a", "z&=");

        // Act
        HttpQuery query = HttpQuery.Create(parsed);
        HttpQueryCollection reparsed = query.Parse();

        // Assert
        query.Value.ShouldBe("a=x%2Cy&a&a=z%26%3D&b=other");
        reparsed["a"].ShouldBe(parsed["a"]);
        reparsed["b"].ShouldBe(parsed["b"]);
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

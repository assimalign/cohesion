using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpParameterValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - Media-type parameter construction rejects names its parser discards")]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("bad\tname")]
    [InlineData("bad\r\nname")]
    [InlineData("caf\u00e9")]
    public void HttpMediaTypeParameter_InvalidName_ShouldThrowAtConstruction(string name)
    {
        // Arrange
        HttpMediaType.Parse($"text/plain;{name}=value").Parameters.ShouldBeEmpty();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new HttpMediaTypeParameter(name, "value"));

        // Assert
        exception.ParamName.ShouldBe("name");
        exception.Message.ShouldContain("token");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Forwarded parameter construction rejects names its parser rejects")]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("bad\tname")]
    [InlineData("bad\r\nname")]
    [InlineData("caf\u00e9")]
    public void HttpForwardedParameter_InvalidName_ShouldThrowAtConstruction(string name)
    {
        // Arrange
        HttpForwardedElement.TryParse($"{name}=value", out _).ShouldBeFalse();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new HttpForwardedParameter(name, "value"));

        // Assert
        exception.ParamName.ShouldBe("name");
        exception.Message.ShouldContain("token");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Cache directive construction rejects names its parser rejects")]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("bad\tname")]
    [InlineData("bad\r\nname")]
    [InlineData("caf\u00e9")]
    public void HttpCacheControlExtension_InvalidName_ShouldThrowAtConstruction(string name)
    {
        // Arrange
        HttpCacheControl.TryParse($"{name}=value", out _).ShouldBeFalse();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new HttpCacheControlExtension(name, "value"));

        // Assert
        exception.ParamName.ShouldBe("name");
        exception.Message.ShouldContain("token");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Preference construction rejects tokens all preference parsers discard")]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("bad\tname")]
    [InlineData("bad\r\nname")]
    [InlineData("caf\u00e9")]
    public void HttpQualityValue_InvalidToken_ShouldThrowAtConstruction(string value)
    {
        // Arrange
        HttpAcceptParser.ParseAcceptEncoding(value).ShouldBeEmpty();
        HttpAcceptParser.ParseAcceptCharset(value).ShouldBeEmpty();
        HttpAcceptParser.ParseAcceptLanguage(value).ShouldBeEmpty();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new HttpQualityValue(value, HttpQuality.One));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("token");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Parameter and preference constructors preserve every token character accepted by their parsers")]
    [InlineData("X-Custom")]
    [InlineData("!#$%&'*+-.^_`|~09AZaz")]
    [InlineData("*")]
    public void Constructors_ValidTokens_ShouldAgreeWithParsers(string token)
    {
        // Arrange
        HttpMediaTypeParameter mediaParameter = new(token, "value");
        HttpForwardedParameter forwardedParameter = new(token, "value");
        HttpCacheControlExtension directive = new(token, "value");
        HttpQualityValue preference = new(token, HttpQuality.One);

        // Act / Assert
        HttpMediaType.Parse($"text/plain;{token}=value").Parameters[0].ShouldBe(mediaParameter);
        HttpForwardedElement.Parse($"{token}=value").Parameters[0].ShouldBe(forwardedParameter);
        HttpCacheControl.Parse($"{token}=value").Extensions[0].ShouldBe(directive);
        HttpAcceptParser.ParseAcceptEncoding(token)[0].ShouldBe(preference);
        HttpAcceptParser.ParseAcceptCharset(token)[0].ShouldBe(preference);
        HttpAcceptParser.ParseAcceptLanguage(token)[0].ShouldBe(preference);
    }
}

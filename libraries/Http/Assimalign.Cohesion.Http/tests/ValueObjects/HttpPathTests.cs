using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpPathTests
{
    [Fact]
    public void Constructor_EmptyValue_ShouldNormalizeToRoot()
    {
        // Arrange
        const string value = "";

        // Act
        HttpPath path = new(value);

        // Assert
        path.Value.ShouldBe("/");
        path.ShouldBe(HttpPath.Root);
    }

    [Fact]
    public void Concat_RootAndChildPath_ShouldCombineWithoutDuplicateSeparator()
    {
        // Arrange
        HttpPath root = HttpPath.Root;
        HttpPath child = new("/api");

        // Act
        HttpPath combined = root.Concat(child);

        // Assert
        combined.Value.ShouldBe("/api");
    }

    [Fact]
    public void FromUriComponent_EncodedPath_ShouldDecodePercentEncodedSegments()
    {
        // Arrange
        const string component = "/hello%24world";

        // Act
        HttpPath decoded = HttpPath.FromUriComponent(component);

        // Assert
        decoded.Value.ShouldBe("/hello$world");
    }

    [Fact]
    public void Constructor_InvalidCharacter_ShouldThrowHttpException()
    {
        // Arrange
        const string value = "/hello world";

        // Act
        Action action = () => _ = new HttpPath(value);

        // Assert
        action.ShouldThrow<HttpException>().Code.ShouldBe(HttpErrorCode.InvalidPath);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should percent-encode each segment and keep pchar and separators")]
    [InlineData("/", "/")]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData("/x:y@z!$&'()*+,;=-._~", "/x:y@z!$&'()*+,;=-._~")]
    [InlineData("/café", "/caf%C3%A9")]
    [InlineData("/日本/\U0001F600", "/%E6%97%A5%E6%9C%AC/%F0%9F%98%80")]
    [InlineData("/100%", "/100%25")]
    [InlineData("/a%25", "/a%2525")]
    [InlineData("/a\\b", "/a%5Cb")]
    [InlineData("/\"<>`{|}^", "/%22%3C%3E%60%7B%7C%7D%5E")]
    [InlineData("/a\u0001\u007F", "/a%01%7F")]
    public void ToUriComponent_OnDecodedPath_ShouldPercentEncodeEachSegment(string value, string expected)
    {
        // Arrange
        HttpPath path = new(value);

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should encode a lone surrogate as U+FFFD")]
    public void ToUriComponent_OnLoneSurrogate_ShouldEncodeReplacementCharacter()
    {
        // Arrange — built in code: a lone surrogate does not survive test-case serialization.
        HttpPath path = new(string.Concat("/lone", ((char)0xD800).ToString(), "x"));

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBe("/lone%EF%BF%BDx");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should keep an encoded slash as written instead of encoding its '%' again")]
    [InlineData("/a%2Fb", "/a%2Fb")]
    [InlineData("/a%2fb", "/a%2fb")]
    [InlineData("/%2F%2F", "/%2F%2F")]
    [InlineData("/a%%2F", "/a%25%2F")]
    [InlineData("/a%2", "/a%252")]
    public void ToUriComponent_OnEncodedSlash_ShouldKeepItAsWritten(string value, string expected)
    {
        // Arrange — FromUriComponent never decodes %2F, so a decoded path carries it encoded.
        HttpPath path = new(value);

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should be read back by FromUriComponent as the same path")]
    [InlineData("/caf%C3%A9")]
    [InlineData("/caf%c3%a9/x")]
    [InlineData("/100%25")]
    [InlineData("/a%2Fb")]
    [InlineData("/a%252Fb")]
    [InlineData("/a%5Cb")]
    [InlineData("/%ZZ/%")]
    [InlineData("/%C3")]
    [InlineData("/%E6%97%A5/%F0%9F%98%80")]
    [InlineData("/%2e%2e/x")]
    public void ToUriComponent_OnTransportDecodedPath_ShouldRoundTripThroughFromUriComponent(string requestTarget)
    {
        // Arrange — the path a transport surfaces for this request-target.
        HttpPath decoded = HttpPath.FromUriComponent(requestTarget);

        // Act
        HttpPath reread = HttpPath.FromUriComponent(decoded.ToUriComponent());

        // Assert
        reread.ShouldBe(decoded);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should return the value itself when nothing needs encoding")]
    public void ToUriComponent_OnPathWithoutEscapes_ShouldReturnValueInstance()
    {
        // Arrange
        HttpPath path = new("/assets/app.js");

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBeSameAs(path.Value);
    }
}

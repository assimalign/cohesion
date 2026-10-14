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

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should keep an escape the decoder leaves as written instead of encoding its '%' again")]
    [InlineData("/caf%E9", "/caf%E9")]                 // a Latin-1 byte, not UTF-8
    [InlineData("/caf%e9", "/caf%e9")]                 // lower-case hex is kept as written
    [InlineData("/%C3", "/%C3")]                       // a lead byte at the end
    [InlineData("/a%A9b", "/a%A9b")]                   // a lone continuation byte
    [InlineData("/over%C0%AE", "/over%C0%AE")]         // an overlong sequence
    [InlineData("/%ED%A0%80", "/%ED%A0%80")]           // an encoded surrogate
    [InlineData("/%E2%82x", "/%E2%82x")]               // a sequence cut short by a literal
    [InlineData("/%C3é", "/%C3%C3%A9")]                // a lead byte before an encoded character
    public void ToUriComponent_OnEscapeTheDecoderLeaves_ShouldKeepItAsWritten(string value, string expected)
    {
        // Arrange — FromUriComponent leaves each of these escapes in the decoded path, so one there is the
        // escape the request carried. Encoding its '%' would write "%25E9", a different URI.
        HttpPath path = new(value);

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should escape a '%' that would decode, since it arrived as %25")]
    [InlineData("/%41", "/%2541")]                     // an ASCII escape
    [InlineData("/%C3%A9", "/%25C3%A9")]               // a valid UTF-8 sequence; the lone %A9 left then stays
    [InlineData("/%00", "/%2500")]                     // NUL, which the decoder refuses rather than leaves
    [InlineData("/%2541", "/%252541")]                 // an escaped '%'
    public void ToUriComponent_OnEscapeTheDecoderWouldDecode_ShouldEscapeItsPercent(string value, string expected)
    {
        // Arrange — a decoded path holds these only when the request spelled the '%' as %25.
        HttpPath path = new(value);

        // Act
        string component = path.ToUriComponent();

        // Assert
        component.ShouldBe(expected);
        HttpPath.FromUriComponent(component).ShouldBe(path);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should write a canonical request-target back unchanged")]
    [InlineData("/caf%C3%A9")]
    [InlineData("/100%25")]
    [InlineData("/a%2Fb")]
    [InlineData("/bad%25zz")]
    [InlineData("/caf%E9")]
    [InlineData("/%C3")]
    [InlineData("/a%A9b")]
    [InlineData("/over%C0%AE")]
    [InlineData("/%ED%A0%80")]
    [InlineData("/%E6%97%A5/%F0%9F%98%80")]
    public void ToUriComponent_OnCanonicalRequestTarget_ShouldReturnIt(string requestTarget)
    {
        // Arrange — the path a transport surfaces for this request-target.
        HttpPath decoded = HttpPath.FromUriComponent(requestTarget);

        // Act
        string component = decoded.ToUriComponent();

        // Assert
        component.ShouldBe(requestTarget);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: ToUriComponent should be read back as the same path where the decoded form is ambiguous")]
    [InlineData("/caf%25E9", "/caf%E9")]
    [InlineData("/%25C3%25A9", "/%25C3%A9")]
    [InlineData("/%C3%25A9", "/%25C3%A9")]
    [InlineData("/%25C3%A9", "/%25C3%A9")]
    [InlineData("/%E2%82%25AC", "/%25E2%82%AC")]
    public void ToUriComponent_OnAmbiguousDecodedPath_ShouldRoundTripThroughFromUriComponent(string requestTarget, string expected)
    {
        // Arrange — "%25E9" and "%E9" decode to the same text, which is written back in the form a client
        // sends; either way FromUriComponent reads the same path.
        HttpPath decoded = HttpPath.FromUriComponent(requestTarget);

        // Act
        string component = decoded.ToUriComponent();

        // Assert
        component.ShouldBe(expected);
        HttpPath.FromUriComponent(component).ShouldBe(decoded);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: A path cannot hold a space, '?' or '#', so ToUriComponent never encodes one")]
    [InlineData("/a b")]
    [InlineData("/a?b")]
    [InlineData("/a#b")]
    public void Constructor_SpaceQuestionMarkOrNumberSign_ShouldThrowInvalidPath(string value)
    {
        // Arrange & Act — #1324 asked for redirect tests with a space and '?' in a path segment; none can
        // reach a redirect, because no HttpPath holds one.
        Action action = () => _ = new HttpPath(value);

        // Assert
        action.ShouldThrow<HttpException>().Code.ShouldBe(HttpErrorCode.InvalidPath);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpPath: FromUriComponent should refuse a path that decodes to a space, '?' or '#'")]
    [InlineData("/a%20b")]
    [InlineData("/a%3Fb")]
    [InlineData("/a%23b")]
    public void FromUriComponent_DecodingToSpaceQuestionMarkOrNumberSign_ShouldThrowInvalidPath(string component)
    {
        // Arrange & Act — the decode every transport runs; each fails the request instead of serving it.
        Action action = () => HttpPath.FromUriComponent(component);

        // Assert
        action.ShouldThrow<HttpException>().Code.ShouldBe(HttpErrorCode.InvalidPath);
    }
}

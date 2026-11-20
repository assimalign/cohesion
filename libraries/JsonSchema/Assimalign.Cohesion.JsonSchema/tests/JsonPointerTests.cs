using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class JsonPointerTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonPointer: ";

    // RFC 6901 §5.
    private const string RfcDocument = """
        {
          "foo": ["bar", "baz"],
          "": 0,
          "a/b": 1,
          "c%d": 2,
          "e^f": 3,
          "g|h": 4,
          "i\\j": 5,
          "k\"l": 6,
          " ": 7,
          "m~n": 8
        }
        """;

    public static TheoryData<string, string> RfcExamples => new()
    {
        { "/foo", """["bar", "baz"]""" },
        { "/foo/0", "\"bar\"" },
        { "/", "0" },
        { "/a~1b", "1" },
        { "/c%d", "2" },
        { "/e^f", "3" },
        { "/g|h", "4" },
        { "/i\\j", "5" },
        { "/k\"l", "6" },
        { "/ ", "7" },
        { "/m~0n", "8" },
    };

    // RFC 6901 §6.
    public static TheoryData<string, string> RfcFragments => new()
    {
        { "", "#" },
        { "/foo", "#/foo" },
        { "/foo/0", "#/foo/0" },
        { "/", "#/" },
        { "/a~1b", "#/a~1b" },
        { "/c%d", "#/c%25d" },
        { "/e^f", "#/e%5Ef" },
        { "/g|h", "#/g%7Ch" },
        { "/i\\j", "#/i%5Cj" },
        { "/k\"l", "#/k%22l" },
        { "/ ", "#/%20" },
        { "/m~0n", "#/m~0n" },
    };

    [Theory(DisplayName = DisplayPrefix + "Constructor: Should accept every RFC 6901 pointer")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/a")]
    [InlineData("/a~0b~1c")]
    [InlineData("/0/12")]
    public void Constructor_ValidPointer_ShouldKeepValue(string value)
    {
        // Act
        JsonPointer pointer = new(value);

        // Assert
        pointer.Value.ShouldBe(value);
        pointer.ToString().ShouldBe(value);
    }

    [Theory(DisplayName = DisplayPrefix + "Constructor: Should refuse a pointer without a leading slash or with a bad escape")]
    [InlineData("a")]
    [InlineData("~")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/a~")]
    [InlineData("#/a")]
    public void Constructor_InvalidPointer_ShouldThrowArgumentException(string value)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new JsonPointer(value));

        // Assert
        exception.ParamName.ShouldBe("value");
        JsonPointer.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "TryCreate: Should refuse null")]
    public void TryCreate_Null_ShouldReturnFalse()
    {
        // Act
        bool created = JsonPointer.TryCreate(null, out JsonPointer pointer);

        // Assert
        created.ShouldBeFalse();
        pointer.Value.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "GetTokens: Should unescape ~1 and ~0 in that order")]
    public void GetTokens_EscapedTokens_ShouldUnescape()
    {
        // Arrange
        JsonPointer pointer = new("/a~1b/c~0d/~01//");

        // Act
        string[] tokens = pointer.GetTokens();

        // Assert
        tokens.ShouldBe(["a/b", "c~d", "~1", "", ""]);
    }

    [Fact(DisplayName = DisplayPrefix + "GetTokens: Should return no tokens for the root")]
    public void GetTokens_Root_ShouldReturnEmpty()
    {
        // Act
        string[] tokens = JsonPointer.Root.GetTokens();

        // Assert
        tokens.ShouldBeEmpty();
        JsonPointer.Root.IsRoot.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Append: Should escape member names and write indexes in decimal")]
    public void Append_TokensAndIndexes_ShouldBuildEscapedPointer()
    {
        // Act
        JsonPointer pointer = JsonPointer.Root.Append("a/b").Append("~").Append(0).Append(12);

        // Assert
        pointer.Value.ShouldBe("/a~1b/~0/0/12");
        pointer.GetTokens().ShouldBe(["a/b", "~", "0", "12"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Append: Should refuse a negative index and a null name")]
    public void Append_InvalidArguments_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => JsonPointer.Root.Append(-1));
        Should.Throw<ArgumentNullException>(() => JsonPointer.Root.Append((string)null!));
    }

    [Fact(DisplayName = DisplayPrefix + "FromTokens: Should round-trip through GetTokens")]
    public void FromTokens_Tokens_ShouldRoundTrip()
    {
        // Arrange
        string[] tokens = ["a/b", "~", "", "~1"];

        // Act
        JsonPointer pointer = JsonPointer.FromTokens(tokens);

        // Assert
        pointer.Value.ShouldBe("/a~1b/~0//~01");
        pointer.GetTokens().ShouldBe(tokens);
        JsonPointer.FromTokens([]).ShouldBe(JsonPointer.Root);
        Should.Throw<ArgumentNullException>(() => JsonPointer.FromTokens(["a", null!]));
    }

    [Fact(DisplayName = DisplayPrefix + "TryEvaluate: Should resolve the empty pointer to the whole document")]
    public void TryEvaluate_Root_ShouldReturnDocument()
    {
        // Arrange
        JsonElement document = TestJson.Parse(RfcDocument);

        // Act
        bool found = JsonPointer.Root.TryEvaluate(document, out JsonElement value);

        // Assert
        found.ShouldBeTrue();
        value.GetRawText().ShouldBe(document.GetRawText());
    }

    [Theory(DisplayName = DisplayPrefix + "TryEvaluate: Should resolve the RFC 6901 examples")]
    [MemberData(nameof(RfcExamples))]
    public void TryEvaluate_RfcExample_ShouldResolveValue(string pointer, string expected)
    {
        // Arrange
        JsonElement document = TestJson.Parse(RfcDocument);

        // Act
        bool found = new JsonPointer(pointer).TryEvaluate(document, out JsonElement value);

        // Assert
        found.ShouldBeTrue();
        value.GetRawText().ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "TryEvaluate: Should not resolve missing members, out-of-range or non-canonical indexes, or the - token")]
    [InlineData("/foo/2")]
    [InlineData("/foo/-")]
    [InlineData("/foo/01")]
    [InlineData("/foo/+1")]
    [InlineData("/foo/1e0")]
    [InlineData("/foo/0/x")]
    [InlineData("/missing")]
    [InlineData("/foo/99999999999")]
    public void TryEvaluate_UnresolvablePointer_ShouldReturnFalse(string pointer)
    {
        // Arrange
        JsonElement document = TestJson.Parse(RfcDocument);

        // Act
        bool found = new JsonPointer(pointer).TryEvaluate(document, out JsonElement value);

        // Assert
        found.ShouldBeFalse();
        value.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Theory(DisplayName = DisplayPrefix + "ToUriFragment: Should percent-encode the RFC 6901 examples")]
    [MemberData(nameof(RfcFragments))]
    public void ToUriFragment_RfcExample_ShouldEncode(string pointer, string expected)
    {
        // Act
        string fragment = new JsonPointer(pointer).ToUriFragment();

        // Assert
        fragment.ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "TryParseUriFragment: Should decode the RFC 6901 examples")]
    [MemberData(nameof(RfcFragments))]
    public void TryParseUriFragment_RfcExample_ShouldDecode(string expected, string fragment)
    {
        // Act
        bool parsed = JsonPointer.TryParseUriFragment(fragment, out JsonPointer pointer);

        // Assert
        parsed.ShouldBeTrue();
        pointer.Value.ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "ToUriFragment: Should encode non-ASCII characters as UTF-8")]
    public void ToUriFragment_NonAscii_ShouldPercentEncodeUtf8()
    {
        // Arrange
        JsonPointer pointer = JsonPointer.Root.Append("é").Append("😀");

        // Act
        string fragment = pointer.ToUriFragment();

        // Assert
        fragment.ShouldBe("#/%C3%A9/%F0%9F%98%80");
        JsonPointer.TryParseUriFragment(fragment, out JsonPointer parsed).ShouldBeTrue();
        parsed.ShouldBe(pointer);
    }

    [Theory(DisplayName = DisplayPrefix + "TryParseUriFragment: Should refuse bad escapes, invalid UTF-8 and invalid pointers")]
    [InlineData(null)]
    [InlineData("#a")]
    [InlineData("#/%7")]
    [InlineData("#/%zz")]
    [InlineData("#/%E0%A4")]
    [InlineData("#/~2")]
    public void TryParseUriFragment_Invalid_ShouldReturnFalse(string? fragment)
    {
        // Act
        bool parsed = JsonPointer.TryParseUriFragment(fragment, out JsonPointer pointer);

        // Assert
        parsed.ShouldBeFalse();
        pointer.Value.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Equality: Should compare ordinally, with default distinct from the root")]
    public void Equality_Pointers_ShouldCompareOrdinally()
    {
        // Arrange
        JsonPointer upper = new("/A");
        JsonPointer lower = new("/a");

        // Act & Assert
        (upper == new JsonPointer("/A")).ShouldBeTrue();
        (upper != lower).ShouldBeTrue();
        (upper < lower).ShouldBeTrue();
        default(JsonPointer).ShouldNotBe(JsonPointer.Root);
        default(JsonPointer).IsRoot.ShouldBeFalse();
        upper.GetHashCode().ShouldBe(new JsonPointer("/A").GetHashCode());
    }

    [Fact(DisplayName = DisplayPrefix + "Conversions: Should convert to and from strings")]
    public void Conversions_String_ShouldRoundTrip()
    {
        // Act
        JsonPointer pointer = "/a/0";
        string text = pointer;

        // Assert
        text.ShouldBe("/a/0");
        Should.Throw<ArgumentException>(() => _ = (JsonPointer)"a");
    }

    [Fact(DisplayName = DisplayPrefix + "Default: Should refuse every member that needs a pointer")]
    public void Default_Members_ShouldThrowInvalidOperation()
    {
        // Arrange
        JsonPointer pointer = default;
        JsonElement document = TestJson.Parse("{}");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => pointer.GetTokens());
        Should.Throw<InvalidOperationException>(() => pointer.Append("a"));
        Should.Throw<InvalidOperationException>(() => pointer.Append(0));
        Should.Throw<InvalidOperationException>(() => pointer.ToUriFragment());
        Should.Throw<InvalidOperationException>(() => pointer.TryEvaluate(document, out _));
        pointer.ToString().ShouldBeEmpty();
    }
}

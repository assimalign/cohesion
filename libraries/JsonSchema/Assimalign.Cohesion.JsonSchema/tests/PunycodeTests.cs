using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class PunycodeTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Punycode: ";

    // RFC 3492 §7.1, samples A, B, L and S.
    public static TheoryData<string, string> RfcSamples => new()
    {
        { "ليهمابتكلموشعربي؟", "egbpdaj6bu4bxfgehfvwxn" },
        { "他们为什么不说中文", "ihqwcrb4cv8a8dqg056pqjye" },
        { "3年B組金八先生", "3B-ww4c5e180e575a65lsy2b" },
        { "-> $1.00 <-", "-> $1.00 <--" },
    };

    [Theory(DisplayName = DisplayPrefix + "Encode: Should produce the RFC 3492 samples")]
    [MemberData(nameof(RfcSamples))]
    public void Encode_RfcSample_ShouldMatch(string unicode, string expected)
    {
        // Arrange
        int[] codePoints = ToCodePoints(unicode);
        Span<char> output = stackalloc char[64];

        // Act
        int written = Punycode.Encode(codePoints, output);

        // Assert
        written.ShouldBeGreaterThan(0);
        output.Slice(0, written).ToString().ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "Decode: Should read the RFC 3492 samples, in either case")]
    [MemberData(nameof(RfcSamples))]
    public void Decode_RfcSample_ShouldMatch(string expected, string punycode)
    {
        // Arrange
        Span<int> output = stackalloc int[64];

        // Act
        int count = Punycode.Decode(punycode, output);
        int upperCount = Punycode.Decode(punycode.ToUpperInvariant(), output.Slice(32));

        // Assert
        count.ShouldBe(ToCodePoints(expected).Length);
        output.Slice(0, count).ToArray().ShouldBe(ToCodePoints(expected));
        upperCount.ShouldBe(count);
    }

    [Theory(DisplayName = DisplayPrefix + "Decode: Should refuse invalid digits, non-basic characters and overflow")]
    [InlineData("a-+")]
    [InlineData("abé")]
    [InlineData("99999999999999999")]
    [InlineData("-")]
    public void Decode_Invalid_ShouldReturnMinusOne(string punycode)
    {
        // Arrange
        Span<int> output = stackalloc int[64];

        // Act
        int count = Punycode.Decode(punycode, output);

        // Assert
        count.ShouldBeLessThan(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Encode and Decode: Should report output that does not fit")]
    public void EncodeDecode_SmallBuffer_ShouldReturnMinusOne()
    {
        // Arrange
        int[] codePoints = ToCodePoints("他们为什么不说中文");
        Span<char> text = stackalloc char[4];
        Span<int> decoded = stackalloc int[4];

        // Act
        int written = Punycode.Encode(codePoints, text);
        int count = Punycode.Decode("ihqwcrb4cv8a8dqg056pqjye", decoded);

        // Assert
        written.ShouldBe(-1);
        count.ShouldBe(-1);
    }

    private static int[] ToCodePoints(string text) => text.EnumerateRunes().Select(rune => rune.Value).ToArray();
}

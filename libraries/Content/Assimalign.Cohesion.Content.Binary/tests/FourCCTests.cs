using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Binary.Tests;

public class FourCCTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Binary] - FourCC: printable Latin-1 identifiers round-trip")]
    [InlineData("moov", 0x6D6F6F76U)]
    [InlineData("©nam", 0xA96E616DU)]
    [InlineData("ÿabc", 0xFF616263U)]
    public void Parse_Latin1_PreservesBigEndianValue(string text, uint expected)
    {
        // Arrange
        Span<byte> bytes = stackalloc byte[4];

        // Act
        var value = FourCC.Parse(text);
        value.WriteTo(bytes);

        // Assert
        value.Value.ShouldBe(expected);
        value.ToString().ShouldBe(text);
        FourCC.FromBytes(bytes).ShouldBe(value);
        FourCC.FromUInt32(expected).ShouldBe(value);
        FourCC.TryParse(text, out var parsed).ShouldBeTrue();
        (value == parsed).ShouldBeTrue();
        (value != FourCC.FromUInt32(expected + 1)).ShouldBeTrue();
        value.Equals((object)parsed).ShouldBeTrue();
        value.GetHashCode().ShouldBe(parsed.GetHashCode());
        value.Equals((object)expected).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FourCC: every Latin-1 byte preserves its bits")]
    public void FromBytes_AllByteValues_RoundTripsAndEscapesControls()
    {
        // Arrange: generate every possible repeated-byte code locally.
        Span<byte> bytes = stackalloc byte[4];
        Span<byte> destination = stackalloc byte[5];
        for (var raw = 0; raw < 256; raw++)
        {
            bytes.Fill((byte)raw);
            destination.Fill(0xEE);

            // Act
            var value = FourCC.FromBytes(bytes);
            value.WriteTo(destination);
            var parsed = FourCC.Parse(new string((char)raw, 4));

            // Assert
            value.ShouldBe(parsed);
            destination[..4].SequenceEqual(bytes).ShouldBeTrue();
            destination[4].ShouldBe((byte)0xEE);
            if (raw < 32 || (raw >= 127 && raw < 160))
            {
                var escape = "\\x" + raw.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
                value.ToString().ShouldBe(escape + escape + escape + escape);
            }
            else
            {
                value.ToString().ShouldBe(new string((char)raw, 4));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - FourCC: invalid strings never truncate or substitute characters")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("😀ab")]
    [InlineData("abcĀ")]
    public void Parse_InvalidCharacters_Rejects(string value)
    {
        // Arrange / Act / Assert
        FourCC.TryParse(value, out var result).ShouldBeFalse();
        result.ShouldBe(default);
        Should.Throw<FormatException>(() => FourCC.Parse(value));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FourCC: null and invalid byte lengths are rejected")]
    public void FromBytes_InvalidLength_ThrowsArgumentError()
    {
        // Arrange / Act / Assert
        FourCC.TryParse(null, out _).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => FourCC.Parse(null!));
        Should.Throw<ArgumentException>(() => FourCC.FromBytes(new byte[3]));
        Should.Throw<ArgumentException>(() => FourCC.FromBytes(new byte[5]));
        Should.Throw<ArgumentException>(() => FourCC.Parse("moov").WriteTo(new byte[3]));
    }
}

using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Symbol: Should reject non-ASCII text at construction")]
    [InlineData("\u0080")]
    [InlineData("caf\u00e9")]
    [InlineData("\u4e2d")]
    [InlineData("\ud83d\ude00")]
    public void Constructor_OnNonAsciiSymbol_ShouldThrow(string value)
    {
        // Arrange
        Action construct = () => _ = new AmqpSymbol(value);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(construct);

        // Assert
        exception.ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Symbol: Should support default as a hash collection key")]
    public void GetHashCode_OnDefaultSymbol_ShouldSupportHashCollections()
    {
        // Arrange
        AmqpSymbol symbol = default;
        AmqpSymbol equalSymbol = default;
        Dictionary<AmqpSymbol, string> values = new();

        // Act
        values[symbol] = "default";

        // Assert
        symbol.Equals(equalSymbol).ShouldBeTrue();
        (symbol == equalSymbol).ShouldBeTrue();
        symbol.GetHashCode().ShouldBe(equalSymbol.GetHashCode());
        values[equalSymbol].ShouldBe("default");
        values.ContainsKey(new AmqpSymbol(string.Empty)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Symbol: Should preserve every ASCII character and case-sensitive identity")]
    public void Constructor_OnAsciiSymbol_ShouldPreserveExactIdentity()
    {
        // Arrange
        char[] characters = new char[128];
        for (int i = 0; i < characters.Length; i++)
        {
            characters[i] = (char) i;
        }
        string value = new(characters);

        // Act
        AmqpSymbol symbol = new(value);

        // Assert
        symbol.Value.ShouldBe(value);
        new AmqpSymbol(string.Empty).Value.ShouldBe(string.Empty);
        new AmqpSymbol("Name").ShouldNotBe(new AmqpSymbol("name"));
        ((string) symbol).ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - FrameCodec: Should reject a sub-512 maximum before encoding")]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(511u)]
    public void Encode_OnMaximumFrameSizeBelowMinimum_ShouldThrow(uint maximum)
    {
        // Arrange
        AmqpFrame frame = new(0, new AmqpClosePerformative());

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => AmqpFrameCodec.Encode(frame, maxFrameSize: maximum));

        // Assert
        exception.ParamName.ShouldBe("maxFrameSize");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Open: Should reject a sub-512 maximum during composition")]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(511u)]
    public void MaxFrameSize_OnOpenBelowMinimum_ShouldThrowWithoutChangingValue(uint maximum)
    {
        // Arrange
        AmqpOpenPerformative open = new() { MaxFrameSize = 512u };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => open.MaxFrameSize = maximum);

        // Assert
        exception.ParamName.ShouldBe("value");
        open.MaxFrameSize.ShouldBe(512u);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Open: Should round-trip valid maximum frame sizes")]
    [InlineData(null)]
    [InlineData(512u)]
    [InlineData(uint.MaxValue)]
    public void Encode_OnValidMaximumFrameSize_ShouldRoundTrip(uint? maximum)
    {
        // Arrange
        AmqpFrame frame = new(0, new AmqpOpenPerformative { ContainerId = "test", MaxFrameSize = maximum });

        // Act
        byte[] bytes = AmqpFrameCodec.Encode(frame, maxFrameSize: 512u);
        AmqpFrame decoded = AmqpFrameCodec.Decode(bytes);

        // Assert
        decoded.Performative.ShouldBeOfType<AmqpOpenPerformative>().MaxFrameSize.ShouldBe(maximum);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - FrameCodec: Should reject an invalid peer maximum frame size")]
    public void Decode_OnOpenMaximumFrameSizeBelowMinimum_ShouldThrowProtocolException()
    {
        // Arrange: Open(container-id = "", hostname = null, max-frame-size = 511).
        byte[] bytes =
        {
            0x00, 0x00, 0x00, 0x16, 0x02, 0x00, 0x00, 0x00,
            0x00, 0x53, 0x10, 0xc0, 0x09, 0x03,
            0xa1, 0x00, 0x40, 0x70, 0x00, 0x00, 0x01, 0xff
        };

        // Act
        Action decode = () => AmqpFrameCodec.Decode(bytes);

        // Assert
        Should.Throw<AmqpProtocolException>(decode);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - MessageCodec: Should reject non-ASCII symbol encodings")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Decode_OnNonAsciiSymbolWireValue_ShouldThrowProtocolException(bool wide, bool array)
    {
        // Arrange: An amqp-value section containing a symbol or a one-symbol array.
        // The symbol text is U+00E9 encoded as UTF-8, which is forbidden for symbols.
        List<byte> bytes = new() { 0x00, 0x53, 0x77 };
        if (array)
        {
            bytes.AddRange(wide
                ? new byte[] { 0xf0, 0x00, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x00, 0x01 }
                : new byte[] { 0xe0, 0x05, 0x01 });
        }
        bytes.AddRange(wide
            ? new byte[] { 0xb3, 0x00, 0x00, 0x00, 0x02, 0xc3, 0xa9 }
            : new byte[] { 0xa3, 0x02, 0xc3, 0xa9 });

        // Act
        Action decode = () => AmqpMessageCodec.Decode(bytes.ToArray());

        // Assert
        Should.Throw<AmqpProtocolException>(decode);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - MessageCodec: Should emit ASCII bytes under the symbol format code")]
    public void Encode_OnAsciiSymbol_ShouldUseSymbolWireEncoding()
    {
        // Arrange
        AmqpMessage message = new() { Value = new AmqpSymbol("AZ\0\u007f") };

        // Act
        byte[] bytes = AmqpMessageCodec.Encode(message);

        // Assert
        bytes.ShouldBe(new byte[] { 0x00, 0x53, 0x77, 0xa3, 0x04, 0x41, 0x5a, 0x00, 0x7f });
        AmqpMessageCodec.Decode(bytes).Value.ShouldBe(new AmqpSymbol("AZ\0\u007f"));
    }
}

using System;

using Shouldly;
using Xunit;

namespace Assimalign.IO.Ebml.Tests;

public class EbmlVIntTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - EncodeSize: auto-width encoding picks the shortest form")]
    [InlineData(0, 1, 0x80ul)]
    [InlineData(1, 1, 0x81ul)]
    [InlineData(126, 1, 0xfeul)]
    [InlineData(127, 2, 0x407ful)]
    [InlineData(128, 2, 0x4080ul)]
    [InlineData(0xdeffad, 4, 0x10deffadul)]
    public void EncodeSize_WithoutLength_EncodesToShortestForm(int value, int expectedLength, ulong expectedEncoded)
    {
        // Act
        var actual = VInt.EncodeSize((ulong)value);

        // Assert
        actual.Length.ShouldBe(expectedLength);
        actual.EncodedValue.ShouldBe(expectedEncoded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - EncodeSize: explicit length pads to the requested width")]
    [InlineData(0, 1, 0x80ul)]
    [InlineData(0, 2, 0x4000ul)]
    [InlineData(0, 3, 0x200000ul)]
    [InlineData(0, 4, 0x10000000ul)]
    [InlineData(127, 2, 0x407ful)]
    public void EncodeSize_WithLength_PadsToRequestedWidth(int value, int length, ulong expectedEncoded)
    {
        // Act
        var actual = VInt.EncodeSize((ulong)value, length);

        // Assert
        actual.Length.ShouldBe(length);
        actual.EncodedValue.ShouldBe(expectedEncoded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - EncodeSize: width too narrow for the value throws")]
    [InlineData(127, 1)]
    public void EncodeSize_WithInsufficientLength_ShouldThrow(int value, int length)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => VInt.EncodeSize((ulong)value, length));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - UnknownSize: all data bits set marks the value reserved")]
    [InlineData(1, 0xfful)]
    [InlineData(2, 0x7ffful)]
    [InlineData(3, 0x3ffffful)]
    [InlineData(4, 0x1ffffffful)]
    [InlineData(5, 0x0ffffffffful)]
    [InlineData(6, 0x07fffffffffful)]
    [InlineData(7, 0x03fffffffffffful)]
    [InlineData(8, 0x01fffffffffffffful)]
    public void UnknownSize_ForEachWidth_IsReserved(int length, ulong expectedEncoded)
    {
        // Act
        var actual = VInt.UnknownSize(length);

        // Assert
        actual.Length.ShouldBe(length);
        actual.IsReserved.ShouldBeTrue();
        actual.EncodedValue.ShouldBe(expectedEncoded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - UnknownSize: width outside 1-8 throws")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]
    public void UnknownSize_WithWidthOutOfRange_ShouldThrow(int length)
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => VInt.UnknownSize(length));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - FromEncoded: strips the width marker to recover the value")]
    [InlineData(0x80ul, 0ul)]
    [InlineData(0xaful, 0x2ful)]
    [InlineData(0x40FFul, 0xFFul)]
    [InlineData(0x2000FFul, 0xFFul)]
    [InlineData(0x100000FFul, 0xFFul)]
    [InlineData(0x1f1020FFul, 0xF1020FFul)]
    public void FromEncoded_WithValidEncoding_RecoversValue(ulong encodedValue, ulong expectedValue)
    {
        // Act
        var actual = VInt.FromEncoded(encodedValue);

        // Assert
        actual.Value.ShouldBe(expectedValue);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - FromEncoded: width marker not matching its position throws")]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(0x40ul)]
    [InlineData(0x20ul)]
    [InlineData(0x10ul)]
    [InlineData(0x8000ul)]
    public void FromEncoded_WithMisplacedWidthMarker_ShouldThrow(ulong encodedValue)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => VInt.FromEncoded(encodedValue));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - EncodeSize: round-trips the value at the expected width")]
    [InlineData(0ul, 1)]
    [InlineData(126ul, 1)]
    [InlineData(127ul, 2)]
    [InlineData(128ul, 2)]
    [InlineData(0xFFFFul, 3)]
    [InlineData(0xFFffFFul, 4)]
    public void EncodeSize_ForSizeOrId_RoundTripsValue(ulong value, int expectedLength)
    {
        // Act
        var actual = VInt.EncodeSize(value);

        // Assert
        actual.IsReserved.ShouldBeFalse();
        actual.Value.ShouldBe(value);
        actual.Length.ShouldBe(expectedLength);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - IsValidIdentifier: only shortest-form, non-reserved encodings qualify")]
    [InlineData(0x80ul, true)]
    [InlineData(0x81ul, true)]
    [InlineData(0x4001ul, false)] // Allows shorter form
    [InlineData(0xfful, false)]   // Reserved value
    [InlineData(0x7ffful, false)] // Reserved value
    public void IsValidIdentifier_ForEncodedValue_ReflectsShortestFormAndReservation(ulong encodedValue, bool expected)
    {
        // Act
        var actual = VInt.FromEncoded(encodedValue);

        // Assert
        actual.IsValidIdentifier.ShouldBe(expected);
    }
}

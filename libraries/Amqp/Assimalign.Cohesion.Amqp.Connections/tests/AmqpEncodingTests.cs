using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpEncodingTests
{
    public static TheoryData<object?, string> CompactEncodings()
    {
        return new TheoryData<object?, string>
        {
            { null, "40" },
            { true, "41" },
            { false, "42" },
            { (byte)7, "5007" },
            { (sbyte)-2, "51fe" },
            { (ushort)0x1234, "601234" },
            { (short)-2, "61fffe" },
            { 0u, "43" },
            { 255u, "52ff" },
            { 256u, "7000000100" },
            { 0ul, "44" },
            { 255ul, "53ff" },
            { 256ul, "800000000000000100" },
            { 0, "5400" },
            { -128, "5480" },
            { 127, "547f" },
            { 128, "7100000080" },
            { -129, "71ffffff7f" },
            { 0L, "5500" },
            { 128L, "810000000000000080" },
            { 1.5f, "723fc00000" },
            { 1.5d, "823ff8000000000000" },
            { new AmqpDecimal32(0x22500000u), "7422500000" },
            { new AmqpDecimal64(0x2238000000000000ul), "842238000000000000" },
            { new AmqpDecimal128(new UInt128(0x2208000000000000ul, 1ul)), "9422080000000000000000000000000001" },
            { new Rune('A'), "7300000041" },
            { new Rune(0x1F600), "730001f600" },
            { 'é', "73000000e9" },
            { DateTimeOffset.FromUnixTimeMilliseconds(1), "830000000000000001" },
            { new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc), "830000000000000001" },
            { new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Unspecified), "830000000000000001" },
            { new Guid("00112233-4455-6677-8899-aabbccddeeff"), "9800112233445566778899aabbccddeeff" },
            { "a", "a10161" },
            { string.Empty, "a100" },
            { new AmqpSymbol("ab"), "a3026162" },
            { new byte[] { 1, 2 }, "a0020102" },
            { new ReadOnlyMemory<byte>([1, 2]), "a0020102" },
            { new Memory<byte>([1, 2]), "a0020102" },
            { new ArraySegment<byte>([0, 1, 2], 1, 2), "a0020102" },
            { AmqpDeliveryTag.Create(new byte[] { 9 }), "a00109" },
            { new List<object?>(), "45" },
            { new List<object?> { 1, "a" }, "c006025401a10161" },
            { new Dictionary<string, object?> { ["a"] = 1 }, "c10602a101615401" },
            { new Dictionary<AmqpSymbol, object?> { ["a"] = null }, "c10502a3016140" },
            { new AmqpSymbol[] { "a", "b" }, "e00602a301610162" },
            { new string[] { "a" }, "e00401a10161" },
            { new int[] { 1, 2 }, "e00a02710000000100000002" },
            { Array.Empty<int>(), "e0020071" },
            { new bool[] { true, false }, "e00402560100" },
            { new AmqpDescribedValue(0x1234ul, "a"), "00800000000000001234a10161" },
            { new AmqpDescribedValue(new AmqpSymbol("x:y"), null), "00a303783a7940" },
            { AmqpAccepted.Instance, "00532445" },
            { new AmqpError(AmqpErrorCondition.NotFound), "00531dc01101a30e616d71703a6e6f742d666f756e64" }
        };
    }

    public static TheoryData<object?, object?> PrimitiveRoundTrips()
    {
        return new TheoryData<object?, object?>
        {
            { null, null },
            { true, true },
            { false, false },
            { (byte)200, (byte)200 },
            { (sbyte)-100, (sbyte)-100 },
            { ushort.MaxValue, ushort.MaxValue },
            { short.MinValue, short.MinValue },
            { uint.MaxValue, uint.MaxValue },
            { int.MinValue, int.MinValue },
            { ulong.MaxValue, ulong.MaxValue },
            { long.MinValue, long.MinValue },
            { float.MaxValue, float.MaxValue },
            { double.Epsilon, double.Epsilon },
            { new AmqpDecimal32(0x30800001u), new AmqpDecimal32(0x30800001u) },
            { new AmqpDecimal64(0x31c0000000000001ul), new AmqpDecimal64(0x31c0000000000001ul) },
            { new AmqpDecimal128(UInt128.MaxValue), new AmqpDecimal128(UInt128.MaxValue) },
            { new Rune(0x10FFFF), new Rune(0x10FFFF) },
            { 'z', new Rune('z') },
            { DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123), DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123) },
            { new DateTime(2024, 5, 6, 7, 8, 9, 10, DateTimeKind.Utc), new DateTimeOffset(2024, 5, 6, 7, 8, 9, 10, TimeSpan.Zero) },
            { new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), new Guid("0f8fad5b-d9cb-469f-a165-70867728950e") },
            { "héllo wörld ✓", "héllo wörld ✓" },
            { new AmqpSymbol("amqp:accepted:list"), new AmqpSymbol("amqp:accepted:list") }
        };
    }

    public static TheoryData<Array> TypedArrays()
    {
        return new TheoryData<Array>
        {
            new AmqpSymbol[] { "a", "b" },
            new string[] { "x", new string('y', 300) },
            new int[] { int.MinValue, 0, int.MaxValue },
            Array.Empty<int>(),
            new uint[] { 0u, uint.MaxValue },
            new long[] { long.MinValue, long.MaxValue },
            new ulong[] { 0ul, ulong.MaxValue },
            new short[] { short.MinValue, short.MaxValue },
            new ushort[] { 0, ushort.MaxValue },
            new sbyte[] { sbyte.MinValue, sbyte.MaxValue },
            new bool[] { true, false, true },
            new float[] { -1.25f, float.MaxValue },
            new double[] { -1.25d, double.MaxValue },
            new Guid[] { new Guid("00112233-4455-6677-8899-aabbccddeeff"), Guid.Empty },
            new DateTimeOffset[] { DateTimeOffset.FromUnixTimeMilliseconds(0), DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000) },
            new AmqpDecimal32[] { new(0u), new(uint.MaxValue) },
            new AmqpDecimal64[] { new(0ul), new(ulong.MaxValue) },
            new AmqpDecimal128[] { new(UInt128.Zero), new(UInt128.MaxValue) },
            new Rune[] { new(0), new(0xFFFD), new(0x10FFFF) },
            new byte[][] { [], [1, 2, 3] },
            new byte[][] { [1], new byte[300] }
        };
    }

    public static TheoryData<Array> RefusedArrays()
    {
        return new TheoryData<Array>
        {
            new string?[] { "a", null },
            new AmqpSymbol[] { "a", default },
            new byte[]?[] { [1], null },
            new char[] { 'a', '\ud800' },
            new string[] { "a", new(['\ud800']) }
        };
    }

    public static TheoryData<string, string> MalformedEncodings()
    {
        return new TheoryData<string, string>
        {
            { string.Empty, ErrorMessages.ValueTruncated },
            { "71000000", ErrorMessages.ValueTruncated },
            { "a10561", ErrorMessages.ValueTruncated },
            { "b17fffffff61", ErrorMessages.ValueTruncated },
            { "c0020540", ErrorMessages.CompoundCountInvalid },
            { "d0000000020000", ErrorMessages.CompoundSizeInvalid },
            { "c1020140", ErrorMessages.MapElementCountOdd },
            { "c103024040", ErrorMessages.MapKeyNull },
            { "c10704540140540140", ErrorMessages.MapKeyDuplicate },
            { "5602", ErrorMessages.BooleanInvalid },
            { "730000d800", ErrorMessages.CharInvalid },
            { "7300110000", ErrorMessages.CharInvalid },
            { "837fffffffffffffff", ErrorMessages.TimestampOutOfRange },
            { "a30180", ErrorMessages.SymbolNotAscii },
            { "e003024000", ErrorMessages.ArrayElementWidthInvalid },
            { "004040", ErrorMessages.DescriptorInvalid }
        };
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should choose the most compact encoding the type system allows")]
    [MemberData(nameof(CompactEncodings))]
    public void WriteValue_WithValue_ShouldWriteCompactEncoding(object? value, string expected)
    {
        // Act
        byte[] encoded = Encode(value);

        // Assert
        Convert.ToHexStringLower(encoded).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode each primitive to the type it was written from")]
    [MemberData(nameof(PrimitiveRoundTrips))]
    public void ReadValue_WithEncodedPrimitive_ShouldRoundTrip(object? value, object? expected)
    {
        // Act
        object? decoded = Decode(Encode(value));

        // Assert
        decoded.ShouldBe(expected);
        decoded?.GetType().ShouldBe(expected!.GetType());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode every binary representation as a byte array")]
    public void ReadValue_WithBinaryRepresentations_ShouldDecodeByteArrays()
    {
        // Arrange
        byte[] bytes = [1, 2, 3];
        object[] representations = [bytes, new ReadOnlyMemory<byte>(bytes), new Memory<byte>(bytes), new ArraySegment<byte>(bytes), AmqpDeliveryTag.Create(bytes)];

        foreach (object representation in representations)
        {
            // Act
            object? decoded = Decode(Encode(representation));

            // Assert
            decoded.ShouldBeOfType<byte[]>().ShouldBe(bytes);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode an array to the array type it was written from")]
    [MemberData(nameof(TypedArrays))]
    public void ReadValue_WithEncodedArray_ShouldRoundTripTypedArray(Array value)
    {
        // Act
        object? decoded = Decode(Encode(value));

        // Assert
        Array array = decoded.ShouldBeAssignableTo<Array>()!;
        array.GetType().ShouldBe(value.GetType());
        array.Cast<object>().ShouldBe(value.Cast<object>());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should widen every string of an array when one needs a 32-bit length")]
    public void WriteValue_WithLongStringInArray_ShouldWidenEveryElement()
    {
        // Arrange
        string[] values = ["a", new string('b', 256)];

        // Act
        byte[] encoded = Encode(values);

        // Assert
        encoded[0].ShouldBe(AmqpFormatCode.Array32);
        encoded[9].ShouldBe(AmqpFormatCode.String32);
        Convert.ToHexStringLower(encoded.AsSpan(10, 5)).ShouldBe("0000000161");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should widen every binary of an array when one needs a 32-bit length")]
    public void WriteValue_WithLongBinaryInArray_ShouldWidenEveryElement()
    {
        // Arrange
        byte[][] values = [[7], new byte[256]];

        // Act
        byte[] encoded = Encode(values);

        // Assert
        encoded[0].ShouldBe(AmqpFormatCode.Array32);
        encoded[9].ShouldBe(AmqpFormatCode.Binary32);
        Convert.ToHexStringLower(encoded.AsSpan(10, 5)).ShouldBe("0000000107");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should refuse an array element that has no place in an array before writing any of it")]
    [MemberData(nameof(RefusedArrays))]
    public void WriteValue_WithRefusedArrayElement_ShouldWriteNothing(Array value)
    {
        // Arrange
        using AmqpBufferWriter writer = new();

        AmqpEncoder.WriteValue(writer, "before");

        int length = writer.Length;

        // Act
        Should.Throw<ArgumentException>(() => AmqpEncoder.WriteValue(writer, value));

        // Assert
        writer.Length.ShouldBe(length);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should refuse half of a surrogate pair in a string before writing any of it")]
    public void WriteValue_WithUnpairedSurrogateInString_ShouldWriteNothing()
    {
        // Arrange
        using AmqpBufferWriter writer = new();
        string value = new(['a', 'b', '\udfff']);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpEncoder.WriteValue(writer, value));

        // Assert
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.StringSurrogateUnpaired, 2), Case.Sensitive);
        writer.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should write a surrogate pair in a string as one four-octet character")]
    public void WriteValue_WithSurrogatePairInString_ShouldWriteOneCharacter()
    {
        // Arrange
        string value = new(['\ud83d', '\ude00']);

        // Act
        byte[] encoded = Encode(value);

        // Assert
        Convert.ToHexStringLower(encoded).ShouldBe("a104f09f9880");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode nested lists and maps")]
    public void ReadValue_WithNestedCompounds_ShouldRoundTrip()
    {
        // Arrange
        List<object?> value =
        [
            1,
            "two",
            new List<object?> { 3u },
            new Dictionary<string, object?> { ["k"] = null }
        ];

        // Act
        List<object?> decoded = Decode(Encode(value)).ShouldBeOfType<List<object?>>();

        // Assert
        decoded.Count.ShouldBe(4);
        decoded[0].ShouldBe(1);
        decoded[1].ShouldBe("two");
        decoded[2].ShouldBeOfType<List<object?>>().ShouldHaveSingleItem().ShouldBe(3u);
        Dictionary<object, object?> map = decoded[3].ShouldBeOfType<Dictionary<object, object?>>();
        map.ShouldContainKey("k");
        map["k"].ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should look up a binary map key by its content")]
    public void ReadValue_WithBinaryMapKey_ShouldCompareKeysByContent()
    {
        // Arrange
        Dictionary<object, object?> value = new()
        {
            [new byte[] { 1, 2 }] = "binary",
            [7] = "int",
            [new AmqpSymbol("s")] = "symbol"
        };

        // Act
        Dictionary<object, object?> decoded = Decode(Encode(value)).ShouldBeOfType<Dictionary<object, object?>>();

        // Assert
        decoded[new byte[] { 1, 2 }].ShouldBe("binary");
        decoded[7].ShouldBe("int");
        decoded[new AmqpSymbol("s")].ShouldBe("symbol");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should write non-generic dictionaries and lists")]
    public void WriteValue_WithNonGenericCollections_ShouldWriteMapAndList()
    {
        // Arrange
        Hashtable table = new() { ["key"] = 5L };
        ArrayList list = new() { "item", 6 };

        // Act
        Dictionary<object, object?> decodedMap = Decode(Encode(table)).ShouldBeOfType<Dictionary<object, object?>>();
        List<object?> decodedList = Decode(Encode(list)).ShouldBeOfType<List<object?>>();

        // Assert
        decodedMap["key"].ShouldBe(5L);
        decodedList.ShouldBe(new object?[] { "item", 6 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should switch a map to its 32-bit form when it outgrows the 8-bit one")]
    public void WriteValue_WithLargeMap_ShouldUseMap32()
    {
        // Arrange
        Dictionary<string, object?> value = new();

        for (int i = 0; i < 200; i++)
        {
            value["key" + i.ToString(CultureInfo.InvariantCulture)] = i;
        }

        // Act
        byte[] encoded = Encode(value);
        Dictionary<object, object?> decoded = Decode(encoded).ShouldBeOfType<Dictionary<object, object?>>();

        // Assert
        encoded[0].ShouldBe(AmqpFormatCode.Map32);
        decoded.Count.ShouldBe(200);
        decoded["key199"].ShouldBe(199);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should keep a compound value in its 8-bit form while its size fits one octet")]
    [InlineData(252, AmqpFormatCode.List8)]
    [InlineData(253, AmqpFormatCode.List32)]
    public void WriteValue_AtList8Boundary_ShouldChooseWidth(int binaryLength, byte expectedConstructor)
    {
        // Arrange
        List<object?> value = [new byte[binaryLength]];

        // Act
        byte[] encoded = Encode(value);
        List<object?> decoded = Decode(encoded).ShouldBeOfType<List<object?>>();

        // Assert
        encoded[0].ShouldBe(expectedConstructor);
        decoded.ShouldHaveSingleItem().ShouldBeOfType<byte[]>().Length.ShouldBe(binaryLength);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should give a variable-width value a 32-bit length only past 255 octets")]
    [InlineData(255, "a1ff")]
    [InlineData(256, "b100000100")]
    public void WriteValue_AtString8Boundary_ShouldChooseWidth(int length, string expectedPrefix)
    {
        // Arrange
        string value = new('x', length);

        // Act
        byte[] encoded = Encode(value);

        // Assert
        Convert.ToHexStringLower(encoded.AsSpan(0, expectedPrefix.Length / 2)).ShouldBe(expectedPrefix);
        Decode(encoded).ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should write symbols and binary past 255 octets in their 32-bit forms")]
    public void WriteValue_WithLongSymbolAndBinary_ShouldUse32BitForms()
    {
        // Arrange
        AmqpSymbol symbol = new(new string('s', 256));
        byte[] binary = new byte[256];

        // Act
        byte[] encodedSymbol = Encode(symbol);
        byte[] encodedBinary = Encode(binary);

        // Assert
        encodedSymbol[0].ShouldBe(AmqpFormatCode.Symbol32);
        encodedBinary[0].ShouldBe(AmqpFormatCode.Binary32);
        Decode(encodedSymbol).ShouldBe(symbol);
        Decode(encodedBinary).ShouldBeOfType<byte[]>().Length.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode a described value no model claims, keeping its descriptor")]
    public void ReadValue_WithUnmodeledDescriptor_ShouldReturnDescribedValue()
    {
        // Arrange
        AmqpDescribedValue numeric = new(0x1234ul, "a");
        AmqpDescribedValue symbolic = new("vendor:thing", new List<object?> { 1 });

        // Act
        AmqpDescribedValue decodedNumeric = Decode(Encode(numeric)).ShouldBeOfType<AmqpDescribedValue>();
        AmqpDescribedValue decodedSymbolic = Decode(Encode(symbolic)).ShouldBeOfType<AmqpDescribedValue>();

        // Assert
        decodedNumeric.Descriptor.ShouldBe(0x1234ul);
        decodedNumeric.Value.ShouldBe("a");
        decodedSymbolic.Descriptor.ShouldBe(new AmqpSymbol("vendor:thing"));
        decodedSymbolic.Value.ShouldBeOfType<List<object?>>().ShouldHaveSingleItem().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode a described value whose descriptor names a modeled type as that type")]
    public void ReadValue_WithModeledDescriptor_ShouldReturnModeledType()
    {
        // Arrange
        AmqpDescribedValue numeric = new(0x24ul, new List<object?>());
        AmqpDescribedValue symbolic = new("amqp:released:list", new List<object?>());

        // Act
        object? decodedNumeric = Decode(Encode(numeric));
        object? decodedSymbolic = Decode(Encode(symbolic));

        // Assert
        decodedNumeric.ShouldBeOfType<AmqpAccepted>();
        decodedSymbolic.ShouldBeOfType<AmqpReleased>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should round-trip errors, outcomes and termini as their own types")]
    public void ReadValue_WithModeledValues_ShouldRoundTrip()
    {
        // Arrange
        AmqpError error = new(AmqpErrorCondition.NotFound, "gone", new Dictionary<AmqpSymbol, object?> { ["x"] = 1 });
        AmqpRejected rejected = new(error);
        AmqpModified modified = new(deliveryFailed: true, undeliverableHere: true, new Dictionary<AmqpSymbol, object?> { ["x-opt-a"] = "b" });
        AmqpTransactionalState transactional = new(new byte[] { 1, 2 }, AmqpAccepted.Instance);
        AmqpSource source = new() { Address = "queue/a", DistributionMode = AmqpDistributionMode.Move };
        AmqpTarget target = new() { Address = "queue/b", Durable = AmqpTerminusDurability.UnsettledState };

        // Act
        AmqpError decodedError = Decode(Encode(error)).ShouldBeOfType<AmqpError>();
        AmqpRejected decodedRejected = Decode(Encode(rejected)).ShouldBeOfType<AmqpRejected>();
        AmqpModified decodedModified = Decode(Encode(modified)).ShouldBeOfType<AmqpModified>();
        AmqpTransactionalState decodedTransactional = Decode(Encode(transactional)).ShouldBeOfType<AmqpTransactionalState>();
        AmqpSource decodedSource = Decode(Encode(source)).ShouldBeOfType<AmqpSource>();
        AmqpTarget decodedTarget = Decode(Encode(target)).ShouldBeOfType<AmqpTarget>();

        // Assert
        decodedError.Condition.ShouldBe(AmqpErrorCondition.NotFound);
        decodedError.Description.ShouldBe("gone");
        decodedError.Info.ShouldNotBeNull()[new AmqpSymbol("x")].ShouldBe(1);
        decodedRejected.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotFound);
        decodedModified.DeliveryFailed.ShouldBeTrue();
        decodedModified.UndeliverableHere.ShouldBeTrue();
        decodedModified.MessageAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-a")].ShouldBe("b");
        decodedTransactional.TransactionId.ToArray().ShouldBe(new byte[] { 1, 2 });
        decodedTransactional.Outcome.ShouldBeOfType<AmqpAccepted>();
        decodedSource.Address.ShouldBe("queue/a");
        decodedSource.DistributionMode.ShouldBe(AmqpDistributionMode.Move);
        decodedTarget.Address.ShouldBe("queue/b");
        decodedTarget.Durable.ShouldBe(AmqpTerminusDurability.UnsettledState);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should wrap each element of a described array in its descriptor")]
    public void ReadValue_WithDescribedArray_ShouldDescribeEachElement()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("e0080200a30178520102");

        // Act
        object?[] decoded = Decode(encoded).ShouldBeOfType<object?[]>();

        // Assert
        decoded.Length.ShouldBe(2);
        AmqpDescribedValue first = decoded[0].ShouldBeOfType<AmqpDescribedValue>();
        first.Descriptor.ShouldBe(new AmqpSymbol("x"));
        first.Value.ShouldBe(1u);
        decoded[1].ShouldBeOfType<AmqpDescribedValue>().Value.ShouldBe(2u);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should fail malformed input with a decode error")]
    [MemberData(nameof(MalformedEncodings))]
    public void ReadValue_WithMalformedInput_ShouldThrowDecodeError(string hex, string expectedMessage)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode(encoded));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldBe(expectedMessage);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should name an unsupported format code")]
    public void ReadValue_WithUnsupportedFormatCode_ShouldThrowDecodeError()
    {
        // Arrange
        byte[] encoded = [0x01];

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FormatCodeUnsupported, (byte)0x01));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should cap the nesting of compound values")]
    [InlineData(AmqpEncoder.MaxNestingDepth, false)]
    [InlineData(AmqpEncoder.MaxNestingDepth + 1, true)]
    public void ReadValue_WithNestedLists_ShouldCapNesting(int wrappers, bool shouldFail)
    {
        // Arrange
        byte[] encoded = [AmqpFormatCode.List0];

        for (int i = 0; i < wrappers; i++)
        {
            encoded = [AmqpFormatCode.List8, (byte)(encoded.Length + 1), 1, .. encoded];
        }

        // Act and Assert
        if (shouldFail)
        {
            Should.Throw<AmqpException>(() => Decode(encoded)).Message.ShouldBe(ErrorMessages.ValueNestingTooDeep);
        }
        else
        {
            Decode(encoded).ShouldBeOfType<List<object?>>();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should cap a chain of described values like nested compounds")]
    public void ReadValue_WithDescribedChain_ShouldCapNesting()
    {
        // Arrange
        List<byte> encoded = [];

        for (int i = 0; i <= AmqpEncoder.MaxNestingDepth; i++)
        {
            encoded.AddRange([AmqpFormatCode.Described, AmqpFormatCode.SmallULong, 0x01]);
        }

        encoded.Add(AmqpFormatCode.Null);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode(encoded.ToArray()));

        // Assert
        exception.Message.ShouldBe(ErrorMessages.ValueNestingTooDeep);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should refuse a value with no AMQP representation")]
    public void WriteValue_WithUnsupportedType_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException timeSpan = Should.Throw<ArgumentException>(() => Encode(TimeSpan.Zero));
        ArgumentException enumeration = Should.Throw<ArgumentException>(() => Encode(DayOfWeek.Monday));
        ArgumentException nested = Should.Throw<ArgumentException>(() => Encode(new List<object?> { new object() }));

        // Assert
        timeSpan.Message.ShouldContain("System.TimeSpan", Case.Sensitive);
        enumeration.Message.ShouldContain("System.DayOfWeek", Case.Sensitive);
        nested.Message.ShouldContain("System.Object", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should cap nesting so the reader can decode whatever the writer writes")]
    public void WriteValue_WithDeepNesting_ShouldCapNesting()
    {
        // Arrange
        List<object?> accepted = [];

        for (int i = 1; i < AmqpEncoder.MaxNestingDepth; i++)
        {
            accepted = [accepted];
        }

        List<object?> refused = [accepted];

        // Act
        byte[] encoded = Encode(accepted);
        ArgumentException exception = Should.Throw<ArgumentException>(() => Encode(refused));

        // Assert
        Decode(encoded).ShouldBeOfType<List<object?>>();
        exception.Message.ShouldStartWith(ErrorMessages.ValueNestingTooDeep);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should refuse a list that contains itself instead of recursing without bound")]
    public void WriteValue_WithSelfReferencingList_ShouldThrowArgumentException()
    {
        // Arrange
        List<object?> list = [];
        list.Add(list);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => Encode(list));

        // Assert
        exception.Message.ShouldStartWith(ErrorMessages.ValueNestingTooDeep);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadSymbolMap: Should skip the numeric keys annotations reserve")]
    public void ReadSymbolMap_WithNumericKey_ShouldSkipIt()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("c10904530540a301615401");

        // Act
        Dictionary<AmqpSymbol, object?>? map = ReadSymbolMap(encoded);

        // Assert
        map.ShouldNotBeNull().Count.ShouldBe(1);
        map[new AmqpSymbol("a")].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadSymbolMap: Should refuse a string key where a symbol belongs")]
    public void ReadSymbolMap_WithStringKey_ShouldThrowDecodeError()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("c10602a101615401");

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => ReadSymbolMap(encoded));

        // Assert
        exception.Message.ShouldBe(ErrorMessages.MapKeyNotSymbol);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadStringMap: Should refuse a symbol key where a string belongs")]
    public void ReadStringMap_WithSymbolKey_ShouldThrowDecodeError()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("c10602a301615401");

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => ReadStringMap(encoded));

        // Assert
        exception.Message.ShouldBe(ErrorMessages.MapKeyNotString);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadSymbolMultiple: Should accept null, one symbol or an array of symbols")]
    [InlineData("40", null)]
    [InlineData("a30161", "a")]
    [InlineData("e00602a301610162", "a,b")]
    public void ReadSymbolMultiple_WithAcceptedForms_ShouldDecode(string hex, string? expected)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpSymbol[]? symbols = ReadSymbolMultiple(encoded);

        // Assert
        if (expected is null)
        {
            symbols.ShouldBeNull();
        }
        else
        {
            symbols.ShouldNotBeNull().Select(symbol => symbol.Value).ShouldBe(expected.Split(','));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadSymbolMultiple: Should refuse anything but symbols")]
    [InlineData("a10161")]
    [InlineData("e00401a10161")]
    public void ReadSymbolMultiple_WithOtherTypes_ShouldThrowDecodeError(string hex)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => ReadSymbolMultiple(encoded));

        // Assert
        exception.Message.ShouldBe(ErrorMessages.ValueNotSymbolArray);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReadAddress: Should accept a symbol some peers send in place of a string")]
    public void ReadAddress_WithSymbol_ShouldReturnText()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("a3027131");

        // Act
        string? address = ReadAddress(encoded);

        // Assert
        address.ShouldBe("q1");
    }

    private static byte[] Encode(object? value)
    {
        using AmqpBufferWriter writer = new();

        AmqpEncoder.WriteValue(writer, value);
        return writer.ToArray();
    }

    private static object? Decode(byte[] encoded)
    {
        AmqpReader reader = new(encoded);
        object? value = reader.ReadValue();

        reader.IsEnd.ShouldBeTrue();
        return value;
    }

    private static Dictionary<AmqpSymbol, object?>? ReadSymbolMap(byte[] encoded)
    {
        AmqpReader reader = new(encoded);

        return reader.ReadSymbolMap();
    }

    private static Dictionary<string, object?>? ReadStringMap(byte[] encoded)
    {
        AmqpReader reader = new(encoded);

        return reader.ReadStringMap();
    }

    private static AmqpSymbol[]? ReadSymbolMultiple(byte[] encoded)
    {
        AmqpReader reader = new(encoded);

        return reader.ReadSymbolMultiple();
    }

    private static string? ReadAddress(byte[] encoded)
    {
        AmqpReader reader = new(encoded);

        return reader.ReadAddress();
    }
}

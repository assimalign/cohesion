using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Protocol.Tests;

public sealed class GraphValueCodecTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: Decimal values retain all bits including presented scale")]
    public void Encode_Decimals_ShouldPreserveBits()
    {
        // Arrange
        decimal[] values = [0.3m, 3m, 1.20m, 0.00m, new decimal(0, 0, 0, true, 4), decimal.MinValue, decimal.MaxValue];
        // Act / Assert
        foreach (decimal value in values)
        {
            decimal decoded = GraphValueCodec.Decode(GraphValueCodec.Encode(value)).ShouldBeOfType<decimal>();
            decimal.GetBits(decoded).ShouldBe(decimal.GetBits(value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: nested values preserve every scalar runtime type")]
    public void Encode_NestedValues_ShouldPreserveRuntimeTypes()
    {
        // Arrange
        object?[] scalars = [null, true, (sbyte)-1, (short)-2, 3, 4L, 0.1f, 0.1d, 0.1m, "é\0", (byte)1, (ushort)2, 3U, ulong.MaxValue];
        var input = new Dictionary<string, object?> { ["nested"] = new object?[] { scalars, new Dictionary<string, object?> { ["empty"] = Array.Empty<object?>() } } };
        // Act
        var map = GraphValueCodec.Decode(GraphValueCodec.Encode(input)).ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
        var values = map!["nested"].ShouldBeOfType<object?[]>()[0].ShouldBeOfType<object?[]>();
        // Assert
        values.ShouldBe(scalars);
        values.Select(value => value?.GetType()).ShouldBe(scalars.Select(value => value?.GetType()));
    }

    public static IEnumerable<object[]> Malformed()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 255 }];
        yield return [new byte[] { 7 }];
        var invalidDecimalFlags = new byte[17]; invalidDecimalFlags[0] = 7; invalidDecimalFlags[13] = 1;
        yield return [invalidDecimalFlags];
        var invalidDecimalScale = new byte[17]; invalidDecimalScale[0] = 7; invalidDecimalScale[15] = 29;
        yield return [invalidDecimalScale];
        yield return [new byte[] { 0, 1, 0, 0, 0, 255 }];
        yield return [new byte[] { 1, 255, 255, 255, 127 }];
        yield return [new byte[] { 1, 255, 255, 255, 255 }];
        yield return [new byte[] { 0, 2, 0, 0, 0, 0 }];
        yield return [new byte[] { 2, 1, 0, 0, 0, 1, 0, 0, 0, 255, 3, 0 }];
        yield return [GraphValueCodec.Encode(1).Concat(new byte[] { 0 }).ToArray()];
        byte[] depth = Enumerable.Repeat(new byte[] { 1, 1, 0, 0, 0 }, GraphValueCodec.MaxDepth + 1)
            .SelectMany(bytes => bytes).Concat(GraphValueCodec.Encode(null)).ToArray();
        yield return [depth];
        yield return [new byte[] { 2, 2, 0, 0, 0, 1, 0, 0, 0, 107, 3, 1, 1, 0, 0, 0, 107, 3, 2 }];
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite codec: malformed components are protocol violations")]
    [MemberData(nameof(Malformed))]
    public void Decode_MalformedComponent_ShouldRejectBeforeAllocation(byte[] bytes)
    {
        // Act / Assert
        Should.Throw<ProtocolException>(() => GraphValueCodec.Decode(bytes));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: precommit sizes agree with actual scalar encodings")]
    public void Measure_ScalarFraming_ShouldMatchEncodedBytes()
    {
        // Arrange
        object?[] values = [null, false, (sbyte)-1, (byte)1, (ushort)2, 3U, ulong.MaxValue, (short)-2, 3, 4L,
            0.1f, 0.1d, 0m, 1000.000m, -0.000001m, decimal.MinValue, "é\0", new byte[] { 0, 1 },
            DateOnly.MinValue, TimeOnly.MinValue, DateTime.UnixEpoch, DateTimeOffset.UnixEpoch, TimeSpan.Zero, Guid.Empty];
        // Act / Assert
        foreach (var value in values) { GraphValueEncoding.Measure(value, new(ErrorMessages.CompositeLimit, ErrorMessages.CompositeMalformed, ErrorMessages.CompositeRequired)).ShouldBe(GraphValueCodec.Encode(value).Length); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite codec: encoded string bytes include framing and zero escaping")]
    [InlineData("a")]
    [InlineData("é")]
    [InlineData("\0")]
    public void Encode_StringBoundary_ShouldUseExactEncodedBytes(string unit)
    {
        // Arrange: list header 5, scalar envelope 5, string type/collation/terminator 4.
        int width = unit == "a" ? 1 : 2;
        int bytes = GraphValueCodec.MaxPayloadLength - 14;
        string text = string.Concat(Enumerable.Repeat(unit, bytes / width)) + new string('a', bytes % width);
        // Act / Assert
        GraphValueCodec.Encode(new object?[] { text }).Length.ShouldBe(GraphValueCodec.MaxPayloadLength);
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new object?[] { text + "a" }));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: claimed counts are refused before large allocations")]
    public void Decode_ImpossibleCount_ShouldBoundAllocations()
    {
        // Arrange
        byte[] payload = [1, 255, 255, 0, 0];
        try { GraphValueCodec.Decode(payload); } catch (ProtocolException) { }
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool refused = false;
        try { GraphValueCodec.Decode(payload); } catch (ProtocolException) { refused = true; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert
        refused.ShouldBeTrue();
        allocated.ShouldBeLessThan(16_384);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: sibling descendants share the count budget before allocation")]
    public void Decode_SiblingCounts_ShouldReserveAggregateBudget()
    {
        // Arrange: each child fits alone; their aggregate does not.
        byte[] sibling = new byte[5 + 40_000 * 2]; sibling[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sibling.AsSpan(1), 40_000);
        for (int i = 5; i < sibling.Length; i += 2) { sibling[i] = 3; }
        byte[] payload = new byte[] { 1, 2, 0, 0, 0 }.Concat(sibling).Concat(sibling).ToArray();
        try { GraphValueCodec.Decode(payload); } catch (ProtocolException) { }
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool refused = false;
        try { GraphValueCodec.Decode(payload); } catch (ProtocolException) { refused = true; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert: the first child and its boxed bytes fit; the second array must never allocate.
        refused.ShouldBeTrue();
        allocated.ShouldBeLessThan(1_500_000);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite codec: limits include descendants and cyclic input")]
    public void Encode_ContainerBounds_ShouldRejectWithoutRecursingUnboundedly()
    {
        // Arrange
        object? maximum = null;
        for (int i = 0; i < GraphValueCodec.MaxDepth; i++) { maximum = new object?[] { maximum }; }
        var cycle = new List<object?>(); cycle.Add(cycle);
        // Act / Assert
        GraphValueCodec.Decode(GraphValueCodec.Encode(maximum)).ShouldNotBeNull();
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new object?[] { maximum }));
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(cycle));
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new object?[GraphValueCodec.MaxValueCount]));
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new object()));
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new int[1, 1]));
        Should.Throw<DatabaseTypeException>(() => GraphValueCodec.Encode(new Dictionary<string, object?> { ["\ud800"] = 1 }));
        Should.Throw<ProtocolException>(() => GraphValueCodec.Decode(new byte[GraphValueCodec.MaxPayloadLength + 1]));
    }
}

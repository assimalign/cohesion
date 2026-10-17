using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphValueCodecTests
{
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

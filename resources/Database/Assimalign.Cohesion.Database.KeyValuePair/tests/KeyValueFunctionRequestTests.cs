using System;
using System.Collections.Generic;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueFunctionRequestTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: repeated binary aliases retain one detached snapshot per input buffer")]
    public void Constructor_RepeatedBinaryAliases_ShouldOwnOneSnapshotPerBuffer()
    {
        // Arrange: a compact CALL can refer repeatedly to the same decoded parameter.
        byte[] payload = new byte[64 * 1024];
        payload[0] = 17;
        var arguments = new object?[64];
        Array.Fill(arguments, payload);

        // Act: count physical owned buffers, not a platform-dependent allocation estimate.
        var request = new KeyValueFunctionRequest("many_arguments", arguments);
        var owned = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long uniqueOwnedBytes = 0;
        foreach (object? argument in request.ArgumentValues)
        {
            byte[] bytes = argument.ShouldBeOfType<byte[]>();
            if (owned.Add(bytes)) { uniqueOwnedBytes += bytes.LongLength; }
        }

        // Assert: argument count cannot amplify one input payload into repeated owned copies.
        uniqueOwnedBytes.ShouldBe(payload.LongLength);
        owned.ShouldHaveSingleItem().ShouldNotBeSameAs(payload);
        request.Arguments.Count.ShouldBe(64);
        payload[0] = 99;
        foreach (object? argument in request.Arguments)
        {
            ((ReadOnlyMemory<byte>)argument!).Span[0].ShouldBe((byte)17);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: identical binary ranges share snapshots while independent ranges remain distinct")]
    public void Constructor_MixedBinaryViews_ShouldUseBackingAndRangeIdentity()
    {
        // Arrange: the first three views have identical backing/range; the slices partially overlap.
        byte[] source = [0, 1, 2, 3, 4, 5];
        byte[] independent = [0, 1, 2, 3, 4, 5];
        object?[] arguments = [source, (ReadOnlyMemory<byte>)source, source.AsMemory(),
            source.AsMemory(1, 3), ((ReadOnlyMemory<byte>)source).Slice(1, 3), source.AsMemory(2, 3), independent];

        // Act
        var request = new KeyValueFunctionRequest("views", arguments);
        object?[] owned = request.ArgumentValues.ToArray();
        source[0] = 99;
        independent[0] = 88;

        // Assert: content equality must not merge independent original buffers or distinct slices.
        owned[1].ShouldBeSameAs(owned[0]);
        owned[2].ShouldBeSameAs(owned[0]);
        owned[4].ShouldBeSameAs(owned[3]);
        owned[5].ShouldNotBeSameAs(owned[3]);
        owned[6].ShouldNotBeSameAs(owned[0]);
        owned[0].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0, 1, 2, 3, 4, 5 });
        owned[3].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 1, 2, 3 });
        owned[5].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 2, 3, 4 });
        owned[6].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0, 1, 2, 3, 4, 5 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: CALL parameter aliases do not multiply decoded binary payloads")]
    public void Parse_RepeatedBinaryParameter_ShouldShareDetachedSnapshot()
    {
        // Arrange: the frame has one payload parameter and many references in command text.
        byte[] payload = new byte[64 * 1024];
        payload[0] = 23;
        var command = new StringBuilder("CALL repeated");
        for (int index = 0; index < 64; index++) { command.Append(" @payload"); }
        var parameters = new Dictionary<string, object?> { ["payload"] = payload };

        // Act
        var request = KeyValueCommandParser.Parse(command.ToString(), parameters).ShouldBeOfType<KeyValueFunctionRequest>();
        var owned = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (object? argument in request.ArgumentValues) { owned.Add(argument.ShouldBeOfType<byte[]>()); }

        // Assert
        byte[] snapshot = owned.ShouldHaveSingleItem();
        snapshot.ShouldNotBeSameAs(payload);
        snapshot.Length.ShouldBe(payload.Length);
        payload[0] = 99;
        snapshot[0].ShouldBe((byte)23);
        request.Arguments.Count.ShouldBe(64);
    }
}

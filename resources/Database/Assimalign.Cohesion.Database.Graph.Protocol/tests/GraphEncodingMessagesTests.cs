using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol.Tests.TestObjects;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Protocol.Tests;

public sealed class GraphEncodingMessagesTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Protocol] - Successful size and codec operations perform zero message lookups")]
    public void Encoding_ValidValues_ShouldPerformZeroMessageLookups()
    {
        // Arrange
        object?[] scalars = [null, true, (sbyte)-1, (short)-2, 3, 4L, 0.1f, 0.1d, 1.20m,
            "é\0", new byte[] { 0, 1 }, (byte)1, (ushort)2, 3U, ulong.MaxValue,
            DateOnly.MinValue, TimeOnly.MinValue, DateTime.UnixEpoch, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, Guid.Empty];
        object?[] values = [.. scalars, scalars,
            new Dictionary<string, object?> { ["nested"] = scalars }];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var scalarWriter = new DatabaseKeyWriter(32);
        CountingGraphEncodingMessages.Reset();

        // Act
        foreach (object? scalar in scalars)
        {
            GraphValueEncoding.ScalarLength<CountingGraphEncodingMessages>(scalar);
        }
        foreach (object? value in values)
        {
            int measured = GraphValueEncoding.Measure<CountingGraphEncodingMessages>(value);
            byte[] encoded = GraphValueEncoding.Encode<CountingGraphEncodingMessages>(value);
            GraphValueEncoding.Decode<CountingGraphEncodingMessages>(encoded);
            stream.SetLength(0);
            stream.Position = 0;
            int count = 0;
            GraphValueEncoding.Write<CountingGraphEncodingMessages>(writer, value, 0, ref count, scalarWriter);
            stream.Length.ShouldBe(measured);
            encoded.Length.ShouldBe(measured);
        }

        // Assert
        CountingGraphEncodingMessages.LookupCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Protocol] - Failures look up only their message using the current UI culture")]
    public void Encoding_FailureAfterCultureChange_ShouldLookUpOnlyCurrentMessage()
    {
        // Arrange
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            // Act / Assert
            foreach (string culture in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                CountingGraphEncodingMessages.Reset();
                Should.Throw<DatabaseTypeException>(() =>
                    GraphValueEncoding.Encode<CountingGraphEncodingMessages>(new object()))
                    .Message.ShouldBe("CompositeRequired:" + culture);
                CountingGraphEncodingMessages.LookupCount.ShouldBe(1);

                CountingGraphEncodingMessages.Reset();
                Should.Throw<ProtocolException>(() =>
                    GraphValueEncoding.Decode<CountingGraphEncodingMessages>(Array.Empty<byte>()))
                    .Message.ShouldBe("CompositeMalformed:" + culture);
                CountingGraphEncodingMessages.LookupCount.ShouldBe(1);

                CountingGraphEncodingMessages.Reset();
                Should.Throw<DatabaseTypeException>(() =>
                    GraphValueEncoding.Measure<CountingGraphEncodingMessages>(new object?[GraphValueEncoding.MaxValueCount]))
                    .Message.ShouldBe("CompositeLimit:" + culture);
                CountingGraphEncodingMessages.LookupCount.ShouldBe(1);
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }
}

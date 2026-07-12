using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Tests;

/// <summary>
/// Tests for the boxed-value bridge (#852): runtime-type dispatch onto the shared
/// component encoding, boxed read-back, and the single-component parameter format.
/// </summary>
public class DatabaseValueCodecTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Types] - ValueCodec: boxed values of every supported runtime type round-trip")]
    public void AppendThenRead_SupportedRuntimeTypes_ShouldRoundTrip()
    {
        // Arrange: one value per supported runtime type, plus null
        object?[] values =
        [
            null,
            true,
            (sbyte)-5,
            (short)-1234,
            42,
            42L,
            1.5f,
            -2.25d,
            123.456m,
            "text with ✓",
            new byte[] { 1, 0, 255 },
            new DateOnly(2026, 7, 12),
            new TimeOnly(13, 30, 5),
            new DateTime(2026, 7, 12, 13, 30, 5, DateTimeKind.Utc),
            new DateTimeOffset(2026, 7, 12, 13, 30, 5, TimeSpan.FromHours(-4)),
            TimeSpan.FromMinutes(90),
            Guid.Parse("1c9b02f4-3a55-4a9e-9d5a-46e2f2e1c2aa"),
        ];

        var writer = new DatabaseKeyWriter();

        // Act
        foreach (object? value in values)
        {
            DatabaseValueCodec.Append(writer, value);
        }

        var decoded = new List<object?>();
        var reader = new DatabaseKeyReader(writer.WrittenSpan);

        while (!reader.IsAtEnd)
        {
            decoded.Add(DatabaseValueCodec.Read(ref reader));
        }

        // Assert
        decoded.ShouldBe(values);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - ValueCodec: single-component payloads round-trip and reject trailing data")]
    public void EncodeDecodeComponent_SingleValue_ShouldRoundTripAndStayStrict()
    {
        // Act
        byte[] payload = DatabaseValueCodec.EncodeComponent(42);

        // Assert
        DatabaseValueCodec.DecodeComponent(payload).ShouldBe(42);
        DatabaseValueCodec.DecodeComponent(DatabaseValueCodec.EncodeComponent(null)).ShouldBeNull();

        // Two components in one parameter payload is malformed
        var writer = new DatabaseKeyWriter();
        DatabaseValueCodec.Append(writer, 1);
        DatabaseValueCodec.Append(writer, 2);
        Should.Throw<DatabaseTypeException>(() => DatabaseValueCodec.DecodeComponent(writer.WrittenSpan));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - ValueCodec: unsupported runtime types fail loudly")]
    public void Append_UnsupportedRuntimeType_ShouldThrow()
    {
        // Arrange
        var writer = new DatabaseKeyWriter();

        // Act / Assert
        Should.Throw<DatabaseTypeException>(() => DatabaseValueCodec.Append(writer, new Uri("https://example.test")));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Tests;

/// <summary>
/// Ordering and round-trip tests for the order-preserving key encodings (#854):
/// for every scalar type, byte-wise comparison of encoded keys must equal value
/// comparison, and decoding must return the original value.
/// </summary>
public class DatabaseKeyEncodingTests
{
    private static byte[] Encode(Action<DatabaseKeyWriter> append)
    {
        var writer = new DatabaseKeyWriter();
        append(writer);
        return writer.ToArray();
    }

    private static void AssertStrictlyAscending<T>(IReadOnlyList<T> orderedValues, Func<T, byte[]> encode)
    {
        for (int i = 1; i < orderedValues.Count; i++)
        {
            byte[] previous = encode(orderedValues[i - 1]);
            byte[] current = encode(orderedValues[i]);

            previous.AsSpan().SequenceCompareTo(current).ShouldBeLessThan(
                0,
                $"expected encoding of '{orderedValues[i - 1]}' to sort before '{orderedValues[i]}'");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: Int64 order is preserved across the full range")]
    public void AppendInt64_OrderedValues_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[] { long.MinValue, -1_000_000L, -42L, -1L, 0L, 1L, 42L, 1_000_000L, long.MaxValue },
            value => Encode(w => w.AppendInt64(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: Int32/Int16/Int8 order is preserved")]
    public void AppendSmallerIntegers_OrderedValues_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[] { int.MinValue, -7, 0, 7, int.MaxValue },
            value => Encode(w => w.AppendInt32(value)));
        AssertStrictlyAscending(
            new short[] { short.MinValue, -7, 0, 7, short.MaxValue },
            value => Encode(w => w.AppendInt16(value)));
        AssertStrictlyAscending(
            new sbyte[] { sbyte.MinValue, -7, 0, 7, sbyte.MaxValue },
            value => Encode(w => w.AppendInt8(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: Float64 total order includes infinities, negative zero, and NaN")]
    public void AppendFloat64_TotalOrder_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[] { double.NegativeInfinity, double.MinValue, -1.5, -double.Epsilon, -0.0, 0.0, double.Epsilon, 1.5, double.MaxValue, double.PositiveInfinity, double.NaN },
            value => Encode(w => w.AppendFloat64(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: Decimal order is preserved across magnitudes and scales")]
    public void AppendDecimal_OrderedValues_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[]
            {
                decimal.MinValue, -1234567.89m, -1.55m, -1.5m, -1.05m, -0.001m,
                0m,
                0.001m, 0.01m, 1.05m, 1.5m, 1.55m, 42m, 1234567.89m, decimal.MaxValue,
            },
            value => Encode(w => w.AppendDecimal(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: binary-collated strings order by code point")]
    public void AppendString_BinaryCollation_ShouldOrderByCodePoint()
    {
        AssertStrictlyAscending(
            new[] { "", "A", "AB", "Z", "a", "ab", "b", "é", "中", "\U0001F600" },
            value => Encode(w => w.AppendString(value, Collation.Binary)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: invariant-collated strings order linguistically")]
    public void AppendString_InvariantCollation_ShouldOrderLinguistically()
    {
        var values = new[] { "apple", "Apple", "banana", "cherry" };
        var expected = values.OrderBy(x => x, StringComparer.InvariantCulture).ToArray();

        AssertStrictlyAscending(
            expected,
            value => Encode(w => w.AppendString(value, Collation.Invariant)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: string prefix relationships survive encoding")]
    public void AppendString_PrefixPairs_ShouldOrderPrefixFirst()
    {
        // A shorter string that is a prefix of a longer one must sort first, and a
        // string containing an embedded zero-adjacent character must not collide
        // with the terminator.
        AssertStrictlyAscending(
            new[] { "ab", "ab", "abx", "abc" },
            value => Encode(w => w.AppendString(value, Collation.Binary)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: binary payloads with embedded zeros order correctly")]
    public void AppendBinary_EmbeddedZeros_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[]
            {
                Array.Empty<byte>(),
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x00 },
                new byte[] { 0x00, 0x01 },
                new byte[] { 0x01 },
                new byte[] { 0x01, 0x00 },
                new byte[] { 0xFF },
            },
            value => Encode(w => w.AppendBinary(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: date and time types preserve chronological order")]
    public void AppendTemporalTypes_OrderedValues_ShouldPreserveOrder()
    {
        AssertStrictlyAscending(
            new[] { DateOnly.MinValue, new DateOnly(1999, 12, 31), new DateOnly(2026, 7, 11), DateOnly.MaxValue },
            value => Encode(w => w.AppendDate(value)));

        AssertStrictlyAscending(
            new[] { TimeOnly.MinValue, new TimeOnly(8, 30), new TimeOnly(23, 59, 59), TimeOnly.MaxValue },
            value => Encode(w => w.AppendTime(value)));

        AssertStrictlyAscending(
            new[] { DateTime.MinValue, new DateTime(2001, 1, 1), new DateTime(2026, 7, 11, 12, 0, 0), DateTime.MaxValue },
            value => Encode(w => w.AppendDateTime(value)));

        AssertStrictlyAscending(
            new[]
            {
                DateTimeOffset.MinValue,
                new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(2)),  // 10:00Z
                new DateTimeOffset(2026, 7, 11, 11, 0, 0, TimeSpan.FromHours(0)),  // 11:00Z
                DateTimeOffset.MaxValue,
            },
            value => Encode(w => w.AppendDateTimeOffset(value)));

        AssertStrictlyAscending(
            new[] { TimeSpan.MinValue, TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.FromDays(1), TimeSpan.MaxValue },
            value => Encode(w => w.AppendTimeSpan(value)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: null orders before every value and composite keys order by significance")]
    public void CompositeKeys_NullsAndComponents_ShouldOrderBySignificance()
    {
        byte[] nullThenMax = Encode(w => w.AppendNull().AppendInt64(long.MaxValue));
        byte[] minThenNull = Encode(w => w.AppendInt64(long.MinValue).AppendNull());
        nullThenMax.AsSpan().SequenceCompareTo(minThenNull).ShouldBeLessThan(0);

        // (1, "b") < (2, "a") — the first component dominates.
        byte[] oneB = Encode(w => w.AppendInt32(1).AppendString("b", Collation.Binary));
        byte[] twoA = Encode(w => w.AppendInt32(2).AppendString("a", Collation.Binary));
        oneB.AsSpan().SequenceCompareTo(twoA).ShouldBeLessThan(0);

        // (1, "a") < (1, "b") — ties fall to the second component.
        byte[] oneA = Encode(w => w.AppendInt32(1).AppendString("a", Collation.Binary));
        oneA.AsSpan().SequenceCompareTo(oneB).ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - RoundTrip: every scalar type decodes to its original value")]
    public void Reader_EncodedComponents_ShouldRoundTrip()
    {
        // Arrange
        var guid = Guid.NewGuid();
        var timestamp = new DateTime(2026, 7, 11, 9, 30, 15, DateTimeKind.Utc);
        var offsetValue = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(-5));

        var writer = new DatabaseKeyWriter();
        writer
            .AppendNull()
            .AppendBoolean(true)
            .AppendInt8(-5)
            .AppendInt16(-1234)
            .AppendInt32(987654)
            .AppendInt64(-9_876_543_210L)
            .AppendFloat32(1.25f)
            .AppendFloat64(-2.5)
            .AppendDecimal(-1234.5678m)
            .AppendString("héllo   wörld", Collation.Binary)
            .AppendString("Invariant Text", Collation.Invariant)
            .AppendBinary(new byte[] { 0x00, 0x01, 0xFF, 0x00 })
            .AppendDate(new DateOnly(2026, 7, 11))
            .AppendTime(new TimeOnly(23, 45, 12))
            .AppendDateTime(timestamp)
            .AppendDateTimeOffset(offsetValue)
            .AppendTimeSpan(TimeSpan.FromMinutes(-90))
            .AppendGuid(guid);

        // Act / Assert
        var reader = new DatabaseKeyReader(writer.WrittenSpan);
        reader.ReadNull().ShouldBeNull();
        reader.ReadBoolean().ShouldBeTrue();
        reader.ReadInt8().ShouldBe((sbyte)-5);
        reader.ReadInt16().ShouldBe((short)-1234);
        reader.ReadInt32().ShouldBe(987654);
        reader.ReadInt64().ShouldBe(-9_876_543_210L);
        reader.ReadFloat32().ShouldBe(1.25f);
        reader.ReadFloat64().ShouldBe(-2.5);
        reader.ReadDecimal().ShouldBe(-1234.5678m);
        reader.ReadString(out var binaryCollation).ShouldBe("héllo   wörld");
        binaryCollation.ShouldBeSameAs(Collation.Binary);
        reader.ReadString(out var invariantCollation).ShouldBe("Invariant Text");
        invariantCollation.ShouldBeSameAs(Collation.Invariant);
        reader.ReadBinary().ShouldBe(new byte[] { 0x00, 0x01, 0xFF, 0x00 });
        reader.ReadDate().ShouldBe(new DateOnly(2026, 7, 11));
        reader.ReadTime().ShouldBe(new TimeOnly(23, 45, 12));

        var decodedDateTime = reader.ReadDateTime();
        decodedDateTime.ShouldBe(timestamp);
        decodedDateTime.Kind.ShouldBe(DateTimeKind.Utc);

        var decodedOffset = reader.ReadDateTimeOffset();
        decodedOffset.ShouldBe(offsetValue);
        decodedOffset.Offset.ShouldBe(TimeSpan.FromHours(-5)); // offset itself round-trips, not just the instant

        reader.ReadTimeSpan().ShouldBe(TimeSpan.FromMinutes(-90));
        reader.ReadGuid().ShouldBe(guid);
        reader.IsAtEnd.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - RoundTrip: decimal edge magnitudes decode exactly")]
    public void ReadDecimal_EdgeValues_ShouldRoundTripExactly()
    {
        var values = new[]
        {
            decimal.MinValue, decimal.MaxValue, 0m, 1m, -1m,
            0.0000000000000000000000000001m, -0.0000000000000000000000000001m,
            79228162514264337593543950334m, 123.4500m /* trailing zeros normalize away */,
        };

        foreach (var value in values)
        {
            var writer = new DatabaseKeyWriter();
            writer.AppendDecimal(value);
            var reader = new DatabaseKeyReader(writer.WrittenSpan);
            reader.ReadDecimal().ShouldBe(value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Reader: type mismatches and truncation fail loudly")]
    public void Reader_TypeMismatchOrTruncation_ShouldThrow()
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt64(5);

        Should.Throw<DatabaseTypeException>(() =>
        {
            var reader = new DatabaseKeyReader(writer.WrittenSpan);
            reader.ReadBoolean();
        });

        Should.Throw<DatabaseTypeException>(() =>
        {
            var reader = new DatabaseKeyReader(writer.WrittenSpan[..4]);
            reader.ReadInt64();
        });
    }
}

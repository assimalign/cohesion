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

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: legacy invariant collation is not index-backed")]
    public void AppendString_InvariantCollation_ShouldRejectWithoutChangingWriter()
    {
        var writer = new DatabaseKeyWriter().AppendInt32(42);
        byte[] original = writer.ToArray();

        Should.Throw<DatabaseTypeException>(() => writer.AppendString("apple", Collation.Invariant))
            .Message.ShouldContain("not index-backed", Case.Sensitive);
        writer.ToArray().ShouldBe(original);
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

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Encoding: temporal identity forms encode SQL-equal values identically (#1099)")]
    public void AppendTemporalIdentityForms_EqualValues_ShouldEncodeIdentically()
    {
        // The value encoding keeps the kind and the offset, so values that SQL
        // equality joins still encode apart — the reason identity keys normalize.
        var noon = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Unspecified);
        DateTime[] kinds = [noon, DateTime.SpecifyKind(noon, DateTimeKind.Utc), DateTime.SpecifyKind(noon, DateTimeKind.Local)];
        kinds.Select(value => Convert.ToHexString(Encode(w => w.AppendDateTime(value)))).Distinct().Count().ShouldBe(3);

        var instant = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset[] offsets = [instant, instant.ToOffset(TimeSpan.FromHours(3)), instant.ToOffset(TimeSpan.FromHours(-5.5))];
        offsets.Select(value => Convert.ToHexString(Encode(w => w.AppendDateTimeOffset(value)))).Distinct().Count().ShouldBe(3);

        // The identity forms — kind Unspecified, offset zero — encode one key per
        // equality class, and that key decodes to the identity form.
        byte[] timestampKey = Encode(w => w.AppendDateTime(noon));
        foreach (var value in kinds)
        {
            Encode(w => w.AppendDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified))).ShouldBe(timestampKey);
        }

        byte[] instantKey = Encode(w => w.AppendDateTimeOffset(instant));
        foreach (var value in offsets)
        {
            Encode(w => w.AppendDateTimeOffset(value.ToUniversalTime())).ShouldBe(instantKey);
        }

        var reader = new DatabaseKeyReader(instantKey);
        reader.ReadDateTimeOffset().Offset.ShouldBe(TimeSpan.Zero);

        // Identity forms keep the chronological order: by ticks, and by instant
        // even where local wall-clock time disagrees (12:00+01:00 is 11:00Z).
        AssertStrictlyAscending(
            new[]
            {
                new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(1)),
                instant.ToOffset(TimeSpan.FromHours(-5.5)),
                new DateTimeOffset(2026, 9, 30, 5, 0, 0, TimeSpan.FromHours(-8)),
            },
            value => Encode(w => w.AppendDateTimeOffset(value.ToUniversalTime())));
        AssertStrictlyAscending(
            new[] { DateTime.SpecifyKind(noon.AddTicks(-1), DateTimeKind.Utc), noon, DateTime.SpecifyKind(noon.AddTicks(1), DateTimeKind.Local) },
            value => Encode(w => w.AppendDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified))));
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
            .AppendString("Canonical Text", Collation.CaseInsensitive)
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
        reader.ReadString(out var foldedCollation).ShouldBe("canonical text");
        foldedCollation.ShouldBeSameAs(Collation.CaseInsensitive);
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

    /// <summary>One component of every type the writer appends, and the legacy invariant string.</summary>
    public static TheoryData<string> SkippableComponents => new()
    {
        "null", "boolean", "int8", "int16", "int32", "int64", "float32", "float64", "decimal-zero", "decimal-positive",
        "decimal-negative", "string-binary", "string-folded", "string-legacy-invariant", "binary", "date", "time",
        "datetime", "datetimeoffset", "timespan", "guid",
    };

    [Theory(DisplayName = "Cohesion Test [Database.Types] - Reader: Skip consumes exactly one component of every type")]
    [MemberData(nameof(SkippableComponents))]
    public void Reader_Skip_ShouldConsumeExactlyOneComponent(string component)
    {
        // Arrange: the component between two sentinels.
        byte[] key = [.. Encode(w => w.AppendInt32(-17)), .. ComponentBytes(component), .. Encode(w => w.AppendInt32(4242))];
        var reader = new DatabaseKeyReader(key);
        reader.ReadInt32().ShouldBe(-17);

        // Act
        reader.Skip();

        // Assert: the reader stands on the next component.
        reader.ReadInt32().ShouldBe(4242);
        reader.IsAtEnd.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Reader: Skip walks past a large string or binary payload without copying it")]
    public void Reader_SkipLargePayloads_ShouldNotAllocateThem()
    {
        // Arrange: about 1 MiB of string and 1 MiB of binary payload, zero bytes included so
        // every escape sequence is walked.
        string text = string.Concat(Enumerable.Repeat("dropped\0column ", 70_000));
        byte[] bytes = Enumerable.Range(0, 1 << 20).Select(value => (byte)(value % 7)).ToArray();
        byte[] key = new DatabaseKeyWriter().AppendString(text, Collation.Binary).AppendBinary(bytes).AppendNull().ToArray();
        var warmUp = new DatabaseKeyReader(key);
        warmUp.Skip();
        warmUp.Skip();

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var reader = new DatabaseKeyReader(key);
        reader.Skip();
        reader.Skip();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        reader.ReadNull().ShouldBeNull();
        reader.IsAtEnd.ShouldBeTrue();
        allocated.ShouldBeLessThan(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Types] - Reader: Skip refuses a malformed or truncated component")]
    [InlineData(new byte[] { }, "no further components")]
    [InlineData(new byte[] { 0xEE }, "unexpected component type")]
    [InlineData(new byte[] { (byte)DatabaseType.Int64, 1, 2, 3 }, "truncated component")]
    [InlineData(new byte[] { (byte)DatabaseType.DateTime, 1, 2, 3, 4, 5, 6, 7, 8 }, "truncated component")]
    [InlineData(new byte[] { (byte)DatabaseType.String, 9, (byte)'a', 0, 0 }, "Unknown collation identifier 9")]
    [InlineData(new byte[] { (byte)DatabaseType.String, 0, (byte)'a', (byte)'b' }, "unterminated variable-length component")]
    [InlineData(new byte[] { (byte)DatabaseType.Binary, (byte)'a', 0 }, "truncated escape sequence")]
    [InlineData(new byte[] { (byte)DatabaseType.Binary, (byte)'a', 0, 0x07 }, "invalid escape marker 0x07")]
    [InlineData(new byte[] { (byte)DatabaseType.String, 1, (byte)'a', 0, 0, 0, 0, 0, 9, (byte)'a' }, "invalid string payload length")]
    [InlineData(new byte[] { (byte)DatabaseType.Decimal, 7 }, "invalid decimal sign byte 0x07")]
    [InlineData(new byte[] { (byte)DatabaseType.Decimal, 2 }, "truncated decimal exponent")]
    [InlineData(new byte[] { (byte)DatabaseType.Decimal, 2, 65, 3, 4 }, "unterminated decimal digits")]
    [InlineData(new byte[] { (byte)DatabaseType.Decimal, 2, 65, 12, 0 }, "invalid decimal digit byte 0x0C")]
    [InlineData(new byte[] { (byte)DatabaseType.Decimal, 2, 65, 0 }, "decimal component has no digits")]
    public void Reader_SkipMalformedComponent_ShouldThrow(byte[] key, string message)
    {
        Should.Throw<DatabaseTypeException>(() =>
        {
            var reader = new DatabaseKeyReader(key);
            reader.Skip();
        }).Message.ShouldContain(message);
    }

    private static byte[] ComponentBytes(string component) => component switch
    {
        "null" => Encode(w => w.AppendNull()),
        "boolean" => Encode(w => w.AppendBoolean(true)),
        "int8" => Encode(w => w.AppendInt8(-5)),
        "int16" => Encode(w => w.AppendInt16(-1234)),
        "int32" => Encode(w => w.AppendInt32(int.MinValue)),
        "int64" => Encode(w => w.AppendInt64(long.MaxValue)),
        "float32" => Encode(w => w.AppendFloat32(-1.5f)),
        "float64" => Encode(w => w.AppendFloat64(double.NaN)),
        "decimal-zero" => Encode(w => w.AppendDecimal(0m)),
        "decimal-positive" => Encode(w => w.AppendDecimal(1234.5678m)),
        "decimal-negative" => Encode(w => w.AppendDecimal(-0.000123m)),
        "string-binary" => Encode(w => w.AppendString("embedded\0zero and ünïcode", Collation.Binary)),
        "string-folded" => Encode(w => w.AppendString("Café", Collation.CaseAccentInsensitive)),
        // What a writer before the invariant collation was retired left behind: the tag,
        // the invariant collation, an escaped sort key, then the original bytes,
        // length-prefixed (see ReadString). Writers refuse to produce it today.
        "string-legacy-invariant" => [(byte)DatabaseType.String, Collation.Invariant.Id,
            (byte)'a', 0x00, 0xFF, (byte)'b', 0x00, 0x00, 0, 0, 0, 3, (byte)'a', 0x00, (byte)'b'],
        "binary" => Encode(w => w.AppendBinary(new byte[] { 0x00, 0xFF, 0x00, 0x01 })),
        "date" => Encode(w => w.AppendDate(new DateOnly(2026, 10, 4))),
        "time" => Encode(w => w.AppendTime(new TimeOnly(23, 59, 58))),
        "datetime" => Encode(w => w.AppendDateTime(new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc))),
        "datetimeoffset" => Encode(w => w.AppendDateTimeOffset(new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.FromHours(2)))),
        "timespan" => Encode(w => w.AppendTimeSpan(TimeSpan.FromMinutes(-90))),
        "guid" => Encode(w => w.AppendGuid(Guid.Parse("5b8f8a59-7a2c-4b45-9d55-0a4d3a5f0c11"))),
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
    };

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

using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlIndexKeyEncodingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Directed key oracle: encoded scalar order equals SQL value order (#1631)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Encode_AllTypesAndCollations_ShouldMatchDeclaredOrder(int bits)
    {
        // Arrange: fixed seeds, boundary values, zero bytes and prefix strings.
        var order = (SqlCatalogIndexKeyOrder)bits;
        foreach (var (type, values, collation) in
            from item in Values()
            from collation in new[] { Collation.Binary, Collation.CaseInsensitive, Collation.CaseAccentInsensitive }
            select (item.Type, item.Values, collation))
        {
            // Act / Assert: compare every pair, including SQL equality classes.
            foreach (var (left, right) in from left in values from right in values select (left, right))
            {
                byte[] first = Encode(type, left, collation, order);
                byte[] second = Encode(type, right, collation, order);
                int expected = Compare(left, right, collation, order);
                Math.Sign(first.AsSpan().SequenceCompareTo(second)).ShouldBe(Math.Sign(expected),
                    type + "/" + collation.Name + "/" + order + ": " + left + " vs " + right);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Directed key oracle: generated composite order applies independently to every component (#1631)")]
    public void Encode_GeneratedMixedKeys_ShouldMatchLexicographicOrder()
    {
        // Arrange
        var random = new Random(1631);
        var rows = Enumerable.Range(0, 48).Select(i => new object?[]
        { i % 7 == 0 ? null : random.Next(-3, 4), i % 5 == 0 ? null : new string((char)('a' + random.Next(3)), random.Next(4)),
            i % 11 == 0 ? null : random.Next(-20, 21) / 10m }).ToArray();
        DatabaseType[] types = [DatabaseType.Int32, DatabaseType.String, DatabaseType.Decimal];
        foreach (var (first, second, third) in
            from first in Enumerable.Range(0, 4) from second in Enumerable.Range(0, 4) from third in Enumerable.Range(0, 4)
            select (first, second, third))
        {
            SqlCatalogIndexKeyOrder[] orders = [(SqlCatalogIndexKeyOrder)first, (SqlCatalogIndexKeyOrder)second, (SqlCatalogIndexKeyOrder)third];
            byte[][] encoded = rows.Select(row => types.SelectMany((type, i) => Encode(type, row[i], Collation.Binary, orders[i])).ToArray()).ToArray();
            // Act / Assert
            foreach (var (i, j) in from i in Enumerable.Range(0, rows.Length) from j in Enumerable.Range(0, rows.Length) select (i, j))
            {
                int expected = 0;
                for (int k = 0; k < types.Length && expected == 0; k++) { expected = Compare(rows[i][k], rows[j][k], Collation.Binary, orders[k]); }
                Math.Sign(encoded[i].AsSpan().SequenceCompareTo(encoded[j])).ShouldBe(Math.Sign(expected));
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Directed key encoding: ordinary ascending values retain their bytes (#1631)")]
    public void Encode_OrdinaryAscendingValues_ShouldPreserveSharedBytes()
    {
        // Arrange / Act / Assert
        foreach (var (type, value) in from item in Values()
            from value in item.Values.Where(value => value is not null && value is not float && value is not double)
            select (item.Type, value))
        {
            var writer = new DatabaseKeyWriter();
            SqlRowCodec.AppendKeyValue(writer, type, value, Collation.Binary);
            Encode(type, value, Collation.Binary, SqlCatalogIndexKeyOrder.AscendingNullsLast).ShouldBe(writer.ToArray());
        }
    }

    private static byte[] Encode(DatabaseType type, object? value, Collation collation, SqlCatalogIndexKeyOrder order)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, type, value, collation);
        byte[] bytes = writer.ToArray();
        SqlIndexKeyEncoding.Transform(bytes, order);
        return bytes;
    }

    private static int Compare(object? left, object? right, Collation collation, SqlCatalogIndexKeyOrder order)
    {
        if (left is null || right is null)
        {
            if (left is null && right is null) { return 0; }
            return (left is null ? -1 : 1) * (((int)order & 1) != 0 ? 1 : -1);
        }
        return SqlValueComparer.Compare(left, right, collation) * (((int)order & 2) != 0 ? -1 : 1);
    }

    private static IEnumerable<(DatabaseType Type, object?[] Values)> Values()
    {
        var random = new Random(1631);
        foreach (var (type, boundary) in BoundaryValues())
        {
            var values = new List<object?>(boundary);
            for (int i = 0; i < 12; i++)
            {
                byte[] bytes = new byte[16];
                random.NextBytes(bytes);
                string text = string.Concat(Enumerable.Range(0, random.Next(9)).Select(_ => new[] { "a", "E", "é", "\0", "😀", "\uE000" }[random.Next(6)]));
                object? value = type switch
                {
                    DatabaseType.Boolean => random.Next(2) == 0,
                    DatabaseType.Int8 => (sbyte)random.Next(-128, 128),
                    DatabaseType.Int16 => (short)random.Next(short.MinValue, short.MaxValue),
                    DatabaseType.Int32 => random.Next(int.MinValue, int.MaxValue),
                    DatabaseType.Int64 => random.NextInt64(long.MinValue, long.MaxValue),
                    DatabaseType.Float32 => (float)((random.NextDouble() - 0.5) * 1e20),
                    DatabaseType.Float64 => (random.NextDouble() - 0.5) * 1e200,
                    DatabaseType.Decimal => random.NextInt64() / 1000m,
                    DatabaseType.String or DatabaseType.Json => text,
                    DatabaseType.Character => new SqlCharacter(text + "  "),
                    DatabaseType.Binary or DatabaseType.JsonBinary => bytes[..random.Next(17)],
                    DatabaseType.Date => DateOnly.FromDayNumber(random.Next(DateOnly.MaxValue.DayNumber)),
                    DatabaseType.Time => new TimeOnly(random.NextInt64(TimeSpan.TicksPerDay)),
                    DatabaseType.DateTime => new DateTime(random.NextInt64(DateTime.MaxValue.Ticks), (DateTimeKind)random.Next(3)),
                    DatabaseType.DateTimeOffset => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(1_000_000)).ToOffset(TimeSpan.FromHours(random.Next(-14, 15))),
                    DatabaseType.TimeSpan => TimeSpan.FromTicks(random.NextInt64(long.MinValue, long.MaxValue)),
                    DatabaseType.Guid => new Guid(bytes),
                    _ => throw new InvalidOperationException(),
                };
                values.Add(value);
            }
            yield return (type, values.ToArray());
        }
    }

    private static IEnumerable<(DatabaseType Type, object?[] Values)> BoundaryValues()
    {
        var random = new Random(1631);
        yield return (DatabaseType.Boolean, [null, false, true]);
        yield return (DatabaseType.Int8, new object?[] { null, sbyte.MinValue, (sbyte)-1, (sbyte)0, sbyte.MaxValue }.Concat(Enumerable.Range(0, 12).Select(_ => (object?)(sbyte)random.Next(-128, 128))).ToArray());
        yield return (DatabaseType.Int16, [null, short.MinValue, (short)-1, (short)0, short.MaxValue]);
        yield return (DatabaseType.Int32, [null, int.MinValue, -1, 0, 1, int.MaxValue]);
        yield return (DatabaseType.Int64, [null, long.MinValue, -1L, 0L, 1L, long.MaxValue]);
        yield return (DatabaseType.Float32, [null, float.NaN, BitConverter.UInt32BitsToSingle(0x7f800001), float.NegativeInfinity, float.MinValue, -float.Epsilon, -0f, 0f, float.Epsilon, float.MaxValue, float.PositiveInfinity]);
        yield return (DatabaseType.Float64, [null, double.NaN, BitConverter.UInt64BitsToDouble(0x7ff0000000000001), double.NegativeInfinity, double.MinValue, -double.Epsilon, -0d, 0d, double.Epsilon, Math.BitIncrement(1d), double.MaxValue, double.PositiveInfinity]);
        yield return (DatabaseType.Decimal, [null, decimal.MinValue, -100m, -1m, -0.1m, 0m, 0.0000000000000000000000000001m, 1m, 1.00m, 10m, decimal.MaxValue]);
        object?[] strings = [null, "", "a", "a\0", "aa", "A", "é", "e", "😀", "𐀀", "ÿ"];
        yield return (DatabaseType.String, strings);
        yield return (DatabaseType.Json, strings);
        yield return (DatabaseType.Character, strings.Select(value => value is string text ? new SqlCharacter(text + "  ") : null).Cast<object?>().ToArray());
        object?[] bytes = [null, Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 255 }, new byte[] { 1 }, new byte[] { 255 }];
        yield return (DatabaseType.Binary, bytes);
        yield return (DatabaseType.JsonBinary, bytes);
        yield return (DatabaseType.Date, [null, DateOnly.MinValue, new DateOnly(2000, 1, 1), DateOnly.MaxValue]);
        yield return (DatabaseType.Time, [null, TimeOnly.MinValue, new TimeOnly(12, 30), TimeOnly.MaxValue]);
        yield return (DatabaseType.DateTime, [null, DateTime.MinValue, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2000, 1, 1), DateTime.MaxValue]);
        yield return (DatabaseType.DateTimeOffset, [null, DateTimeOffset.MinValue, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(5)), DateTimeOffset.MaxValue]);
        yield return (DatabaseType.TimeSpan, [null, TimeSpan.MinValue, TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.MaxValue]);
        yield return (DatabaseType.Guid, [null, Guid.Empty, Guid.Parse("01000000-0000-0000-0000-000000000000"), Guid.Parse("10000000-0000-0000-0000-000000000000"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")]);
    }
}

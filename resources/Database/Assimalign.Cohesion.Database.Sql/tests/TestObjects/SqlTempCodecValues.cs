using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal static class SqlTempCodecValues
{
    internal const int Seed = 17890202;
    internal sealed record Domain(string Name, object?[] Values);
    internal sealed record KeyDomain(string Name, object?[][] Keys, bool UsesCollation);

    internal static List<Domain> Create()
    {
        var random = new Random(Seed);
        var domains = new List<Domain>();
        void Add(string name, object?[] edges, Func<object> next)
        {
            var values = new object?[Math.Max(64, edges.Length)];
            edges.CopyTo(values, 0);
            for (int i = edges.Length; i < values.Length; i++) { values[i] = next(); }
            domains.Add(new Domain(name, values));
        }
        long Bits() => random.NextInt64(long.MinValue, long.MaxValue);
        Add("sbyte", [null, sbyte.MinValue, sbyte.MaxValue, (sbyte)0, (sbyte)1, (sbyte)-1], () => (sbyte)random.Next(-128, 128));
        Add("short", [null, short.MinValue, short.MaxValue, (short)0, (short)1, (short)-1], () => (short)random.Next(short.MinValue, short.MaxValue));
        Add("int", [null, int.MinValue, int.MaxValue, 0, 1, -1], () => (int)Bits());
        Add("long", [null, long.MinValue, long.MaxValue, 0L, 1L, -1L, 9007199254740993L], () => Bits());
        Add("float", [null, float.MinValue, float.MaxValue, 0f, -0f, float.Epsilon, -float.Epsilon,
            float.NaN, BitConverter.UInt32BitsToSingle(0x7f800001), float.NegativeInfinity, float.PositiveInfinity,
            1f, float.BitIncrement(1), float.BitDecrement(1)], () => BitConverter.UInt32BitsToSingle((uint)Bits()));
        Add("double", [null, double.MinValue, double.MaxValue, 0d, -0d, double.Epsilon, -double.Epsilon,
            double.NaN, BitConverter.UInt64BitsToDouble(0x7ff0000000000001), double.NegativeInfinity, double.PositiveInfinity,
            1d, double.BitIncrement(1), double.BitDecrement(1), 0.1d, 1e-28, 9007199254740992d], () => BitConverter.Int64BitsToDouble(Bits()));
        Add("decimal", [null, decimal.MinValue, decimal.MaxValue, 0m, new decimal(0, 0, 0, true, 28), 1m, 1.0m, 1.00m,
            1.10m, 0.1m, 0.0000000000000000000000000001m, -0.0000000000000000000000000001m],
            () => new decimal((int)Bits(), (int)Bits(), (int)Bits(), random.Next(2) == 0, (byte)random.Next(29)));
        Add("mixed numeric", domains.Take(7).SelectMany(x => x.Values).Concat(new object?[] { 1, 1.0m, 1.00m, 1d, 1f,
            0.1m, 0.1d, double.BitIncrement(0.1), 1e-28, 0.0000000000000000000000000001m, double.PositiveInfinity,
            decimal.MaxValue, (double)decimal.MaxValue, long.MaxValue, (double)long.MaxValue,
            1e-300, 2e-300, 1e-29, 1e308, 2.225073858507201e-308, 2.2250738585072014e-308,
            double.Epsilon, 2 * double.Epsilon, 0.5d, 0.5m, 0.25f, 0.25m, -1d, -1m,
            0.1f, 0.1d, 1.10m, 1.1d, 9007199254740993L, 9007199254740992d,
            double.NaN, double.NegativeInfinity, float.NaN, float.NegativeInfinity }).ToArray(), () => 0);
        Add("bool", [null, false, true], () => random.Next(2) == 0);
        object?[] text = [null, "", "a", "A", "a ", "a  ", "á", "A\u0301", "\0", "a\0b", "\0\0", "a\0",
            "ß", "ss", "Σ", "σ", "ς", "İ", "i", "ı", "\ue000", "\U00010000", "\U0010ffff", "\u0301",
            new string('x', 1025), new string('X', 1025), new string('x', 1024) + "y"];
        string Text() => string.Concat(Enumerable.Range(0, random.Next(24)).Select(_ => new[] { "a", "A", " ", "\0", "é", "e\u0301", "\U0001f600" }[random.Next(7)]));
        Add("string", text, () => Text());
        Add("CHAR", text.Select(x => x is string s ? (object)new SqlCharacter(s) : null).ToArray(), () => new SqlCharacter(Text()));
        Add("mixed text", text.Concat(new object?[] { new SqlCharacter("a"), new SqlCharacter("A  "), new SqlCharacter(""), new SqlCharacter("   ") }).ToArray(), () => random.Next(2) == 0 ? Text() : new SqlCharacter(Text()));
        byte[] Binary() { var bytes = new byte[random.Next(32)]; random.NextBytes(bytes); return bytes; }
        Add("binary", [null, Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 0 }, new byte[] { 0, 255 }, new byte[] { 255 },
            new byte[2048], Enumerable.Repeat((byte)255, 2048).ToArray()], () => Binary());
        Add("DateOnly", [null, DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(2000, 1, 1)], () => DateOnly.FromDayNumber(random.Next(DateOnly.MaxValue.DayNumber + 1)));
        Add("TimeOnly", [null, TimeOnly.MinValue, TimeOnly.MaxValue, new TimeOnly(1)], () => new TimeOnly(random.NextInt64(TimeSpan.TicksPerDay)));
        Add("DateTime", [null, DateTime.MinValue, DateTime.MaxValue, new DateTime(100, DateTimeKind.Unspecified), new DateTime(100, DateTimeKind.Utc),
            new DateTime(100, DateTimeKind.Local)], () => new DateTime(random.NextInt64(DateTime.MaxValue.Ticks + 1), (DateTimeKind)random.Next(3)));
        DateTimeOffset Instant() => new(random.NextInt64(TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay), TimeSpan.FromMinutes(random.Next(-840, 841)));
        Add("DateTimeOffset", [null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2000, 1, 1, 1, 0, 0, TimeSpan.FromHours(1))],
            () => Instant());
        Add("mixed temporal", [null, DateOnly.MinValue, DateOnly.MaxValue, DateTime.MinValue, DateTime.MaxValue, DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue, new DateOnly(2000, 1, 1), new DateTime(2000, 1, 1), new DateTimeOffset(2000, 1, 1, 1, 0, 0, TimeSpan.FromHours(1))],
            () => random.Next(3) switch
            {
                0 => new DateTime(random.NextInt64(DateTime.MaxValue.Ticks + 1)),
                1 => DateOnly.FromDayNumber(random.Next(DateOnly.MaxValue.DayNumber + 1)),
                _ => Instant(),
            });
        Add("TimeSpan", [null, TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromTicks(-1)], () => TimeSpan.FromTicks(Bits()));
        Add("Guid", [null, Guid.Empty, Guid.AllBitsSet, Guid.Parse("80000000-0000-0000-0000-000000000000"), Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff")], () => { var bytes = new byte[16]; random.NextBytes(bytes); return new Guid(bytes); });
        Add("byte", [null, byte.MinValue, byte.MaxValue], () => (byte)random.Next(256));
        Add("ushort", [null, ushort.MinValue, ushort.MaxValue], () => (ushort)random.Next(65536));
        Add("uint", [null, uint.MinValue, uint.MaxValue], () => (uint)Bits());
        Add("ulong", [null, ulong.MinValue, ulong.MaxValue], () => (ulong)Bits());
        Add("char", [null, char.MinValue, char.MaxValue, 'a', 'A', '\ud800'], () => (char)random.Next(65536));
        // JSONB projections are byte arrays, including when used as ORDER BY keys.
        Add("JSONB", [null, System.Text.Encoding.UTF8.GetBytes("{}"), System.Text.Encoding.UTF8.GetBytes("[]"),
            System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"), System.Text.Encoding.UTF8.GetBytes("{\"a\":2}")],
            () => System.Text.Encoding.UTF8.GetBytes("[" + random.Next(10000) + "]"));
        return domains;
    }
    internal static List<KeyDomain> CreateKeys(List<Domain> domains)
    {
        var result = new List<KeyDomain>();
        foreach (var domain in domains)
        {
            var values = domain.Values;
            var keys = Enumerable.Range(0, values.Length).Select(i => new object?[]
                { values[i], i % 3 == 0 ? null : values[i / 2], i % 5 == 0 ? null : values[i * 7 % values.Length] }).ToArray();
            result.Add(new(domain.Name, keys, values.Any(v => v is string or SqlCharacter)));
        }
        void Composite(string first, string second, string third)
        {
            var a = domains.Single(d => d.Name == first).Values;
            var b = domains.Single(d => d.Name == second).Values;
            var c = domains.Single(d => d.Name == third).Values;
            var keys = Enumerable.Range(0, Math.Max(a.Length, Math.Max(b.Length, c.Length)))
                .Select(i => new object?[] { a[i % a.Length], b[i * 7 % b.Length], c[i * 11 % c.Length] }).ToArray();
            result.Add(new(first + "/" + second + "/" + third, keys, keys.Any(k => k.Any(v => v is string or SqlCharacter))));
        }
        // Different domains per column, with first-column differences as well as ties.
        Composite("mixed numeric", "mixed text", "mixed temporal");
        Composite("mixed text", "JSONB", "mixed numeric");
        Composite("mixed temporal", "mixed numeric", "Guid");
        Composite("binary", "bool", "TimeOnly");
        return result;
    }
}

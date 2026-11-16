using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTempCodecTests
{
    private readonly ITestOutputHelper _output;
    public SqlTempCodecTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp codecs: seeded differential against the collector over more than one million pairs")]
    public void TryEncode_SeededPairs_ShouldMatchCollectorAndRoundTripRows()
    {
        // Arrange
        _output.WriteLine($"TEMP_DIFFERENTIAL seed={SqlTempCodecValues.Seed}");
        var domains = SqlTempCodecValues.CreateKeys(SqlTempCodecValues.Create());
        Collation[] collations = [Collation.Binary, Collation.CaseInsensitive, Collation.CaseAccentInsensitive];
        long rows = 0;
        var rowIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var evaluations = new HashSet<(int Left, int Right, int Flags, int Collation)>();
        int widest = 0;

        // Act: exhaustive ordered pairs; identical representations/configurations count once. Every column
        // sees ASC/DESC and NULLS FIRST/LAST, with leading/middle/trailing NULLs and ties.
        foreach (var domain in domains)
        {
            foreach (var collation in domain.UsesCollation ? collations : collations[..1])
            {
                for (int flags = 0; flags < 4; flags++)
                {
                    var columns = Enumerable.Range(0, 3).Select(i => new SqlTempSortKeyCodec.Column(
                        ((flags & 2) != 0) ^ (i == 1), ((flags & 1) != 0) ^ (i == 2), collation)).ToArray();
                    var order = columns.Select(c => new SqlBoundOrdering(SqlBoundConstant.Null, c.IsDescending, c.NullsFirst, new SqlBoundCollation(c.Collation))).ToArray();
                    using var collector = new SqlRowCollector(order, null, null, null, CancellationToken.None, null, 0);
                    var keys = domain.Keys;
                    var encoded = new byte[keys.Length][];
                    var identities = new int[keys.Length];
                    var hasText = keys.Select(k => k.Any(v => v is string or SqlCharacter)).ToArray();
                    var families = new byte[columns.Length];
                    for (int i = 0; i < keys.Length; i++)
                    {
                        SqlTempSortKeyCodec.TryEncode(keys[i], columns, families, out encoded[i]).ShouldBeTrue(domain.Name);
                        widest = Math.Max(widest, encoded[i].Length);
                        RoundTrip(keys[i]); rows++;
                        SqlTempRowCodec.TryEncode(keys[i], out var representation).ShouldBeTrue();
                        string identity = Convert.ToHexString(representation);
                        if (!rowIds.TryGetValue(identity, out int id)) { id = rowIds.Count; rowIds.Add(identity, id); }
                        identities[i] = id;
                    }
                    for (int a = 0; a < keys.Length; a++)
                    {
                        for (int b = 0; b < keys.Length; b++)
                        {
                            int effectiveCollation = (hasText[a] || hasText[b])
                                ? Array.IndexOf(collations, collation) : 0;
                            if (!evaluations.Add((identities[a], identities[b], flags, effectiveCollation))) { continue; }
                            int expected = Math.Sign(collector.CompareKeys(keys[a], keys[b]));
                            int actual = Math.Sign(encoded[a].AsSpan().SequenceCompareTo(encoded[b]));
                            if (actual != expected)
                            {
                                actual.ShouldBe(expected, $"seed={SqlTempCodecValues.Seed}; domain={domain.Name}; collation={collation.Name}; flags={flags}; pair={a},{b}; left={Convert.ToHexString(encoded[a])}; right={Convert.ToHexString(encoded[b])}");
                            }
                        }
                    }
                }
            }
        }

        // Assert
        evaluations.Count.ShouldBeGreaterThanOrEqualTo(1_000_000);
        widest.ShouldBeGreaterThan(1024);
        _output.WriteLine($"TEMP_DIFFERENTIAL seed={SqlTempCodecValues.Seed}; domains={domains.Count}; distinct evaluations={evaluations.Count}; row round trips={rows}; largest key={widest}; excluded shipped SQL types=none");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp key families: every ordered CLR type pair agrees with CS02055")]
    public void TryEncode_AllDomainTypePairs_ShouldRefuseExactlyIncomparableFamilies()
    {
        // Arrange: deriving the types from the corpus makes a newly added type require a family.
        var values = SqlTempCodecValues.Create().SelectMany(d => d.Values).Where(v => v is not null)
            .GroupBy(v => v!.GetType()).Select(g => g.First()!).ToArray();
        SqlTempSortKeyCodec.Column[] columns = [new(false, true, Collation.Binary)];
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary))],
            null, null, null, CancellationToken.None, null, 0);
        int pairs = 0;

        // Act and assert: even self-pairs must encode, so an unassigned type cannot hide as a refusal.
        foreach (object left in values)
        {
            foreach (object right in values)
            {
                var firstFamily = new byte[1];
                var secondFamily = new byte[1];
                SqlTempSortKeyCodec.TryEncode([left], columns, firstFamily, out _).ShouldBeTrue(left.GetType().Name);
                SqlTempSortKeyCodec.TryEncode([right], columns, secondFamily, out _).ShouldBeTrue(right.GetType().Name);
                firstFamily[0].ShouldNotBe((byte)0); secondFamily[0].ShouldNotBe((byte)0);
                bool differs = firstFamily[0] != secondFamily[0];
                bool mismatch = false;
                try { collector.CompareKeys([left], [right]); }
                catch (SqlExecutionException error) { error.Diagnostics.Single().Code.ShouldBe("CS02055"); mismatch = true; }
                mismatch.ShouldBe(differs, left.GetType().Name + "/" + right.GetType().Name);
                SqlTempSortKeyCodec.TryEncode([null], columns, firstFamily, out _).ShouldBeTrue();
                SqlTempSortKeyCodec.TryEncode([right], columns, firstFamily, out var bytes).ShouldBe(!differs);
                if (differs) { bytes.ShouldBeEmpty(); }
                pairs++;
            }
        }
        _output.WriteLine($"TEMP_FAMILIES types={values.Length}; ordered pairs={pairs}");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp key families: NULL does not establish a family and every column is checked")]
    public void TryEncode_NullsAndUntiedLeadingColumn_ShouldRetainPerColumnFamilies()
    {
        // Arrange
        SqlTempSortKeyCodec.Column[] columns = [new(false, true, Collation.Binary), new(false, true, Collation.Binary)];
        var families = new byte[2];
        using var collector = new SqlRowCollector(columns.Select(c => new SqlBoundOrdering(SqlBoundConstant.Null, c.IsDescending,
            c.NullsFirst, new SqlBoundCollation(c.Collation))).ToArray(), null, null, null, CancellationToken.None, null, 0);

        // Act and assert: the whole-sort state starts before the first spill and survives NULLs.
        SqlTempSortKeyCodec.TryEncode([null, null], columns, families, out _).ShouldBeTrue();
        families.ShouldBe(new byte[2]);
        SqlTempSortKeyCodec.TryEncode([1, false], columns, families, out _).ShouldBeTrue();
        SqlTempSortKeyCodec.TryEncode([null, null], columns, families, out _).ShouldBeTrue();
        collector.CompareKeys([1, false], [2, double.NaN]).ShouldBeLessThan(0);
        SqlTempSortKeyCodec.TryEncode([2, double.NaN], columns, families, out _).ShouldBeFalse();
        SqlTempSortKeyCodec.TryEncode([1, false], columns, new byte[1], out _).ShouldBeFalse();
    }

    private static void RoundTrip(object?[] row)
    {
        SqlTempRowCodec.TryEncode(row, out var bytes).ShouldBeTrue();
        SqlTempRowCodec.TryDecode(bytes, out var decoded).ShouldBeTrue();
        decoded.Length.ShouldBe(row.Length);
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] is null) { decoded[i].ShouldBeNull(); continue; }
            decoded[i].ShouldNotBeNull();
            decoded[i]!.GetType().ShouldBe(row[i]!.GetType());
            switch (row[i])
            {
                case decimal number: decimal.GetBits((decimal)decoded[i]!).ShouldBe(decimal.GetBits(number)); break;
                case double number: BitConverter.DoubleToUInt64Bits((double)decoded[i]!).ShouldBe(BitConverter.DoubleToUInt64Bits(number)); break;
                case float number: BitConverter.SingleToUInt32Bits((float)decoded[i]!).ShouldBe(BitConverter.SingleToUInt32Bits(number)); break;
                case DateTime date: Unsafe.BitCast<DateTime, ulong>((DateTime)decoded[i]!).ShouldBe(Unsafe.BitCast<DateTime, ulong>(date)); break;
                case DateTimeOffset date: ((DateTimeOffset)decoded[i]!).EqualsExact(date).ShouldBeTrue(); break;
                case SqlCharacter text: ((SqlCharacter)decoded[i]!).Value.ShouldBe(text.Value); ((SqlCharacter)decoded[i]!).Text.ShouldBe(text.Text); break;
                case byte[] binary: ((byte[])decoded[i]!).ShouldBe(binary); ReferenceEquals(binary, decoded[i]).ShouldBeFalse(); break;
                default: decoded[i].ShouldBe(row[i]); break;
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp keys: equality classes canonicalize representation without a tie breaker")]
    public void TryEncode_EqualRepresentations_ShouldProduceIdenticalBytes()
    {
        // Arrange
        object[][] classes = [[(sbyte)1, (short)1, 1, 1L, 1f, 1d, 1m, 1.0m, 1.00m],
            [0, -0d, -0f, new decimal(0, 0, 0, true, 28)],
            [float.NaN, double.NaN, BitConverter.UInt64BitsToDouble(0x7ff0000000000001)],
            ["a", "A", new SqlCharacter("A  ")]];

        // Act and assert
        foreach (object[] values in classes)
        {
            SqlTempSortKeyCodec.Column[] columns = [new(false, true, Collation.CaseInsensitive)];
            using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.CaseInsensitive))], null, null, null, CancellationToken.None, null, 0);
            var families = new byte[columns.Length];
            SqlTempSortKeyCodec.TryEncode([values[0]], columns, families, out var first).ShouldBeTrue();
            foreach (object value in values)
            {
                collector.CompareKeys([values[0]], [value]).ShouldBe(0);
                SqlTempSortKeyCodec.TryEncode([value], columns, families, out var bytes).ShouldBeTrue(); bytes.ShouldBe(first);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp keys: deferred collation uses the final qualifying row's resolved snapshot")]
    public void TryEncode_DeferredCollation_ShouldUseOneFinalSnapshotForAllRows()
    {
        // Arrange: the first qualifying row resolves binary; the last resolves case insensitive.
        var frame = new SqlSubqueryValues();
        var term = new SqlCollationSubqueryTerm(0, Collation.CaseInsensitive, new SqlCollationCandidate(Collation.Binary, 1));
        var bound = SqlBoundCollation.Of(SqlCollationCandidate.Deferred(term), default, Collation.Binary);
        bound.IsFixed.ShouldBeFalse();
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, bound)], frame, null, null, CancellationToken.None, null, 0);
        frame.Set(0, []); collector.Add([0], ["a"]);
        frame.Set(0, [1]); collector.Add([1], ["A"]);
        var final = bound.Resolve(frame);

        var families = new byte[1];

        // Act
        SqlTempSortKeyCodec.TryEncode(["a"], [new(false, true, final)], families, out var a).ShouldBeTrue();
        SqlTempSortKeyCodec.TryEncode(["A"], [new(false, true, final)], families, out var b).ShouldBeTrue();

        // Assert: this proves the in-memory final-snapshot rule and encoding with that snapshot.
        // It does not prove streaming spill safety: SQ-L2b must refuse this non-fixed ordering,
        // because row 1 must be encoded before the last qualifying row exists.
        collector.CompareKeys(["a"], ["A"]).ShouldBe(0); a.ShouldBe(b);
        collector.Finish().Select(row => row[0]).ShouldBe(new object?[] { 0, 1 });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temp sort record bounds: fan-in follows the declared maximum and oversized rows refuse CK05006")]
    [InlineData(1048576, 16)]
    [InlineData(4184064, 16)]
    [InlineData(4194304, 15)]
    [InlineData(8388608, 7)]
    [InlineData(16777216, 3)]
    public void Append_RecordBound_ShouldPinFanInAndRefuseOversizedRows(int bound, int fanIn)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "sql-temp-bound-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var space = StorageTempSpace.Create(directory, 268435456, () => 0);
            using var statement = space.CreateStatement(268435456);
            using var sorter = StorageTempSorter.Create(statement, 67108864, bound);

            // Act and assert
            sorter.FanIn.ShouldBe(fanIn);
            Should.Throw<StorageTempException>(() => sorter.Append([], new byte[bound - 15])).Code.ShouldBe("CK05006");
            Directory.Exists(directory).ShouldBeFalse(); // Refusal precedes creating any run.
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temp record bound: the grant-derived maximum retains fan-in sixteen")]
    [InlineData(164096, 16)]
    [InlineData(1048576, 55296)]
    [InlineData(8388608, 514048)]
    [InlineData(67108864, 4184064)]
    public void Create_GrantDerivedRecordBound_ShouldRetainMaximumFanIn(int grant, int expectedBound)
    {
        // Arrange: StorageTempSorter uses (grant - 32768) / (8192 + bound).
        int bound = (grant - 32768) / 16 - 8192;
        string directory = Path.Combine(Path.GetTempPath(), "sql-temp-formula-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var space = StorageTempSpace.Create(directory, 268435456, () => 0);
            using var statement = space.CreateStatement(268435456);

            // Act
            using var sorter = StorageTempSorter.Create(statement, grant, bound);
            using var larger = StorageTempSorter.Create(statement, grant, bound + 1);

            // Assert: the fixed 64 MiB recommendation must not be reused at smaller grants.
            bound.ShouldBe(expectedBound);
            sorter.FanIn.ShouldBe(16);
            larger.FanIn.ShouldBe(15);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp keys: more than 64 components use pooled boundaries without truncation")]
    public void TryEncode_WideKeyVector_ShouldPreserveAllComponents()
    {
        // Arrange
        object?[] first = Enumerable.Repeat<object?>("a\0", 70).ToArray();
        object?[] second = first.ToArray(); second[^1] = "b";
        var columns = Enumerable.Repeat(new SqlTempSortKeyCodec.Column(true, false, Collation.Binary), 70).ToArray();

        var families = new byte[columns.Length];

        // Act
        SqlTempSortKeyCodec.TryEncode(first, columns, families, out var a).ShouldBeTrue();
        SqlTempSortKeyCodec.TryEncode(second, columns, families, out var b).ShouldBeTrue();

        // Assert
        a.AsSpan().SequenceCompareTo(b).ShouldBeGreaterThan(0);
        RoundTrip(first);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp codecs: unsupported inputs refuse and arbitrary UTF-16 rows remain exact")]
    public void TryEncode_UnsupportedOrMalformedValues_ShouldRefuseWithoutLossyFallback()
    {
        // Arrange
        SqlTempSortKeyCodec.Column[] binary = [new(false, true, Collation.Binary)];
        object?[] row = ["\ud800\0\udfff", new SqlCharacter("\ud800  "), '\udfff'];

        // Act and assert
        RoundTrip(row);
        SqlTempRowCodec.TryEncode([new object()], out var unsupported).ShouldBeFalse(); unsupported.ShouldBeEmpty();
        SqlTempSortKeyCodec.TryEncode([new object()], binary, new byte[1], out unsupported).ShouldBeFalse(); unsupported.ShouldBeEmpty();
        SqlTempSortKeyCodec.TryEncode([row[0]], binary, new byte[1], out unsupported).ShouldBeFalse(); unsupported.ShouldBeEmpty();
        SqlTempSortKeyCodec.TryEncode(["a"], [new(false, true, Collation.Invariant)], new byte[1], out unsupported).ShouldBeFalse();
        SqlTempSortKeyCodec.TryEncode([null], [new(false, true, Collation.Invariant)], new byte[1], out unsupported).ShouldBeTrue();
        SqlTempSortKeyCodec.TryEncode([1], [new(false, true, Collation.Invariant)], new byte[1], out _).ShouldBeTrue();
        SqlTempSortKeyCodec.TryEncode([1], [], [], out unsupported).ShouldBeFalse();
        SqlTempRowCodec.TryWrite(row, new byte[1], out int written).ShouldBeFalse(); written.ShouldBe(0);
        SqlTempRowCodec.TryEncode(row, out var bytes).ShouldBeTrue();
        for (int length = 0; length < bytes.Length; length++) { SqlTempRowCodec.TryDecode(bytes.AsSpan(0, length), out _).ShouldBeFalse(); }
        SqlTempRowCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _).ShouldBeFalse();
        bytes[0]++; SqlTempRowCodec.TryDecode(bytes, out _).ShouldBeFalse();
        SqlTempRowCodec.TryDecode([2, 255, 255, 255, 127], out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp sort keys: wide composite keys and stable equality survive external merging")]
    public void Complete_EncodedRows_ShouldMatchCollectorThroughMultipleRuns()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "sql-temp-codec-" + Guid.NewGuid().ToString("N"));
        try
        {
            SqlTempSortKeyCodec.Column[] columns = [new(true, false, Collation.CaseInsensitive), new(false, true, Collation.Binary)];
            var order = columns.Select(c => new SqlBoundOrdering(SqlBoundConstant.Null, c.IsDescending, c.NullsFirst, new SqlBoundCollation(c.Collation))).ToArray();
            using var collector = new SqlRowCollector(order, null, null, null, CancellationToken.None, null, 0);
            using var space = StorageTempSpace.Create(directory, 32 * 1024 * 1024, () => 0);
            using var statement = space.CreateStatement(32 * 1024 * 1024);
            using var sorter = StorageTempSorter.Create(statement, 65536, 8192);
            var families = new byte[columns.Length];
            for (int i = 0; i < 600; i++)
            {
                object?[] keys = [i % 13 == 0 ? null : new string(i % 2 == 0 ? 'a' : 'A', 1100) + (i % 7), i % 3 == 0 ? (object)1.00m : 1];
                object?[] row = [i, keys[0], 1.10m, -0d, new SqlCharacter("a  "), new byte[] { 0, 255 }];
                SqlTempSortKeyCodec.TryEncode(keys, columns, families, out var key).ShouldBeTrue();
                SqlTempRowCodec.TryEncode(row, out var value).ShouldBeTrue();
                collector.Add(row, keys); sorter.Append(key, value);
            }

            // Act
            var expected = collector.Finish();
            using var reader = sorter.Complete();
            int count = 0;
            while (reader.MoveNext())
            {
                SqlTempRowCodec.TryDecode(reader.Value.Span, out var actual).ShouldBeTrue();
                actual[0].ShouldBe(expected[count][0]);
                RoundTrip(actual); count++;
            }

            // Assert
            count.ShouldBe(600); sorter.RunsCreated.ShouldBeGreaterThan(sorter.FanIn); sorter.MergePasses.ShouldBeGreaterThan(1);
            _output.WriteLine($"TEMP_MERGE rows={count}; runs={sorter.RunsCreated}; passes={sorter.MergePasses}; fan-in={sorter.FanIn}");
            reader.Dispose(); Directory.GetFiles(directory, "*.ctmp").ShouldBeEmpty();
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temp row encoding: caller scratch removes per-operation allocation and pins row sizes")]
    [InlineData(20, 35, 23)]
    [InlineData(50, 65, 53)]
    public void TryWrite_CallerScratch_ShouldAllocateZeroBytes(int characters, int rowBytes, int keyBytes)
    {
        // Arrange: the fits fixture assumed here is (INTEGER id, TEXT value), ordered by value.
        object?[] row = [1, new string('a', characters)];
        SqlTempRowCodec.TryGetLength(row, out int length).ShouldBeTrue(); length.ShouldBe(rowBytes);
        var scratch = new byte[length];
        SqlTempSortKeyCodec.TryEncode([row[1]], [new(false, true, Collation.Binary)], new byte[1], out var key).ShouldBeTrue(); key.Length.ShouldBe(keyBytes);
        const int operations = 10000;
        long before = long.MaxValue, after = long.MaxValue;
        int checksum = 0;
        for (int round = -1; round < 3; round++)
        {
            // Act: warm up, then minimum of three allocation rounds, never a duration threshold.
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < operations; i++) { SqlTempRowCodec.TryEncode(row, out var bytes); checksum += bytes.Length; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (round >= 0) { before = Math.Min(before, allocated); }
            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < operations; i++) { SqlTempRowCodec.TryWrite(row, scratch, out int written); checksum += written; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (round >= 0) { after = Math.Min(after, allocated); }
        }

        // Assert
        checksum.ShouldBeGreaterThan(0); before.ShouldBeGreaterThan(0); after.ShouldBe(0);
        _output.WriteLine($"TEMP_SIZE characters={characters}; row={rowBytes}; key={keyBytes}; sorter record={16 + rowBytes + keyBytes}; run stream={20 + rowBytes + keyBytes}; allocation/op={before / operations}->{after / operations} B (minimum of 3 rounds)");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temp rows: measured stream bytes and collector charges determine default quota capacity")]
    [InlineData(20, 'a', 78, 264)]
    [InlineData(50, 'a', 138, 392)]
    [InlineData(20, '界', 158, 264)]
    public void Complete_TextShapes_ShouldMeasureRunStreamAndMemoryCharge(int characters, char character, int streamBytes, long charge)
    {
        // Arrange: 8,160 rows make each expected stream length page-aligned, eliminating tail rounding.
        string directory = Path.Combine(Path.GetTempPath(), "sql-temp-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            object?[] row = [1, new string(character, characters)];
            SqlSelectMemory memory = default;
            memory.EntryBytes(row, [row[1]], out _).ShouldBe(charge);
            SqlTempRowCodec.TryEncode(row, out var value).ShouldBeTrue();
            SqlTempSortKeyCodec.TryEncode([row[1]], [new(false, true, Collation.Binary)], new byte[1], out var key).ShouldBeTrue();
            using var space = StorageTempSpace.Create(directory, 268435456, () => 0);
            using var statement = space.CreateStatement(268435456);
            using var sorter = StorageTempSorter.Create(statement, 4194304, 1024);

            // Act
            for (int i = 0; i < 8160; i++) { sorter.Append(key, value); }
            using var reader = sorter.Complete();

            // Assert: actual physical bytes / pages measure framing-inclusive stream bytes per row.
            sorter.RunsCreated.ShouldBe(1);
            statement.PhysicalBytes.ShouldBe(streamBytes * 8192L);
            long quotaRows = (268435456L / 8192 * 8160) / streamBytes;
            double multiple = quotaRows * charge / 67108864d;
            _output.WriteLine($"TEMP_CAPACITY text={characters}xU+{(int)character:X4}; row={value.Length}; key={key.Length}; stream/row={streamBytes}; charge/row={charge}; quota rows={quotaRows}; grant multiple={multiple:F4}");
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temp row text: UTF-8 tags reject malformed input and raw UTF-16 remains exact")]
    public void TryDecode_TextTags_ShouldPreserveUtf16AndRefuseInvalidUtf8()
    {
        // Arrange
        object?[] row = ["a\0界\U0001f600", new SqlCharacter("界  "), "\ud800", new SqlCharacter("\udfff  ")];

        // Act and assert
        RoundTrip(row);
        SqlTempRowCodec.TryEncode(["a"], out var valid).ShouldBeTrue();
        SqlTempRowCodec.TryEncode(["\ud800"], out var malformed).ShouldBeTrue();
        valid[5].ShouldNotBe(malformed[5]);
        malformed[5].ShouldBe((byte)DatabaseType.String);
        valid[10] = 255;
        SqlTempRowCodec.TryDecode(valid, out _).ShouldBeFalse();
        malformed[6] = 1; // A raw UTF-16 payload must have an even byte length.
        SqlTempRowCodec.TryDecode(malformed, out _).ShouldBeFalse();
    }
}

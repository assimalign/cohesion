using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: the 256-byte threshold preserves narrow reference charges (#1776)")]
    [InlineData(112, 1184L)]
    [InlineData(113, 512L)]
    public void Add_ThresholdOutputKeyAlias_ShouldPinExactBytes(int length, long expected)
    {
        // Arrange
        SqlSelectMemory.WideValueThreshold.ShouldBe(256);
        SqlSelectMemory.WideValueEntryBytes.ShouldBe(64);
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics);
        string value = new('x', length);

        // Act
        collector.Add([value], [value]);
        collector.Add([value], [value]);

        // Assert
        SqlHashJoinTable.ValueBytes(value).ShouldBe(length == 112 ? 248 : 256);
        metrics.PeakHashMemoryBytes.ShouldBe(expected);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: identity counts real separate payloads, including binary (#1776)")]
    [InlineData(false, false, 512L)]
    [InlineData(false, true, 832L)]
    [InlineData(true, false, 512L)]
    [InlineData(true, true, 832L)]
    public void Add_EqualCells_ShouldChargeDistinctObjects(bool binary, bool separate, long expected)
    {
        // Arrange
        object first = binary ? new byte[232] : new string('x', 113);
        object second = separate ? binary ? new byte[232] : new string('x', 113) : first;
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics);

        // Act
        collector.Add([first], [first]);
        collector.Add([second], [second]);

        // Assert
        metrics.PeakHashMemoryBytes.ShouldBe(expected);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: several columns share one payload and retain their slots (#1776)")]
    public void Add_RepeatedColumns_ShouldChargeOnlySlotsAfterFirstIdentity()
    {
        // Arrange
        string value = new('x', 113);
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics);

        // Act
        collector.Add([value, value, value], [value]);
        collector.Add([value, value, value], [value]);

        // Assert
        metrics.PeakHashMemoryBytes.ShouldBe(544);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: heap credit preserves shared and last holders (#1776)")]
    [InlineData(0, 576L, 896L)]
    [InlineData(1, 576L, 576L)]
    [InlineData(2, 896L, 896L)]
    public void Add_HeapReplacement_ShouldReserveOnlyNetGrowth(int shape, long initial, long expected)
    {
        // Arrange: numeric keys control replacement independently of payload spelling.
        string first = new('a', 113), second = new('b', 113), third = new('c', 113);
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics, 2, expected);
        collector.Add([first], [3]);
        collector.Add([shape == 2 ? second : first], [2]);
        metrics.HashMemoryBytes.ShouldBe(initial);

        // Act: shape 0 leaves another first holder; 1 replaces with the same cell;
        // 2 removes the last first holder and adds a new cell at an exact full grant.
        collector.Add([shape == 1 ? first : third], [1]);

        // Assert
        metrics.HashMemoryBytes.ShouldBe(expected);
        metrics.PeakHashMemoryBytes.ShouldBe(expected);
        collector.Finish().Count.ShouldBe(2);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: failed admission does not record a wide reference (#1776)")]
    public void Add_RefusedRow_ShouldLeaveIdentityCountsUntouched()
    {
        // Arrange
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics, null, 416);
        string first = new('a', 113), refused = new('b', 113);
        collector.Add([first], [first]);

        // Act
        var error = Should.Throw<DatabaseException>(() => collector.Add([refused], [refused]));

        // Assert: the same owner can still admit no extra payload after a refusal.
        error.Diagnostics[0].Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(416);
        metrics.PeakHashMemoryBytes.ShouldBe(416);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: sequential release preserves the remaining holder and removes dead identities (#1776)")]
    public void RemoveRow_SharedValue_ShouldReleaseAtLastHolder()
    {
        // Arrange
        SqlSelectMemory owner = default;
        string value = new('x', 113);
        object?[] first = [value], second = [value];
        owner.RowBytes(first, out _).ShouldBe(384);
        owner.AddRow(first);
        owner.RowBytes(second, out _).ShouldBe(64);
        owner.AddRow(second);

        // Act / Assert
        owner.RemoveRow(first).ShouldBe(64);
        owner.RemoveRow(second).ShouldBe(384);
        owner.HasWideCells.ShouldBeTrue(); // reuse capacity without retaining the key
        owner.RowBytes([value], out _).ShouldBe(384);
        owner.Clear();
        owner.HasWideCells.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: narrow ordered replacement allocates no dictionary or per-row object (#1776)")]
    public void Add_NarrowOrderedHeap_ShouldAllocateZeroPerReplacement()
    {
        // Arrange: prebuilt rows, a warm one-entry heap, and three measured rounds.
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics, 1);
        object?[] row = [1];
        object?[][] keys = Enumerable.Range(0, 40001).Select(i => new object?[] { 40001 - i }).ToArray();
        int position = 0;
        collector.Add(row, keys[position++]);
        Measure();

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure()); }

        // Assert
        minimum.ShouldBe(0);
        _output.WriteLine($"NARROW_ORDERED_ALLOCATION|{minimum}|operations=10000");
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);

        long Measure()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { collector.Add(row, keys[position++]); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: replaced heap and released child values collect during execution (#1776)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ReleasedWideValue_ShouldNotBePinned(bool forceBase, bool child)
    {
        // Arrange
        WeakReference? first = null;
        var heapMetrics = new SqlStatementMetrics();
        using var heap = Collector(heapMetrics, 1);
        int observed = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("wide-no-pinning");
        builder.Functions.Add(SqlScalarFunction.Create("make_value", (long k) =>
        {
            if (k == (child ? 2 : 1))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                first.ShouldNotBeNull().IsAlive.ShouldBeFalse();
                observed++;
            }
            string value = new((char)('a' + k), 2000);
            if (k == 3) { first = child ? new WeakReference(value) : ReplaceWideValue(heap); }
            return value;
        }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(3),(2),(1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO u VALUES(3),(2),(1)", cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string sql = child ? "SELECT EXISTS(SELECT make_value(u.k) FROM u WHERE u.k=t.k) FROM t ORDER BY k"
            : "SELECT make_value(k) AS p,k AS q FROM t ORDER BY q LIMIT 1";

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { _ = row.FieldCount; }

        // Assert
        observed.ShouldBe(1);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        heap.Dispose();
        heapMetrics.HashMemoryBytes.ShouldBe(0);
        heapMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    // Isolate producer locals from forced GC: Debug JIT can extend their lifetime.
    // The collector stays live in the SQL callback while its replaced value is collected.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReplaceWideValue(SqlRowCollector collector)
    {
        string value = new('z', 2000);
        var reference = new WeakReference(value);
        collector.Add([value], [2]);
        collector.Add([new string('y', 2000)], [1]);
        return reference;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: CHAR identity includes its padded and comparison payload (#1776)")]
    [InlineData(false, 800L)]
    [InlineData(true, 1408L)]
    public void Add_CharacterIdentity_ShouldChargeEachCellObject(bool separate, long expected)
    {
        // Arrange
        var first = new SqlCharacter(new string('x', 113));
        var second = separate ? new SqlCharacter(first.Value) : first;
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics);

        // Act
        collector.Add([first], [first]);
        collector.Add([second], [second]);

        // Assert
        metrics.PeakHashMemoryBytes.ShouldBe(expected);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    private static SqlRowCollector Collector(SqlStatementMetrics metrics, int? bound = null, long budget = 1_048_576)
        => new([new SqlBoundOrdering(new SqlBoundSlot(0), false, false, new SqlBoundCollation(Collation.Binary))], null,
            bound is null ? null : new SqlRowCount(null, "LIMIT", null, bound.Value), null, CancellationToken.None, metrics, budget);
}

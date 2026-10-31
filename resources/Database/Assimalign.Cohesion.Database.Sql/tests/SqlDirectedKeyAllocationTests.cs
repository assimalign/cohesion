#if !DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlDirectedKeyAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlDirectedKeyAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Directed keys: component workspace preserves base allocations at small and wide key widths (#1631)")]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task BuildIndexKeyUnchecked_AscendingIntegers_ShouldRetainBaseAllocation(int columns)
    {
        // Arrange: the pre-SQ-K1 builder needs no component boundary workspace.
        await using var engine = SqlDatabaseEngine.Create("directed-key-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] names = Enumerable.Range(0, columns).Select(i => "c" + i).ToArray();
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(" + string.Join(',', names.Select(name => name + " INT")) + ")");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(" + string.Join(',', names) + ")");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.TryGetIndex(table.ObjectId, "ix", out var metadata).ShouldBeTrue();
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        int[] ordinals = Enumerable.Range(0, columns).ToArray();
        object?[] values = ordinals.Cast<object?>().ToArray();
        var seek = new SqlIndexSeekPath(metadata, values, null, null);
        IndexKey BaselineKey()
        {
            var writer = new DatabaseKeyWriter();
            foreach (int ordinal in ordinals) { SqlRowCodec.AppendKeyValue(writer, DatabaseType.Int32, values[ordinal], Collation.Binary); }
            return new IndexKey(writer.ToArray());
        }
        IndexKeyRange BaselineRange()
        {
            var key = BaselineKey();
            byte[] successor = key.Encoded.ToArray();
            successor[^1]++;
            return new IndexKeyRange(key, new IndexKey(successor), true, false);
        }
        IndexKey ActualKey() => BuildKey(executor, table, ordinals, values, metadata.KeyOrders);
        IndexKeyRange ActualRange() => SqlIndexKeyEncoding.BuildSeekRange(table, seek, ordinals, Collation.Binary);
        ActualKey().ShouldBe(BaselineKey());
        ActualRange().ShouldBe(BaselineRange());
        Measure(ActualKey, 1_000);
        Measure(ActualRange, 1_000);

        // Act: warmed pool, current-thread bytes, minimum of three same-process rounds.
        long baseline = Minimum(BaselineKey), actual = Minimum(ActualKey);
        long baselineRange = Minimum(BaselineRange), actualRange = Minimum(ActualRange);

        // Assert: 1/64 columns cannot gain a buffer allocation; 65 cannot gain int[65].
        _output.WriteLine($"{columns} columns: key base={baseline / 1000d}, actual={actual / 1000d}; equality range base={baselineRange / 1000d}, actual={actualRange / 1000d} B/key");
        actual.ShouldBe(baseline);
        actualRange.ShouldBe(baselineRange);

        static long Minimum<T>(Func<T> build)
            => Enumerable.Range(0, 3).Select(_ => Measure(build, 1_000)).Min();
        static long Measure<T>(Func<T> build, int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++) { _ = build(); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildIndexKeyUnchecked")]
    private static extern IndexKey BuildKey(SqlPlanExecutor executor, SqlCatalogTable table, int[] ordinals, object?[] values,
        IReadOnlyList<SqlCatalogIndexKeyOrder>? orders);
}
#endif

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapMeasurement;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash ordinal source reader: marginal live readers include objects, buffers and decoded rows (#1789)")]
    [InlineData(32)]
    [InlineData(5800)]
    public async Task Read_HashOrdinalSource_ShouldBoundMarginalReaders(int width)
    {
        // Arrange: the actual hash iterator over a shared sealed run. Only cursor-local
        // objects grow between the two populations; statement/run ownership is shared.
        await using var engine = await Create("ordinal-reader-charge", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        ExecutorMetrics(executor) = new SqlStatementMetrics();
        using var statement = engine.TempSpace!.CreateStatement(1048576);
        var spill = NewReaderSpill(executor, session, [Collation.Binary, Collation.Binary], null, null, statement, stage: false);
        using var owner = (IDisposable)spill;
        object?[] row = [1, new string('x', width)];
        ValidateReaderKey(spill, row);
        SqlTempRowCodec.TryEncode(row, out var encoded).ShouldBeTrue();
        using var run = statement.CreateRun(); run.Append(encoded); run.Seal();
        var readers = new IEnumerator<object?[]>?[32];
        long live = long.MaxValue;
        long bound = StorageTempSpace.PageSize + run.MaximumRecordLength + SqlTempRowCodec.DecodedBytes(row) + 256;
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);

        for (int round = 1; round <= 3; round++)
        {
            long measured = Measure(pool, _output, nameof(Read_HashOrdinalSource_ShouldBoundMarginalReaders), $"width={width}|round={round}", readBaseline =>
            {
                try
                {
                    Fill(0, 8);
                    images.Drain(pool);
                    long small = readBaseline();
                    // Act
                    Fill(8, 32);
                    long measured = LiveHeap.Read() - small;
                    _output.WriteLine($"ORDINAL_READER_ROUND|width={width}|round={round}|live={measured}|bound={24 * bound}");
                    GC.KeepAlive(readers);
                    return measured;
                }
                finally
                {
                    foreach (var reader in readers) { reader?.Dispose(); }
                    Array.Clear(readers);
                }
            });
            live = Math.Min(live, measured);
        }
        // Assert: marginal bytes per reader against the unchanged per-reader bound.
        _output.WriteLine($"ORDINAL_READER|width={width}|live={live / 24d}|bound={bound}");
        (live / 24).ShouldBeLessThanOrEqualTo(bound);

        void Fill(int first, int last)
        {
            for (int i = first; i < last; i++)
            {
                var reader = ReadHashRows(spill, run, default).GetEnumerator();
                reader.MoveNext().ShouldBeTrue(); readers[i] = reader;
            }
        }
    }

    private const string hashSpillType = "Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+HashSpill, Assimalign.Cohesion.Database.Sql";

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(hashSpillType)]
    private static extern object NewReaderSpill(SqlPlanExecutor executor, SqlDatabaseSession owner, Collation[] collations,
        SqlGroupPlan? plan, SqlStatementContext? context, StorageTempStatement? shared, bool stage);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Validate")]
    private static extern void ValidateReaderKey([UnsafeAccessorType(hashSpillType)] object spill, object?[] row);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Read")]
    private static extern IEnumerable<object?[]> ReadHashRows([UnsafeAccessorType(hashSpillType)] object spill, StorageTempRun run, CancellationToken token);
}

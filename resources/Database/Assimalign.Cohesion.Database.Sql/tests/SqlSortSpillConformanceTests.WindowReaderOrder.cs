using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - The key reader releases its ledger and runs before the ordinal sort grant (#1789)")]
    public Task ExecuteAsync_WindowOrdinalSort_ShouldReleaseKeyReaderBeforeGrant()
        => CheckWindowReaderHandoffAsync(false);

    [Fact(DisplayName = "Cohesion Test [Sql] - The final reader releases its ledger and runs before the DISTINCT grant (#1789)")]
    public Task ExecuteAsync_WindowFinalDistinct_ShouldReleaseOrdinalReaderBeforeGrant()
        => CheckWindowReaderHandoffAsync(true);

    private async Task CheckWindowReaderHandoffAsync(bool final)
    {
        // Arrange: inspect the live ledger inside each successor's reservation.
        const long grant = 262144;
        await using var engine = await CreateWindowAsync("window-reader-order", grant);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session, 1600);
        StorageTempRun[] keyRuns = [], finalRuns = [];
        int ordinalGrants = 0, distinctGrants = 0, distinctRows = 0;
        bool ordinalPending = false, distinctPending = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "window-keys") { keyRuns = StatementRuns(engine.TempSpace!).ToArray(); }
            if (phase == "window-output") { ordinalPending = true; }
            if (phase == "window-ordinal") { finalRuns = StatementRuns(engine.TempSpace!).ToArray(); }
        };
        database.Definitions.ObserveSelectRetention = (phase, _, count) =>
        {
            if (phase == "distinct") { distinctPending = true; distinctRows = count; }
        };
        SqlStatementMetrics.AfterMemoryReservation = (metrics, bytes) =>
        {
            if (ordinalPending && !final)
            {
                ordinalPending = false; ordinalGrants++;
                metrics.PlainSelectMemoryBytes.ShouldBe(0, "the key reader must release before the ordinal grant");
                Closed(keyRuns);
            }
            if (distinctPending && final)
            {
                distinctPending = false; distinctGrants++;
                long projected = distinctRows * new SqlSelectMemory().RowBytes([1L], out _);
                // The callback runs after HashMemoryBytes grows, before the plain ledger grows.
                metrics.PlainSelectMemoryBytes.ShouldBe(projected, "only the projected rows may precede DISTINCT");
                metrics.HashMemoryBytes.ShouldBe(projected + bytes);
                Closed(finalRuns);
            }
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT DISTINCT ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t");
            // Assert: both checkpoints must have been exercised in this statement.
            (await Rows(result)).Length.ShouldBe(16);
            ordinalGrants.ShouldBe(final ? 0 : 1); distinctGrants.ShouldBe(final ? 1 : 0); distinctRows.ShouldBe(1600);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally
        {
            SqlStatementMetrics.AfterMemoryReservation = null; SqlRowCollector.ObserveSpill = null;
            database.Definitions.ObserveSelectRetention = null;
        }

        static void Closed(StorageTempRun[] runs)
        {
            runs.ShouldNotBeEmpty();
            foreach (var run in runs) { RunFile(run).SafeFileHandle.IsClosed.ShouldBeTrue(); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A consumed sorted window spool closes its reader workspace and runs before the next reservation (#1789)")]
    public async Task Read_WindowSortedSpool_ShouldCloseWorkspaceBeforeNextGrant()
    {
        // Arrange: retain the actual reader so a still-open exhausted reader is observable.
        const long grant = 262144;
        await using var engine = await CreateWindowAsync("window-sorted-reader-order", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var owner = new SqlWindowInputSpool(metrics, grant, session, CancellationToken.None);
        using var statement = owner.Statement;
        using var sorter = StorageTempSorter.Create(statement, 131072, 1024, 64);
        for (int i = 0; i < 128; i++)
        {
            SqlTempRowCodec.TryEncode([i], out var encoded).ShouldBeTrue();
            sorter.Append([(byte)i], encoded);
        }
        using var reader = sorter.Complete();
        var runs = Runs(reader).ToArray();
        using var spool = new SqlWindowInputSpool(metrics, grant, statement, reader,
            SqlTempRowCodec.DecodedBytes([0]), 128);
        int decoded = 0, nextGrants = 0;
        // Act: the consuming scope is still alive when the next grant is reserved.
        foreach (var row in spool.Read(CancellationToken.None))
        { metrics.PlainSelectMemoryBytes.ShouldBe(spool.ReadBytes); decoded++; }
        SqlStatementMetrics.AfterMemoryReservation = (ledger, _) =>
        {
            nextGrants++;
            // Assert: exhaustion alone closes runs in Storage; SQL must also close the reader.
            Workspace(reader).IsEmpty.ShouldBeTrue();
            foreach (var run in runs) { RunFile(run).SafeFileHandle.IsClosed.ShouldBeTrue(); }
            statement.PhysicalBytes.ShouldBe(0); ledger.PlainSelectMemoryBytes.ShouldBe(0);
        };
        try
        {
            metrics.ReservePlainSelectMemory(grant, grant);
            metrics.ReleasePlainSelectMemory(grant);
            decoded.ShouldBe(128); nextGrants.ShouldBe(1); metrics.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }
    }
}

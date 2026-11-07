using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered index DISTINCT matches unindexed spill and fitting reference (#1789)")]
    public async Task ExecuteAsync_OrderedIndexDistinct_ShouldKeepFirstOccurrenceWithoutRetainingAllRows()
    {
        // Arrange
        await using var reference = await Create("index-reference", 67108864);
        await using var scan = await Create("index-scan", 262144);
        await using var indexed = await Create("index-ordered", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await scan.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var c = await (await indexed.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a,b,c })
        {
            await session.ExecuteAsync("CREATE TABLE orders(tenant INT,status INT)");
            for (int first = 0; first < 4000; first += 200)
            { await session.ExecuteAsync("INSERT INTO orders VALUES" + string.Join(',', Enumerable.Range(first,200).Select(i => $"(1,{i%7})"))); }
        }
        await c.ExecuteAsync("CREATE INDEX i ON orders(tenant,status)");
        const string sql = "SELECT DISTINCT status FROM orders WHERE tenant=1 ORDER BY status";
        // Act
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
        await using var middle = (DatabaseResultSet)await b.ExecuteAsync(sql);
        await using var right = (DatabaseResultSet)await c.ExecuteAsync(sql);
        // Assert
        left.ShouldBeOfType<SqlMaterializedResultSet>(); middle.ShouldBeOfType<SqlSpilledResultSet>();
        c.LastStatementMetrics!.AccessPath.ShouldStartWith("ordered:");
        (await Rows(middle)).ShouldBe(await Rows(left)); (await Rows(right)).ShouldBe(await Rows(left));
        right.ShouldBeOfType<SqlMaterializedResultSet>();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Detached ordered scan fallback retains root spill permission (#1789)")]
    public async Task ExecuteAsync_DetachedOrderedIndex_ShouldSpillFallbackSort()
    {
        // Arrange
        await using var engine = await Create("ordered-fallback",262144);
        var database=await engine.CreateDatabaseAsync("db");
        await using var session=await database.CreateSessionAsync(); await Seed(session);
        await session.ExecuteAsync("CREATE INDEX ix ON t(k)");
        var plan=SqlOrderedIndexTests.Plan(database,"SELECT id,k FROM t ORDER BY k LIMIT 3000");
        plan.Access.ShouldBeOfType<SqlOrderedIndexPath>();
        await session.ExecuteAsync("DROP INDEX ix ON t");
        var transaction=await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            var context=new SqlStatementContext(transaction,database.Coordinator);
            var executor=new SqlPlanExecutor(database.DataStorage,database.Catalog,database.IndexManager,database.Definitions);
            // Act
            await using var result=(DatabaseResultSet)await executor.ExecuteRootAsync(plan,context,CancellationToken.None,session);
            // Assert
            result.ShouldBeOfType<SqlSpilledResultSet>(); (await Rows(result)).Length.ShouldBe(3000);
            context.Metrics.AccessPath.ShouldBe("scan");
        }
        finally { await database.Coordinator.RollbackAsync(transaction); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered aggregates never start a root spill that starves later group sorting (#1789)")]
    public async Task ExecuteAsync_OrderedAggregate_ShouldRefuseWithoutCreatingTempRuns()
    {
        // Arrange
        await using var engine = await Create("ordered-aggregate", 4194304);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session); int creates = 0;
        BeforeCreate(engine.TempSpace!, () => creates++);
        try
        {
            // Act / Assert
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "SELECT id%1000 AS k,STRING_AGG(note,',' ORDER BY id),reader_payload(id%1000) AS w FROM t GROUP BY id%1000 ORDER BY w,k"));
            error.Diagnostics[0].Code.ShouldBe("CS05067"); creates.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeCreate(engine.TempSpace!, null); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Receipt failure after spilled evaluation releases the undeliverable result (#1789)")]
    public async Task ExecuteAsync_ScriptReceiptFails_ShouldDisposeUnreturnedSpill()
    {
        // Arrange
        await using var engine = await Create("receipt-failure", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        bool injected = false;
        SetCommitting(database.DataStorage, _ => { injected = true; throw new InvalidOperationException("injected receipt bracket failure"); });
        var receipt = new SqlCatalogScriptPublication("spill",new string('A',64),1,null,database.Catalog.DdlGeneration,null,"");
        try
        {
            // Act / Assert
            await Should.ThrowAsync<Exception>(async () => await session.ExecuteScriptAsync(
                SqlQueryRequest.FromSql("SELECT id,k FROM t ORDER BY k"), receipt, CancellationToken.None));
            injected.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SetCommitting(database.DataStorage,null); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DisposeAsync faults asynchronously and clears its session owner (#1789)")]
    public async Task DisposeAsync_CloseFailure_ShouldReturnFaultedTaskAndDropOwner()
    {
        // Arrange
        await using var engine = await Create("async-dispose",262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        var result = (SqlSpilledResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k");
        var run = Runs(Sorted(result)!)[0]; RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle);
        // Act
        ValueTask disposal = default;
        Should.NotThrow(() => { disposal = result.DisposeAsync(); });
        // Assert
        (await Should.ThrowAsync<DatabaseException>(async () => await disposal)).Diagnostics[0].Code.ShouldBe("CK05005");
        ResultOwner(result).ShouldBeNull(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill analysis belongs on the sorting body beneath wrappers (#1789)")]
    [InlineData("WITH one AS (SELECT 1 AS n) SELECT id,k FROM t ORDER BY k")]
    [InlineData("SELECT id,k,(SELECT 1) AS scalar FROM t ORDER BY k")]
    public async Task ExecuteAsync_ExplainWrappedSpill_ShouldAnnotateSortingBody(string sql)
    {
        // Arrange
        await using var engine = await Create("wrapped-analysis",262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE " + sql);
        var rows = await Rows(result);
        // Assert
        var annotated = rows.Single(row => row.Cells.OfType<string>().Any(value => value.Contains("spilled_bytes=",StringComparison.Ordinal)));
        annotated.Cells.ShouldNotContain(value => Equals(value,"With") || Equals(value,"Subqueries"));
        annotated.Cells.OfType<string>().ShouldNotContain(value => value.StartsWith("; ",StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Abrupt stop with a live SQL spilled result sweeps on reopen and preserves committed rows (#1789)")]
    public async Task ExecuteAsync_AbruptStop_ShouldSweepAndReopenDatabase()
    {
        // Arrange
        string root = Path.Combine(_root,"abrupt");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"FormatProbeFixture","Assimalign.Cohesion.Database.Storage.FormatProbeFixture.dll"));
        start.ArgumentList.Add("sql-spill-stop"); start.ArgumentList.Add(root);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(ceiling.Token);
        try
        {
            (await process.StandardOutput.ReadLineAsync(ceiling.Token)).ShouldBe("SQL_SPILL_READY");
            Directory.GetFiles(Path.Combine(root,".cohesion-temp"),"*.ctmp").ShouldNotBeEmpty();
            // Act
            process.Kill(entireProcessTree:true); await process.WaitForExitAsync(ceiling.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree:true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        (await error).ShouldBeEmpty();
        await using var engine = SqlDatabaseEngine.Create("reopen-spill",new() { RootPath=root,StatementMemoryBudget=262144,CheckpointJournalSize=65536 });
        await using var session = await (await engine.OpenDatabaseAsync("db",ceiling.Token)).CreateSessionAsync(ceiling.Token);
        // Assert
        Directory.GetFiles(Path.Combine(root,".cohesion-temp"),"*.ctmp").ShouldBeEmpty();
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM t");
        (await Rows(result))[0].Cells[0].ShouldBe(4000L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Aggregated temporary failure in Transform preserves an explicit transaction (#1789)")]
    public async Task ExecuteAsync_AggregateTempFailureInTransform_ShouldKeepTransactionUsable()
    {
        // Arrange
        await using var engine=await Create("transform-aggregate",1048576);
        await using var session=await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        await session.ExecuteAsync("BEGIN"); bool injected=false;
        SqlRowCollector.ObserveSpill=phase =>
        {
            if(phase=="transform") { injected=true; throw new AggregateException(TempException("CK05005","injected aggregate temporary close failure")); }
        };
        try
        {
            // Act / Assert
            var failure=await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT stamp(id) FROM t WHERE id<1000 ORDER BY wide(id)"));
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05005");
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        }
        finally { SqlRowCollector.ObserveSpill=null; await session.ExecuteAsync("ROLLBACK"); }
        (await session.ExecuteAsync("SELECT id FROM t LIMIT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Collector cleanup cannot replace the original projection error (#1789)")]
    public async Task ExecuteAsync_ErrorWhileCollectorUnwinds_ShouldPreserveOriginalDiagnostic()
    {
        // Arrange
        await using var reference = await Create("unwind-reference",67108864);
        await using var engine = await Create("unwind-error",262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        const string sql = "SELECT CASE WHEN id=2500 THEN 1/0 ELSE id END AS v FROM t ORDER BY v";
        var expected = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
        bool injected=false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase!="arena" || injected) { return; }
            var run=StatementRuns(engine.TempSpace!).First(); RunFile(run)=new FailedCloseStream(RunFile(run).SafeFileHandle); injected=true;
        };
        try
        {
            // Act
            var actual = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));
            // Assert
            injected.ShouldBeTrue(); actual.Diagnostics.Select(d => d.Code).ShouldBe(expected.Diagnostics.Select(d => d.Code));
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill=null; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method,Name="set_TransactionCommitting")]
    private static extern void SetCommitting(global::Assimalign.Cohesion.Database.Storage.Storage storage,Action<StorageTransaction>? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_owner")]
    private static extern ref SqlDatabaseSession? ResultOwner(SqlSpilledResultSet result);
}

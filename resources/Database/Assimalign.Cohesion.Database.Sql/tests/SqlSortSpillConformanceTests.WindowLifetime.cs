using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - One oversized window partition keeps CS05079 until paged access (#1789)")]
    [InlineData("PARTITION BY 0 ORDER BY id")]
    [InlineData("")]
    public async Task ExecuteAsync_WindowOversizedPartition_ShouldKeepRefusal(string window)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-large-partition", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session,8000);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SELECT id,ROW_NUMBER() OVER({window}) FROM t"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05079");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Closing the session or engine during a spilled window releases unregistered stage runs (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_WindowOwnerStops_ShouldReleaseStageRuns(bool engineClose)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-owner-close", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        Task closing = Task.CompletedTask; bool exercised = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "window-keys" || exercised) { return; }
            exercised = true; closing = engineClose ? engine.DisposeAsync().AsTask() : session.DisposeAsync().AsTask();
        };
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(async () =>
            { await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t"); });
            await closing.WaitAsync(TimeSpan.FromSeconds(30));
            // Assert
            exercised.ShouldBeTrue(); failure.ShouldNotBeNull();
            (failure is OperationCanceledException or ObjectDisposedException).ShouldBeTrue();
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Damaged window sorted input raises located CK05004 and releases every stage owner (#1789)")]
    public async Task ExecuteAsync_WindowRunDamage_ShouldKeepDatabaseOnline()
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-damage", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        bool armed = false, injected = false;
        SqlRowCollector.ObserveSpill = phase => { if (phase == "window-keys") { armed = true; } };
        BeforeRead(engine.TempSpace!, () =>
        {
            if (!armed || injected) { return; }
            var run = StatementRuns(engine.TempSpace!).First(run => run.IsSealed && run.RecordCount > 0);
            System.IO.RandomAccess.Write(Handle(run), new byte[] { 255 }, 0); injected = true;
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t"));
            // Assert
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05004"); failure.Diagnostics[0].Start.ShouldNotBeNull();
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally { SqlRowCollector.ObserveSpill = null; BeforeRead(engine.TempSpace!, null); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window stage runs consume no client result slots and ANALYZE reports their spills (#1789)")]
    [InlineData("SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) FROM t LIMIT 1")]
    [InlineData("SELECT t.id,ROW_NUMBER() OVER(PARTITION BY t.id%100 ORDER BY t.k) FROM t CROSS JOIN u LIMIT 1")]
    [InlineData("SELECT id,COUNT(*),ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY COUNT(*)) FROM t GROUP BY id LIMIT 1")]
    public async Task ExecuteAsync_WindowStageRuns_ShouldNotUseResultSlots(string sql)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-slots", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        var held = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < SqlSpillLifetime.MaximumResultsPerSession; i++)
            { held.Add((DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY id DESC")); }
            long before = TempBytes(engine.TempSpace!);
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql))
            { (await Rows(result)).ShouldHaveSingleItem(); result.ShouldBeOfType<SqlMaterializedResultSet>(); }
            await using var explained = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE " + sql);
            // Assert
            (await Rows(explained)).Any(row => Equals(row.Cells[2], "WindowAgg") && row.Cells.Any(cell => cell is string detail && detail.Contains("spilled_bytes=",StringComparison.Ordinal) && detail.Contains("spill_runs=",StringComparison.Ordinal))).ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(before);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { foreach (var result in held) { await result.DisposeAsync(); } }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window spill cancellation releases input, keys, partition output and ordinal runs (#1789)")]
    [InlineData("window-input")] [InlineData("window-keys")] [InlineData("window-output")] [InlineData("window-ordinal")]
    [InlineData("partition")] [InlineData("read")] [InlineData("write")]
    public async Task ExecuteAsync_WindowCancellation_ShouldReleaseStageRuns(string point)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        using var cancellation = new CancellationTokenSource(); bool exercised = false, sorting = false;
        void Cancel() { exercised = true; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (point == "window-keys" && phase == "window-key-sorting" || point == "window-ordinal" && phase == "window-ordinal-sorting")
            { sorting = true; }
            if (point is not ("window-keys" or "window-ordinal") && phase == point) { Cancel(); }
        };
        SqlRowCollector.ObserveBuffer = (phase, _) =>
        { if (sorting && phase == "window-decoded" || point == "partition" && phase == "window-partition") { Cancel(); } };
        if (point == "read") { BeforeRead(engine.TempSpace!, Cancel); }
        if (point == "write") { BeforeWrite(engine.TempSpace!, Cancel); }
        try
        {
            // Act / Assert
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "SELECT id,LAG(k) OVER(PARTITION BY id%100 ORDER BY id) FROM t", cancellationToken: cancellation.Token));
            exercised.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlRowCollector.ObserveBuffer = null; BeforeRead(engine.TempSpace!, null); BeforeWrite(engine.TempSpace!, null); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window spill keeps the first key/argument error and the exact visit-cap refusal (#1789)")]
    [InlineData("SELECT ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY fails_on(id)),LAG(stamp(id)) OVER(PARTITION BY id%73 ORDER BY id) FROM t", false)]
    [InlineData("SELECT LAG(fails_on(id)) OVER(PARTITION BY id%100 ORDER BY id),ROW_NUMBER() OVER(PARTITION BY id%73 ORDER BY stamp(id)) FROM t", false)]
    [InlineData("SELECT window_sum(id) OVER(PARTITION BY id%100 ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t", true)]
    public async Task ExecuteAsync_WindowFailure_ShouldMatchFittingFailure(string sql, bool workCap)
    {
        // Arrange
        var outcomes = new List<(string?, string, long, long, string)>();
        for (int index = 0; index < 2; index++)
        {
            var calls = new List<long>();
            var builder = SqlDatabaseEngine.CreateBuilder("window-failure");
            builder.Options.TempDirectory = System.IO.Path.Combine(_root, "window-failure-" + index);
            builder.Options.StatementMemoryBudget = index == 0 ? 67108864 : 262144;
            builder.Options.StatementTempSpaceQuota = 134217728;
            builder.Functions.Add(SqlScalarFunction.Create("stamp", (long v) => { calls.Add(v); return v; }));
            builder.Functions.Add(SqlScalarFunction.Create("fails_on", (long v) =>
            { calls.Add(-v - 1); if (v == 317) { throw new InvalidOperationException("window argument failed"); } return v; }));
            builder.Functions.Add(SqlAggregateFunction.Create<long,int,long>("window_sum", () => 0, (s,v) => s+v, s => s));
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await Seed(session, workCap ? 15000 : 4000);
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            // Assert
            var metrics = session.LastStatementMetrics!;
            outcomes.Add((failure.Diagnostics[0].Code, failure.Message, metrics.RecordsExamined, metrics.WindowFrameWorkVisits, string.Join(',', calls)));
            if (workCap) { failure.Diagnostics[0].Code.ShouldBe("CS05084"); metrics.WindowFrameWorkVisits.ShouldBe(1000000); }
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        outcomes[1].ShouldBe(outcomes[0]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window stage close failure preserves the computed result and releases every file (#1789)")]
    public async Task ExecuteAsync_WindowConsumedCloseFailure_ShouldKeepResult()
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-close", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        bool injected = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "window-ordinal" || injected) { return; }
            foreach (var run in StatementRuns(engine.TempSpace!).Where(run => run.IsSealed).ToArray())
            { RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle); injected = true; }
        };
        try
        {
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t"))
            {
                // Assert
                var rows = await Rows(result); rows.Length.ShouldBe(4000);
                rows.Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0,4000).Cast<object?>());
            }
            injected.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }
}

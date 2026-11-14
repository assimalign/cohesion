using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Consumed join spool close failure preserves computed output and releases the session slot (#1789)")]
    public async Task ExecuteAsync_ConsumedJoinCloseFailure_ShouldKeepResultAndReleaseSlot()
    {
        // Arrange
        await using var engine = await Create("join-close-success", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Verbose);
        StorageTempRun? matches = null;
        bool injected = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "output-drained") { matches ??= StatementRuns(engine.TempSpace!).First(); }
            if (phase == "output-spool" && matches is not null)
            { RunFile(matches) = new FailedCloseStream(RunFile(matches).SafeFileHandle); injected = true; }
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT stamp(t.id),RPAD(t.note,1024,'x') FROM t CROSS JOIN u");
            injected.ShouldBeTrue();
            (await Rows(result)).Length.ShouldBe(4000);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
        // Assert: all eight slots are free after computed-result disposal.
        events.Events.Count(e => e.EventId == 22 && e.Payload!.Contains("CK05005")).ShouldBe(1);
        var outputs = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t"); outputs.Add(result);
                (await Rows(result)).Length.ShouldBe(4000);
            }
        }
        finally { foreach (var result in outputs) { await result.DisposeAsync(); } }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS copy credit closes the fitting source band without a temp directory (#1789, #1838)")]
    public async Task ExecuteAsync_CopyCreditWithoutTemp_ShouldCompleteBand()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-no-temp", new() { StatementMemoryBudget = 262144 });
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 113).Select(i => $"({i},'{new string('x',1024)}')")));
        // Act / Assert
        for (int count = 86; count <= 113; count++)
        {
            (await session.ExecuteAsync($"CREATE TABLE a AS SELECT * FROM t WHERE id<{count}")).AffectedCount.ShouldBe(count);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            await session.ExecuteAsync("DROP TABLE a");
        }
        engine.TempSpace.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Nested spilled readers reserve on the live outer statement ledger (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NestedReader_ShouldChargeLiveLedger(bool grouping)
    {
        // Arrange
        await using var engine = await Create("nested-ledger", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        session.Database.Definitions.ForceSelectRetentionBasePath = grouping;
        SqlStatementMetrics? metrics = null; long minimum = long.MaxValue; int reads = 0;
        SqlStatementMetrics.AfterMemoryReservation = (current, _) => metrics = current;
        BeforeRead(engine.TempSpace!, () => { reads++; minimum = Math.Min(minimum, metrics!.HashMemoryBytes); });
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(grouping
                ? "SELECT COUNT(*) FROM t" : "SELECT EXISTS (SELECT id FROM t ORDER BY k)");
            await Rows(result);
            // Assert
            reads.ShouldBeGreaterThan(0); minimum.ShouldBeGreaterThanOrEqualTo(8192);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; BeforeRead(engine.TempSpace!, null); }
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Nested sorted caches complete with fitting rows and release all charges (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SortedRelationCache_ShouldCompleteAndRelease(bool cte)
    {
        // Arrange
        await using var fitting = await Create("sorted-cache-fitting", 67108864);
        await using var spilling = await Create("sorted-cache-spill", 262144);
        await using var a = await (await fitting.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilling.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        string sql = cte ? "WITH d AS (SELECT id,k FROM t ORDER BY k) SELECT COUNT(*) FROM d"
            : "SELECT COUNT(*) FROM (SELECT id,k FROM t ORDER BY k) d";
        // Act
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        // Assert
        (await Rows(actual)).ShouldBe(await Rows(expected));
        SqlSortSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
        b.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        await actual.DisposeAsync(); TempBytes(spilling.TempSpace!).ShouldBe(0); Files(spilling).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting CTE cache-copy spill releases source charges on success, cancellation and quota refusal (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_FittingCacheCopy_ShouldDrainAndRelease(int exit)
    {
        // Arrange: 1,500 two-INT output rows fit the body, but not its cache copy.
        await using var engine = await Create("fitting-cache-copy", 262144, quota: exit == 2 ? 0 : 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, 1500);
        using var cancellation = new CancellationTokenSource();
        int creates = 0, drained = 0, appends = 0;
        BeforeCreate(engine.TempSpace!, () => creates++);
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "output-drained") { drained++; }
            if (phase == "output-append") { appends++; if (exit == 1) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } }
        };
        try
        {
            const string sql = "WITH d AS (SELECT id,k FROM t) SELECT COUNT(*) FROM d";
            // Act / Assert
            if (exit == 0)
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                (await Rows(result)).ShouldHaveSingleItem().Cells[0].ShouldBe(1500L);
                SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull().Runs.ShouldBe(1);
                appends.ShouldBe(1500);
            }
            else if (exit == 1)
            {
                await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
                appends.ShouldBe(1);
            }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
                failure.Diagnostics[0].Code.ShouldBe("CK05002");
            }
            creates.ShouldBe(1); drained.ShouldBe(0); // SpillMaterialized, not body output Add
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeCreate(engine.TempSpace!, null); SqlRowCollector.ObserveSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Active relation caches count toward the session spill cap and explain refusal (#1789)")]
    public async Task ExecuteAsync_RelationCacheCap_ShouldNameActiveCaches()
    {
        // Arrange
        await using var engine = await Create("cache-cap", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        string definitions = string.Join(',', Enumerable.Range(0, 9).Select(i => $"c{i} AS (SELECT id,k FROM t)"));
        string joins = string.Join(" CROSS JOIN ", Enumerable.Range(0, 9).Select(i => "c" + i));
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("WITH " + definitions + " SELECT COUNT(*) FROM " + joins));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067"); failure.Message.ShouldContain("8 active relation caches", Case.Sensitive);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        await using var next = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t");
        (await Rows(next)).Length.ShouldBe(4000);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlated subqueries re-read a spilled CTE for each outer row (#1789)")]
    public async Task ExecuteAsync_CorrelatedSpilledCache_ShouldRescanEachOuterRow()
    {
        // Arrange
        await using var engine = await Create("cache-rescan", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        await session.ExecuteAsync("INSERT INTO u VALUES(2)");
        var scans = new List<int>(); int reads = 0;
        BeforeRead(engine.TempSpace!, () => reads++);
        try
        {
            foreach (int outerRows in new[] { 1, 2 })
            {
                reads = 0;
                // Act
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                    "WITH d AS (SELECT id,k FROM t) SELECT u.id,(SELECT COUNT(*) FROM d WHERE d.k=u.id) FROM u WHERE u.id<=" + outerRows);
                // Assert
                (await Rows(result)).Length.ShouldBe(outerRows); scans.Add(reads);
                session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                SqlSortSpillMetrics.For(session.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBe(1);
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            }
            scans[0].ShouldBeGreaterThan(0); scans[1].ShouldBe(2 * scans[0]);
            _output.WriteLine($"CACHE_RESCAN|oneOuter={scans[0]}|twoOuter={scans[1]}");
        }
        finally { BeforeRead(engine.TempSpace!, null); }
    }
}

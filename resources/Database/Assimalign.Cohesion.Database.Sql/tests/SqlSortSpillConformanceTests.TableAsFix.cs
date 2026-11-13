using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Whole fitting CTAS retains fresh source and batch charges on the temp route (#1789)")]
    public async Task ExecuteAsync_TableAsFittingFrame_ShouldKeepAllocationAndChargePins()
    {
        // Arrange: measure the entire synchronous CTAS frame, including publication.
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-fitting-frame");
        builder.Options.TempDirectory = Path.Combine(_root, "fitting-frame"); builder.Options.StatementMemoryBudget = 1048576;
        await using var engine = await builder.BuildAsync(); var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session, 100);
        const string sql = "CREATE TABLE archive AS SELECT id,k,note FROM t ORDER BY k";
        long minimum = long.MaxValue; var peaks = new List<long>(); var admitted = new List<long>();
        long epoch = database.TableReclamation.EnterStatement();
        try
        {
            for (int i = 0; i < 10; i++) { await session.ExecuteAsync(sql); await session.ExecuteAsync("DROP TABLE archive"); }
            SqlPlanExecutor.TableAsSourceAdmitted = admitted.Add;
            // Act
            for (int round = 0; round < 3; round++)
            {
                long bytes = 0;
                for (int i = 0; i < 20; i++)
                {
                    long start = GC.GetAllocatedBytesForCurrentThread(); var pending = session.ExecuteAsync(sql);
                    bool synchronous = pending.IsCompletedSuccessfully; var result = await pending;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - start;
                    synchronous.ShouldBeTrue(); result.AffectedCount.ShouldBe(100);
                    peaks.Add(session.LastStatementMetrics!.PeakHashMemoryBytes);
                    await session.ExecuteAsync("DROP TABLE archive");
                }
                minimum = Math.Min(minimum, bytes / 20);
            }
            // Assert
            admitted.ShouldAllBe(bytes => bytes == 17472); peaks.ShouldAllBe(peak => peak == 23744);
            _output.WriteLine($"CTAS_FITTING_FRAME|bytes={minimum}|admitted=17472|peak=23744");
            // The simple fitting form measured 141,736-142,016 B. This full
            // publication loop measured 194,329 B at 57500fb3f and 194,398 B at
            // the batch head. A 1.5x simple-form bound covers that frame while
            // rejecting the review's extra 1 MiB per CTAS on every configuration.
            minimum.ShouldBeLessThanOrEqualTo(142016L * 3 / 2);
        }
        finally { SqlPlanExecutor.TableAsSourceAdmitted = null; database.TableReclamation.ExitStatement(epoch); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Oversized CTAS records refuse in the codec before batch admission at both grants (#1789)")]
    [InlineData(393216)]
    [InlineData(524288)]
    public async Task ExecuteAsync_TableAsOversizedRecord_ShouldKeepPageRefusal(int width)
    {
        // Arrange: the review's seven RPAD columns, 60 rows and default grant/quota.
        string query = "SELECT id," + string.Join(',', Enumerable.Range(0, 7).Select(i =>
            $"RPAD(CAST(id AS TEXT),{width},'{"xyzpqrs"[i]}') AS c{i + 1}")) + " FROM t ORDER BY id";
        foreach (long grant in new[] { 1073741824L, 67108864L })
        {
            var builder = SqlDatabaseEngine.CreateBuilder("ctas-oversized");
            builder.Options.RootPath = Path.Combine(_root, "oversized-" + grant);
            builder.Options.StatementMemoryBudget = grant;
            builder.Options.BufferPoolCapacity = 1048576;
            await using var engine = await builder.BuildAsync();
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(); await Seed(session, 60);
            long baseline = 0, peak = 0; int encoded = 0, decoded = 0, sampling = 0, samples = 0;
            using var started = new ManualResetEventSlim(); Thread? sampler = null;
            SqlPlanExecutor.TableAsSourceAdmitted = _ =>
            {
                baseline = LiveHeap.Read(); Volatile.Write(ref sampling, 1);
                sampler = new Thread(() =>
                {
                    while (Volatile.Read(ref sampling) != 0)
                    { peak = Math.Max(peak, LiveHeap.Read() - baseline); samples++; started.Set(); }
                }) { IsBackground = true };
                sampler.Start(); started.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            };
            SqlPlanExecutor.TableAsSourceReading = (_, _) => decoded++;
            SqlPlanExecutor.TableAsRecordEncoded = _ => encoded++;
            try
            {
                // Act
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS " + query));
                Volatile.Write(ref sampling, 0); if (sampler is not null) { sampler.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue(); }
                _output.WriteLine($"CTAS_OVERSIZED|width={width}|grant={grant}|peak={peak}|decoded={decoded}|encoded={encoded}|samples={samples}|code={failure.Diagnostics[0].Code}");
                // Assert
                failure.Diagnostics[0].Code.ShouldBe("CS05042"); encoded.ShouldBe(0); samples.ShouldBeGreaterThan(0);
                database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse(); database.IsOffline.ShouldBeFalse();
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
                if (grant == 67108864) { decoded.ShouldBe(1); peak.ShouldBeLessThanOrEqualTo(grant + 553240); }
            }
            finally { Volatile.Write(ref sampling, 0); if (sampler is not null) { sampler.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue(); } SqlPlanExecutor.TableAsSourceAdmitted = null; SqlPlanExecutor.TableAsSourceReading = null; SqlPlanExecutor.TableAsRecordEncoded = null; }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Completed CTAS survives counted temp close failure and next-open sweep (#1789)")]
    public async Task ExecuteAsync_TableAsCompletedCloseFailure_ShouldPublishEveryRow()
    {
        // Arrange
        string root = Path.Combine(_root, "completed-close"), leftover = "";
        bool injected = false, closed = false; int copied = 0;
        StorageTempRun? lastRun = null;
        await using (var engine = SqlDatabaseEngine.Create("completed-close", new() { RootPath = root, StatementMemoryBudget = 262144 }))
        {
            var database = await engine.CreateDatabaseAsync("db"); await using var session = await database.CreateSessionAsync(); await Seed(session);
            using var events = new EventSourceRecorder(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Verbose);
            SqlPlanExecutor.TableAsSourceReading = (result, _) =>
            {
                lastRun ??= Runs(Sorted(result)!)[0];
            };
            SqlPlanExecutor.TableAsStage = (phase, _) =>
            {
                if (phase != "Copied" || ++copied != 63) { return; }
                injected = true;
                // Inject after the last flush; this replacement leaves a real sweep candidate.
                var run = lastRun!; var original = RunFile(run); leftover = original.Name;
                original.Dispose();
                RunFile(run) = new TableAsFailedCloseStream(File.OpenHandle(leftover, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete), () => closed = true);
            };
            try
            {
                // Act
                (await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id")).AffectedCount.ShouldBe(4000);
                // Assert
                injected.ShouldBeTrue(); closed.ShouldBeTrue(); copied.ShouldBe(63);
                events.Events.Count(e => e.EventId == 22 && e.Payload!.Contains("CK05005")).ShouldBe(1);
                database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeTrue();
                (await TableAsPhysicalRowsAsync(session, "archive")).Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 4000).Cast<object?>());
                TempBytes(engine.TempSpace!).ShouldBe(0);
                Files(engine).ShouldBe(1);
                File.Exists(leftover).ShouldBeTrue(); database.IsOffline.ShouldBeFalse();
                _output.WriteLine($"CTAS_COMPLETED_CLOSE|rows=4000|brackets={copied}|closeFailures=1|tempBytes={TempBytes(engine.TempSpace!)}|leftoverFiles={Files(engine)}");
            }
            finally { SqlPlanExecutor.TableAsSourceReading = null; SqlPlanExecutor.TableAsStage = null; }
        }
        await using var reopened = SqlDatabaseEngine.Create("completed-close-reopen", new() { RootPath = root });
        await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        File.Exists(leftover).ShouldBeFalse();
        Files(reopened).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS fitting-source refusal window stays unchanged pending follow-up (#1789)")]
    [InlineData(262144L, 1024, 80, 120)]
    [InlineData(67108864L, 7000, 4690, 4710)]
    public async Task ExecuteAsync_TableAsFittingWindow_ShouldPinBothSides(long grant, int width, int first, int last)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-window");
        builder.Options.RootPath = Path.Combine(_root, "window"); builder.Options.StatementMemoryBudget = grant;
        await using var engine = await builder.BuildAsync(); var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
        for (int start = 0; start < last; start += 100)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(100, last - start)).Select(i => $"({i},'{new string('x', width)}')"))); }
        var refused = new List<int>(); var spilled = new List<int>(); var fitting = new List<int>();
        // Act
        for (int n = first; n <= last; n++)
        {
            var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync($"CREATE TABLE archive AS SELECT * FROM t WHERE id<{n} ORDER BY id"));
            if (failure is not null)
            {
                failure.ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CS05067"); refused.Add(n);
                database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            }
            else
            {
                (SqlSortSpillMetrics.For(session.LastStatementMetrics!) is null ? fitting : spilled).Add(n);
                await session.ExecuteAsync("DROP TABLE archive");
            }
        }
        // Assert
        fitting.ShouldNotBeEmpty(); refused.ShouldNotBeEmpty(); spilled.ShouldNotBeEmpty();
        refused.ShouldBe(Enumerable.Range(refused[0], refused[^1] - refused[0] + 1));
        fitting[^1].ShouldBe(grant == 262144 ? 85 : 4696);
        refused[0].ShouldBe(grant == 262144 ? 86 : 4697); refused[^1].ShouldBe(grant == 262144 ? 113 : 4707);
        spilled[0].ShouldBe(grant == 262144 ? 114 : 4708);
        _output.WriteLine($"CTAS_WINDOW|grant={grant}|width={width}|fitsThrough={fitting[^1]}|refuses={refused[0]}..{refused[^1]}|spillsFrom={spilled[0]}");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered and ordered-index CTAS refuse until SQ-L4 while a sort spills (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TableAsUnspooledSource_ShouldRefuseUntilSqL4(bool index)
    {
        // Arrange: ten grants of storable rows under the review's three statement shapes.
        await using var engine = await Create("ctas-unspooled", 262144);
        var database = await engine.CreateDatabaseAsync("db"); await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT" + (index ? " PRIMARY KEY" : "") + ",note TEXT)");
        for (int first = 0; first < 1200; first += 100)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 100).Select(i => $"({i},'{new string('x', 1024)}')"))); }
        string query = index ? "SELECT * FROM t WHERE id >= 0 ORDER BY id" : "SELECT * FROM t";
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS " + query));
        failure.Diagnostics[0].Code.ShouldBe("CS05067"); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        _output.WriteLine($"CTAS_UNSPOOLED|index={index}|code={failure.Diagnostics[0].Code}|path={session.LastStatementMetrics!.AccessPath}");
        if (!index)
        {
            (await session.ExecuteAsync("CREATE TABLE sorted AS SELECT * FROM t ORDER BY id")).AffectedCount.ShouldBe(1200);
            SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
        }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Default-grant unordered and ordered-index CTAS refuse until SQ-L4; ten-grant sort completes (#1789)")]
    public async Task ExecuteAsync_TableAsDefaultGrantUnspooledSources_ShouldRefuseUntilSqL4()
    {
        // Arrange: default memory and temp quotas, ten grants of the review's storable shape.
        const int count = 10 * 67108864 / 232;
        await using var engine = SqlDatabaseEngine.Create("ctas-default-unspooled", new() { RootPath = Path.Combine(_root, "default-unspooled") });
        var database = await engine.CreateDatabaseAsync("db"); await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,note TEXT COLLATE binary)");
        for (int first = 0; first < count; first += 1000)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(1000, count - first)).Select(i => $"({i},'xxxxxxxxxxxxxxxxxxxx')"))); }
        // Act / Assert
        foreach (var statement in new[] { "CREATE TABLE a AS SELECT * FROM t", "CREATE TABLE c AS SELECT * FROM t WHERE id >= 0 ORDER BY id" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement));
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
            _output.WriteLine($"CTAS_DEFAULT_UNSPOOLED|sql={statement}|rows={count}|code={failure.Diagnostics[0].Code}|path={session.LastStatementMetrics!.AccessPath}");
        }
        (await session.ExecuteAsync("CREATE TABLE b AS SELECT * FROM t ORDER BY id")).AffectedCount.ShouldBe(count);
        SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
        database.Catalog.TryGetTable("dbo", "a", out _).ShouldBeFalse(); database.Catalog.TryGetTable("dbo", "c", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "b", out _).ShouldBeTrue();
        _output.WriteLine($"CTAS_DEFAULT_SORTED|rows={count}|status=Success|grant=67108864");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS binary cells, deferred collations and mixed key families retain source refusal (#1789)")]
    [InlineData("SELECT id,app_binary(id) AS cell FROM t ORDER BY id", "CS05067")]
    [InlineData("SELECT id FROM t ORDER BY CASE WHEN id%2=0 THEN id ELSE note END", "CS02080")]
    [InlineData("SELECT id FROM t ORDER BY unproved_result(id,CAST(NULL AS BIGINT))", "CS05067")]
    [InlineData("SELECT id FROM t ORDER BY note IN (SELECT 'a' COLLATE binary)", "CS05067")]
    public async Task ExecuteAsync_TableAsCodecRefusal_ShouldLeaveNoTable(string query, string code)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-codec-refusal");
        builder.Options.RootPath = Path.Combine(_root, "codec"); builder.Options.StatementMemoryBudget = 262144;
        builder.Functions.Add(SqlScalarFunction.Create("app_binary", (long value) => new byte[] { (byte)value }));
        builder.Functions.Add(new HashUnprovedResultFunction(true, text: true));
        await using var engine = await builder.BuildAsync(); var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS " + query));
        failure.Diagnostics[0].Code.ShouldBe(code); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS deferred FILTER subquery refuses CS05067 without leaving a table (#1789)")]
    public async Task ExecuteAsync_TableAsDeferredSubquery_ShouldRefuseBeforeSpill()
    {
        // Arrange: groups fit; their wide sorted output crosses the source grant.
        const string query = "CREATE TABLE archive AS SELECT id%80 AS key,COUNT(*) FILTER(WHERE (SELECT u.id FROM u)=1) AS n," +
            "RPAD(CAST(id%80 AS TEXT),4000,'x') AS note FROM t GROUP BY id%80 ORDER BY key";
        foreach (long grant in new[] { 262144L, 67108864L })
        {
            await using var engine = await Create("ctas-deferred-" + grant, grant);
            var database = await engine.CreateDatabaseAsync("db"); await using var session = await database.CreateSessionAsync(); await Seed(session);
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions)
                .Plan(SqlQueryRequest.FromSql(query).Statement.SqlExpression).ShouldBeOfType<SqlCreateTableAsPlan>();
            plan.Source.ShouldBeOfType<SqlSubqueryPlan>().Queries.ShouldContain(child => child.IsDeferred && !child.IsCorrelated);
            // Act / Assert
            if (grant == 262144)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query));
                failure.Diagnostics[0].Code.ShouldBe("CS05067"); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            }
            else { (await session.ExecuteAsync(query)).AffectedCount.ShouldBe(80); }
            SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); database.IsOffline.ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS ninth spill refuses CS05067 and preserves eight live readable results (#1789)")]
    public async Task ExecuteAsync_TableAsEightLiveResults_ShouldRefuseWithoutAffectingReaders()
    {
        // Arrange
        await using var engine = await Create("ctas-eight", 262144); var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        var results = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < 8; i++) { results.Add((await session.ExecuteAsync("SELECT id,k FROM t ORDER BY id")).ShouldBeOfType<SqlSpilledResultSet>()); }
            long live = TempBytes(engine.TempSpace!);
            // Act / Assert
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS SELECT id,k FROM t ORDER BY id"));
            failure.Diagnostics[0].Code.ShouldBe("CS05067"); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            TempBytes(engine.TempSpace!).ShouldBe(live);
            foreach (var result in results) { (await Rows(result)).Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 4000).Cast<object?>()); }
        }
        finally { foreach (var result in results) { await result.DisposeAsync(); } }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }
}

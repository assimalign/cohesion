using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS spill cancellation survives a simultaneous temporary close failure (#1789)")]
    public async Task ExecuteAsync_TableAsCanceledAndCloseFails_ShouldPreserveCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = await Create("ctas-close-cancel", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        bool injected = false, closed = false;
        SqlPlanExecutor.TableAsSourceReading = (result, _) =>
        {
            if (injected) { return; }
            injected = true;
            var run = Runs(Sorted(result)!)[0]; RunFile(run) = new TableAsFailedCloseStream(RunFile(run).SafeFileHandle, () => closed = true);
            cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
        };
        try
        {
            // Act / Assert
            var failure = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "CREATE TABLE archive AS SELECT * FROM t ORDER BY id", cancellationToken: cancellation.Token));
            _output.WriteLine($"CTAS_CLOSE|closed={closed}|type={failure.GetType()}");
            closed.ShouldBeTrue();
            injected.ShouldBeTrue(); database.IsOffline.ShouldBeFalse();
            database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlPlanExecutor.TableAsSourceReading = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS fitting sources preserve retention allocations and admission; dispatch adds no allocation (#1789)")]
    [InlineData("SELECT id,k,note FROM t ORDER BY k")]
    [InlineData("WITH d AS (SELECT id,k,note FROM t) SELECT * FROM d ORDER BY k")]
    public async Task ExecuteAsync_TableAsFittingSource_ShouldPreserveAllocationsAndCharges(string query)
    {
        // Arrange: compare the old source entry with the explicitly permitted entry,
        // under an already initialized outer CTAS metrics owner.
        await using var engine = await Create("ctas-fitting", 1048576);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session, 100);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            var statement = new SqlStatementContext(transaction, database.Coordinator, catalogSnapshot: database.Catalog.CaptureSnapshot());
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions, default, statement)
                .Plan(SqlQueryRequest.FromSql(query).Statement.SqlExpression, statement.Snapshot);
            for (int i = 0; i < 10; i++) { Execute(false); Execute(true); }
            // Act
            var before = Measure(false); var after = Measure(true);
            // Assert
            after.Source.ShouldBe(before.Source); after.Peak.ShouldBe(before.Peak);
            after.Bytes.ShouldBeLessThanOrEqualTo(before.Bytes);
            if (plan is SqlSelectPlan)
            {
                var leafBefore = Measure(false, leaf: true); var leafAfter = Measure(true, leaf: true);
                leafAfter.ShouldBe(leafBefore);
                _output.WriteLine($"CTAS_FITTING_OPERATOR|before={leafBefore.Bytes}|after={leafAfter.Bytes}|source={leafAfter.Source}|peak={leafAfter.Peak}");
            }
            statement.Metrics.HashMemoryBytes.ShouldBe(0); statement.Metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0);
            _output.WriteLine($"CTAS_FITTING|before={before.Bytes}|after={after.Bytes}|source={after.Source}|peak={after.Peak}");

            (long Bytes, long Source, long Peak) Measure(bool permission, bool leaf = false)
            {
                long minimum = long.MaxValue, admitted = 0;
                for (int round = 0; round < 3; round++)
                {
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20; i++) { admitted = Execute(permission, leaf); }
                    minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - start);
                }
                return (minimum / 20, admitted, statement.Metrics.PeakHashMemoryBytes);
            }
            long Execute(bool permission, bool leaf = false)
            {
                var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
                ExecutorMetrics(executor) = statement.Metrics;
                SqlMaterializedResultSet result;
                if (leaf) { result = (SqlMaterializedResultSet)TableAsSelect(executor, (SqlSelectPlan)plan, statement, default, null, plan, permission ? session : null); }
                else
                {
                    var task = permission ? executor.ExecuteRootAsync(plan, statement, default, session) : executor.ExecuteAsync(plan, statement, default);
                    task.IsCompletedSuccessfully.ShouldBeTrue();
                    result = (SqlMaterializedResultSet)task.GetAwaiter().GetResult();
                }
                long bytes = result.ReserveMemory(statement.Metrics, 1048576, default);
                statement.Metrics.ReleaseHashMemory(bytes); result.DisposeAsync().GetAwaiter().GetResult();
                // Simulate the enclosing CTAS scope ending after source disposal.
                statement.Metrics.ReleasePlainSelectMemory(statement.Metrics.PlainSelectMemoryBytes);
                return bytes;
            }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS source roots retain root SELECT's unsupported spill refusals (#1789)")]
    [InlineData("SELECT DISTINCT id FROM t ORDER BY k", "CS05067")]
    [InlineData("SELECT id,COUNT(*) OVER() AS n FROM t ORDER BY id", "CS05079")]
    [InlineData("SELECT id FROM t UNION ALL SELECT id FROM t ORDER BY id", "CS05067")]
    [InlineData("SELECT id,COUNT(*) AS n FROM t GROUP BY GROUPING SETS ((id)) ORDER BY id", "CS05067")]
    [InlineData("SELECT id,(SELECT u.id FROM u WHERE u.id=t.id) AS n FROM t ORDER BY id", "CS05067")]
    [InlineData("SELECT nextval('s') AS n,id FROM t ORDER BY k", "CS05067")]
    [InlineData("SELECT id,JSON_VALUE('[1]', '$[0]' RETURNING BIGINT) AS n FROM t ORDER BY id", "CS05067")]
    public async Task ExecuteAsync_TableAsIneligibleSource_ShouldKeepRootRefusal(string query, string code)
    {
        // Arrange
        await using var engine = await Create("ctas-ineligible", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS " + query));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe(code); database.IsOffline.ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS inferred SQL/JSON domain checks also exclude source spill (#1789)")]
    public async Task ExecuteAsync_TableAsJsonDomain_ShouldKeepStatementOwnerExclusive()
    {
        // Arrange: the source only reads the domain; the destination executes its check.
        await using var engine = await Create("ctas-json-domain", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE DOMAIN guarded AS JSON CHECK(JSON_QUERY(VALUE, '$[*]' WITH WRAPPER) IS NOT NULL)");
        await session.ExecuteAsync("CREATE TABLE t(id INT,payload guarded)");
        for (int first = 0; first < 4000; first += 200)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 200).Select(i => $"({i},'[1]')"))); }
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067"); SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
        database.IsOffline.ShouldBeFalse(); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS spill conformance: physical rows, representatives, volatile calls and sequences match fitting sources (#1789)")]
    public async Task ExecuteAsync_TableAsDifferential_ShouldPreservePhysicalOrderAndEvaluation()
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await Create("ctas-reference", 67108864, logs[0]);
        await using var spilled = await Create("ctas-spilled", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        string[] queries =
        [
            "SELECT * FROM t ORDER BY id DESC",
            "SELECT id,k FROM t ORDER BY k DESC NULLS FIRST,id",
            "SELECT id,k FROM t ORDER BY k LIMIT 2500 OFFSET 13",
            "SELECT DISTINCT note FROM t ORDER BY id DESC",
            "SELECT DISTINCT k FROM t ORDER BY id DESC LIMIT 10 OFFSET 2",
            "SELECT stamp(id) AS observed,k FROM t ORDER BY observed DESC",
            "SELECT stamp(id) AS observed,k FROM t ORDER BY k DESC",
            "SELECT nextval('s') AS serial,id FROM t ORDER BY serial DESC",
        ];
        for (int i = 0; i < queries.Length; i++)
        {
            // Act
            logs[0].Clear(); logs[1].Clear();
            var left = await a.ExecuteAsync($"CREATE TABLE c{i} AS " + queries[i]);
            var right = await b.ExecuteAsync($"CREATE TABLE c{i} AS " + queries[i]);

            // Assert before a later SELECT replaces the CTAS metrics.
            right.AffectedCount.ShouldBe(left.AffectedCount);
            logs[1].ShouldBe(logs[0], customMessage: queries[i]);
            var metrics = b.LastStatementMetrics!;
            SqlSortSpillMetrics.For(metrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(spilled.TempSpace!).ShouldBe(0); Files(spilled).ShouldBe(0);
            (await TableAsPhysicalRowsAsync(b, $"c{i}")).ShouldBe(await TableAsPhysicalRowsAsync(a, $"c{i}"), customMessage: queries[i]);
        }
        await using var referenceSequence = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var spillSequence = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(spillSequence)).ShouldBe(await Rows(referenceSequence));
        // More than eight sequential CTAS sources must leave every session result slot free.
        await b.ExecuteAsync("CREATE TABLE last AS SELECT id FROM t ORDER BY id");
        var results = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < SqlSpillLifetime.MaximumResultsPerSession; i++)
            { results.Add((await b.ExecuteAsync("SELECT id,k FROM t ORDER BY k")).ShouldBeOfType<SqlSpilledResultSet>()); }
        }
        finally { foreach (var result in results) { await result.DisposeAsync(); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS spill capacity: file-backed session and declared scripts honor the default four-grant quota (#1789)")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ExecuteAsync_TableAsTenGrants_ShouldCompleteOrCodeQuota(bool script, bool distinct, bool overQuota)
    {
        // Arrange: ORDER BY id costs 232 B per full row or 192 B per note-only DISTINCT
        // input. The three-byte refusal keeps SQ-L2b's ORDER BY note and 264 B charge.
        const int grant = 262144;
        int count = overQuota ? 7 * grant / 264 : 10 * grant / (distinct ? 192 : 232);
        string root = Path.Combine(_root, "ctas-capacity");
        string note = new(overQuota ? '界' : 'x', 20);
        await using (var seedEngine = SqlDatabaseEngine.Create("ctas-capacity-seed", new() { RootPath = root, CheckpointJournalSize = 65536 }))
        {
            await using var seedSession = await (await seedEngine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await seedSession.ExecuteAsync("CREATE TABLE t(id INT,note TEXT COLLATE binary)");
            for (int first = 0; first < count; first += 200)
            { await seedSession.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200, count - first)).Select(i => $"({i},'{note}')"))); }
        }
        string sql = "CREATE TABLE archive AS SELECT " + (distinct ? "DISTINCT " : "") + (distinct ? "note" : "*") + " FROM t ORDER BY " + (overQuota ? "note" : "id DESC");
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-capacity");
        builder.Options.RootPath = root; builder.Options.StatementMemoryBudget = grant; builder.Options.CheckpointJournalSize = 65536;
        if (script) { builder.AddDatabase("db", declaration => declaration.Script("archive", sql)); }
        int runs = 0;
        SqlRowCollector.ObserveSpill = phase => { if (phase == "complete") { runs++; } };
        try
        {
            // Act / Assert
            if (script && overQuota)
            {
                (await Should.ThrowAsync<DatabaseException>(async () => await builder.BuildAsync())).Diagnostics[0].Code.ShouldBe("CK05002");
                await using var reopened = SqlDatabaseEngine.Create("ctas-quota-reopen", new() { RootPath = root });
                var db = await reopened.OpenDatabaseAsync("db"); db.IsOffline.ShouldBeFalse();
                db.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
                return;
            }
            await using var engine = await builder.BuildAsync();
            var database = await engine.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            if (!script)
            {
                if (overQuota)
                {
                    (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql))).Diagnostics[0].Code.ShouldBe("CK05002");
                    database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
                    database.IsOffline.ShouldBeFalse(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
                    return;
                }
                (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(distinct ? 1 : count);
                var metrics = SqlSortSpillMetrics.For(session.LastStatementMetrics!)!;
                metrics.Runs.ShouldBeGreaterThan(1); metrics.MergePasses.ShouldBeGreaterThan(0);
                _output.WriteLine($"CTAS_CAPACITY|script={script}|distinct={distinct}|rows={count}|runs={metrics.Runs}|passes={metrics.MergePasses}");
            }
            runs.ShouldBeGreaterThan(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            int visited = 0;
            foreach (var row in await TableAsPhysicalRowsAsync(session, "archive"))
            {
                if (distinct) { row.Cells[0].ShouldBe(note); }
                else { row.Cells[0].ShouldBe(count - 1 - visited); row.Cells[1].ShouldBe(note); }
                visited++;
            }
            visited.ShouldBe(distinct ? 1 : count);
            if (script) { database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1); }
            (await session.ExecuteAsync("CREATE TABLE empty AS SELECT * FROM t ORDER BY id WITH NO DATA")).AffectedCount.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS spill failures: sort and copy cancellation or temporary reads abandon pages online and allow retry (#1789)")]
    [InlineData("sort", true)]
    [InlineData("copy", true)]
    [InlineData("copy", false)]
    public async Task ExecuteAsync_TableAsSourceFailure_ShouldReclaimAndRetry(string phase, bool cancel)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Create("ctas-failure", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        ulong owner = 0; int copied = 0; bool injected = false;
        void Fail()
        {
            injected = true;
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw TempException("CK05005", "injected CTAS temporary read failure");
        }
        SqlRowCollector.ObserveSpill = point => { if (phase == "sort" && point == "append") { Fail(); } };
        SqlPlanExecutor.TableAsStage = (point, id) =>
        { owner = id; if (point == "Copied") { copied++; if (phase == "copy" && copied == 1) { BeforeRead(engine.TempSpace!, Fail); } } };
        try
        {
            // Act
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC", cancellationToken: cancellation.Token)); }
            else { (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC"))).Diagnostics[0].Code.ShouldBe("CK05005"); }
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.TableAsStage = null; BeforeRead(engine.TempSpace!, null); }
        // Assert
        injected.ShouldBeTrue(); if (phase == "copy") { copied.ShouldBeGreaterThan(0); }
        database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse(); database.IsOffline.ShouldBeFalse();
        await ReclaimTableAsAsync(database, guard.Token); database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        (await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC")).AffectedCount.ShouldBe(4000);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS spill Build source failure retains its script prefix and resumes exactly the failed statement (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_TableAsSourceFailure_ShouldRetainPrefixAndResume(bool readFailure)
    {
        // Arrange
        string root = Path.Combine(_root, "ctas-script-retry");
        await using (var seedEngine = SqlDatabaseEngine.Create("seed", new() { RootPath = root }))
        { await using var seedSession = await (await seedEngine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(seedSession); }
        const string script = "CREATE TABLE marker(id INT); INSERT INTO marker VALUES(1); CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC; INSERT INTO marker VALUES(2);";
        SqlDatabaseEngineBuilder Builder()
        {
            var builder = SqlDatabaseEngine.CreateBuilder("ctas-script-retry");
            builder.Options.RootPath = root; builder.Options.StatementMemoryBudget = 262144;
            builder.Options.StatementTempSpaceQuota = 67108864;
            builder.AddDatabase("db", declaration => declaration.Script("archive", script)); return builder;
        }
        var failedBuilder = Builder();
        if (!readFailure) { failedBuilder.Options.StatementTempSpaceQuota = 0; }
        int observed = 0;
        SqlPlanExecutor.TableAsSourceReading = (result, _) =>
        {
            if (++observed != 65) { return; }
            BeforeRead(TableAsSpace(TableAsStatement(result)), () => throw TempException("CK05005", "injected script source read failure"));
        };
        // Act: a coded source failure at Build leaves the earlier script effects durable.
        try
        { (await Should.ThrowAsync<DatabaseException>(async () => await failedBuilder.BuildAsync())).Diagnostics[0].Code.ShouldBe(readFailure ? "CK05005" : "CK05002"); }
        finally { SqlPlanExecutor.TableAsSourceReading = null; }
        if (readFailure) { observed.ShouldBeGreaterThanOrEqualTo(65); }
        // Assert
        await using (var online = SqlDatabaseEngine.Create("inspect-prefix", new() { RootPath = root }))
        {
            var database = await online.OpenDatabaseAsync("db"); database.IsOffline.ShouldBeFalse();
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(2);
            database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            await using var reader = await database.CreateSessionAsync();
            await using var marker = (DatabaseResultSet)await reader.ExecuteAsync("SELECT * FROM marker");
            (await Rows(marker)).ShouldHaveSingleItem().Cells[0].ShouldBe(1);
        }
        await using var recovered = await Builder().BuildAsync();
        var resumed = await recovered.OpenDatabaseAsync("db");
        resumed.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
        await using var session = await resumed.CreateSessionAsync();
        await using var markers = (DatabaseResultSet)await session.ExecuteAsync("SELECT * FROM marker");
        (await Rows(markers)).Select(row => row.Cells[0]).ShouldBe(new object?[] { 1, 2 });
        (await TableAsPhysicalRowsAsync(session, "archive")).Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 4000).Reverse().Cast<object?>());
        TempBytes(recovered.TempSpace!).ShouldBe(0); Files(recovered).ShouldBe(0);
    }

    private static async Task ReclaimTableAsAsync(SqlDatabase database, CancellationToken token)
    {
        database.TableReclamation.Request();
        do { database.TableReclamation.RunBatch(token); if (database.TableReclamation.HasWork) { await Task.Delay(1, token); } }
        while (database.TableReclamation.HasWork);
    }

    private static async Task<RowValues[]> TableAsPhysicalRowsAsync(SqlDatabaseSession session, string table)
    {
        // Unordered pages expose physical insertion order without retaining an over-budget
        // destination SELECT. The observer's output list is outside statement execution.
        var rows = new List<RowValues>();
        for (int offset = 0; ; offset += 64)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT * FROM {table} LIMIT 64 OFFSET {offset}");
            var page = await Rows(result); rows.AddRange(page);
            if (page.Length < 64) { return rows.ToArray(); }
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_statement")]
    private static extern ref StorageTempStatement TableAsStatement(SqlSpilledResultSet result);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Space")]
    private static extern StorageTempSpace TableAsSpace(StorageTempStatement statement);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteSelect")]
    private static extern DatabaseResult TableAsSelect(SqlPlanExecutor executor, SqlSelectPlan plan, SqlStatementContext statement,
        CancellationToken token, int? retainRows, SqlPlan? retentionPlan, SqlDatabaseSession? owner);

    private sealed class TableAsFailedCloseStream : FileStream
    {
        private readonly Action _closed;
        internal TableAsFailedCloseStream(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Action closed) : base(handle, FileAccess.ReadWrite) { _closed = closed; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { _closed(); throw new IOException("injected CTAS close failure"); }
        }
    }
}

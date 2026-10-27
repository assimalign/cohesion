using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCostAndHashJoinTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Hash cancellation: a live build releases its grant and preserves caller token identity (#1522)")]
    public async Task ExecuteAsync_CancellationDuringHashProbe_ShouldReleaseBuild()
    {
        using var cancellation = new CancellationTokenSource();
        SqlDatabaseSession? activeSession = null;
        long liveBytes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("hash-cancellation");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_hash", (long id) =>
        {
            liveBytes = activeSession!.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes;
            cancellation.Cancel();
            return id;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        activeSession = session;
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        var failure = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            "SELECT l.id FROM l JOIN r ON l.k=r.k WHERE cancel_hash(l.id)>=0", cancellationToken: cancellation.Token));
        failure.CancellationToken.ShouldBe(cancellation.Token);
        liveBytes.ShouldBeGreaterThan(0);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM l", cancellationToken: CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(80); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Cost probes: temporary metadata intents release and older grants survive (#1522)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CostProbe_ShouldPreserveOnlyOlderMetadataGrants(bool preexisting)
    {
        await using var engine = SqlDatabaseEngine.Create("cost-intent-lifetime", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_v ON t(v)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var resource = LockResource.Object(table.ObjectId);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var observer = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            if (preexisting) { database.Coordinator.LockManager.TryAcquire(reader.Sequence, resource, LockMode.IntentExclusive).ShouldBeTrue(); }
            var context = new SqlStatementContext(reader, database.Coordinator);
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            await using var result = (DatabaseResultSet)await executor.ExecuteAsync(SqlQueryRequest.FromSql("SELECT id FROM t WHERE v=7"), context, CancellationToken.None);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { Convert.ToInt32(row.GetValue(0)).ShouldBe(7); count++; }
            count.ShouldBe(1);
            context.Metrics.AccessPath.ShouldBe("seek:by_v");
            database.Coordinator.LockManager.TryAcquire(observer.Sequence, resource, LockMode.Exclusive).ShouldBe(!preexisting);
        }
        finally
        {
            await database.Coordinator.RollbackAsync(observer, CancellationToken.None);
            await database.Coordinator.RollbackAsync(reader, CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash memory: concurrently retained builds share one statement grant (#1522)")]
    public async Task ExecuteAsync_TwoHashBuilds_ShouldShareTheStatementBudget()
    {
        long peak = await RunAsync(64L * 1024 * 1024, false);
        await RunAsync(peak * 3 / 4, true);

        static async Task<long> RunAsync(long budget, bool refuse)
        {
            await using var engine = SqlDatabaseEngine.Create("hash-shared-grant", new() { StatementMemoryBudget = budget });
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            foreach (string table in new[] { "a", "b", "c" })
            {
                await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
            }
            const string query = "SELECT a.id FROM a JOIN b ON a.k=b.k JOIN c ON b.k=c.k ORDER BY a.id";
            if (refuse)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
                failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
                session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
                session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(budget);
            }
            else
            {
                await ReadAsync(query);
                session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe(["join-hash", "join-hash"]);
            }
            long peakBytes = session.LastStatementMetrics.ShouldNotBeNull().PeakHashMemoryBytes;
            // The reduced grant still fits each individual build, and failure releases all ownership.
            await ReadAsync("SELECT a.id FROM a JOIN b ON a.k=b.k ORDER BY a.id");
            session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(budget);
            return peakBytes;

            async Task ReadAsync(string sql)
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { Convert.ToInt32(row.GetValue(0)).ShouldBe(++count); }
                count.ShouldBe(80);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash memory: retained padded CHAR and trimmed text both consume the grant (#1522)")]
    public async Task ExecuteAsync_PaddedCharacterPayload_ShouldCountRetainedStorage()
    {
        await using var engine = SqlDatabaseEngine.Create("hash-char-memory", new() { StatementMemoryBudget = 20_000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT,payload CHAR(1000))", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id},'x')")), cancellationToken: CancellationToken.None);
        }
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT l.id FROM l JOIN r ON l.k=r.k", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(20_000);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash memory: nonpositive engine budgets are refused before workers start (#1522)")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_NonpositiveMemoryBudget_ShouldRefuse(long bytes)
    {
        // Arrange / Act
        var failure = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("invalid-budget", new() { StatementMemoryBudget = bytes }));

        // Assert
        failure.ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.StatementMemoryBudget));
        failure.Message.ShouldContain("invalid-budget", Case.Sensitive);
        failure.Message.ShouldContain("SQL", Case.Sensitive);
        failure.ActualValue.ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join costing: a smaller indexed driver wins independently of index name (#1522)")]
    public async Task ExecuteAsync_BothSidesIndexed_ShouldDriveTheSmallerInput()
    {
        await using var engine = SqlDatabaseEngine.Create("join-driver-cost", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE big(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE small(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX z_big_key ON big(k)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX a_small_key ON small(k)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO big VALUES " + string.Join(',', Enumerable.Range(1, 1000).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO small VALUES " + string.Join(',', Enumerable.Range(1, 8).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT b.id,s.id FROM big b JOIN small s ON b.k=s.k ORDER BY b.id", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { Convert.ToInt32(row.GetValue(0)).ShouldBe(++count); Convert.ToInt32(row.GetValue(1)).ShouldBe(count); }
        count.ShouldBe(8);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.JoinAccessPaths.ShouldBe(["join-seek:z_big_key"]);
        metrics.RecordsExamined.ShouldBe(16);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash NULL keys: all-null inputs allocate no buckets and preserve LEFT rows (#1522)")]
    public async Task ExecuteAsync_AllNullHashKeys_ShouldAvoidRetainedRows()
    {
        await using var engine = SqlDatabaseEngine.Create("hash-nulls", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},NULL)")), cancellationToken: CancellationToken.None);
        }
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT l.id,r.id FROM l LEFT JOIN r ON l.k=r.k ORDER BY l.id", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { Convert.ToInt32(row.GetValue(0)).ShouldBe(++count); row.IsNull(1).ShouldBeTrue(); }
        count.ShouldBe(80);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.JoinAccessPaths.ShouldBe(["join-hash"]);
        metrics.JoinCandidatePairs.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBe(104);
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join: duplicates, NULLs, composite equalities and LEFT residuals retain exact rows (#1522)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_HashDuplicatesAndResiduals_ShouldMatchSqlEquality(bool left, bool composite)
    {
        await using var engine = SqlDatabaseEngine.Create("hash-semantics", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT,v INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 96)
                .Select(id => $"({id},{(id % 13 == 0 ? "NULL" : (id % 12).ToString())},{id % 5})")), cancellationToken: CancellationToken.None);
        }
        string condition = composite ? "l.k=r.k AND l.v=r.v" : "l.k=r.k AND r.v>l.v";
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            $"SELECT l.id,r.id FROM l {(left ? "LEFT" : "INNER")} JOIN r ON {condition} ORDER BY l.id,r.id", cancellationToken: CancellationToken.None);
        var expected = Enumerable.Range(1, 96).SelectMany(a =>
        {
            var matches = Enumerable.Range(1, 96).Where(b => a % 13 != 0 && b % 13 != 0 && a % 12 == b % 12 &&
                (composite ? a % 5 == b % 5 : b % 5 > a % 5)).Select(b => (a, b: (int?)b)).ToArray();
            return left && matches.Length == 0 ? [(a, b: (int?)null)] : matches;
        }).ToArray();
        var actual = new System.Collections.Generic.List<(int, int?)>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { actual.Add((Convert.ToInt32(row.GetValue(0)), row.IsNull(1) ? null : Convert.ToInt32(row.GetValue(1)))); }
        actual.ShouldBe(expected);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.JoinAccessPaths.ShouldBe(["join-hash"]);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, engine.EngineOptions.StatementMemoryBudget);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN ANALYZE: data changes are refused and standalone ANALYZE stays unsupported (#1522)")]
    [InlineData("EXPLAIN ANALYZE INSERT INTO t VALUES (2)")]
    [InlineData("EXPLAIN ANALYZE UPDATE t SET id=2")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t")]
    [InlineData("EXPLAIN ANALYZE WITH c AS (SELECT 1) DELETE FROM t")]
    [InlineData("ANALYZE t")]
    public async Task ExecuteAsync_AnalyzeDataChange_ShouldRefuseWithoutWriting(string sql)
    {
        await using var engine = SqlDatabaseEngine.Create("analyze-readonly", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldNotBeEmpty();
        if (sql.StartsWith("EXPLAIN", StringComparison.Ordinal))
        { failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05057"); }
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM t", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { Convert.ToInt32(row.GetValue(0)).ShouldBe(1); count++; }
        count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - EXPLAIN ANALYZE: SELECT executes and reports work while plain EXPLAIN does not (#1522)")]
    public async Task ExecuteAsync_ExplainAnalyze_ShouldReportActualJoinCandidates()
    {
        await using var engine = SqlDatabaseEngine.Create("hash-explain", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        const string query = "SELECT l.id FROM l JOIN r ON l.k=r.k WHERE l.id<=40";
        await using (var plain = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None))
        {
            await foreach (var row in plain.GetRowsAsync(CancellationToken.None)) { row.GetValue(7).ShouldBeNull(); }
            session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(0);
        }
        await using var analyzed = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE " + query, cancellationToken: CancellationToken.None);
        bool stepSeen = false;
        await foreach (var row in analyzed.GetRowsAsync(CancellationToken.None))
        {
            if (row.GetString(2) != "JoinStep") { continue; }
            row.GetString(5).ShouldNotBeNull().ShouldContain("HashJoin", Case.Sensitive);
            row.GetValue(6).ShouldNotBeNull();
            Convert.ToInt64(row.GetValue(7)).ShouldBe(40);
            stepSeen = true;
        }
        stepSeen.ShouldBeTrue();
        session.LastStatementMetrics.ShouldNotBeNull().JoinCandidatePairs.ShouldBe(40);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash memory: a small statement budget refuses the build without changing data (#1522)")]
    public async Task ExecuteAsync_HashBuildExceedsBudget_ShouldFailWithCodedError()
    {
        await using var engine = SqlDatabaseEngine.Create("hash-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "l", "r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT l.id FROM l JOIN r ON l.k=r.k", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1024);
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM l", cancellationToken: CancellationToken.None);
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(80); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Access costing: skew changes select the more selective index without statistics maintenance (#1522)")]
    public async Task ExecuteAsync_SkewChanges_ShouldChooseTheSelectivePathWithoutAnalyze()
    {
        await using var engine = SqlDatabaseEngine.Create("index-cost-skew", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,a INT,b INT,r INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX a_broad ON t(a,b)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX z_rare ON t(r)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',',
            Enumerable.Range(1, 800).Select(id => $"({id},1,1,{id})")), cancellationToken: CancellationToken.None);
        await CheckAsync("a=1 AND b=1 AND r=579", "z_rare");
        await session.ExecuteAsync("UPDATE t SET a=id,b=id,r=1", cancellationToken: CancellationToken.None);
        await CheckAsync("a=579 AND b=579 AND r=1", "a_broad");

        async Task CheckAsync(string predicate, string index)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM t WHERE " + predicate,
                cancellationToken: CancellationToken.None);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                Convert.ToInt32(row.GetValue(0)).ShouldBe(579);
                rows++;
            }
            rows.ShouldBe(1);
            session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:" + index);
            session.LastStatementMetrics.RecordsExamined.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Broad equijoin: tenfold inputs have linear candidate work and identical pairs (#1522)")]
    public async Task ExecuteAsync_TenfoldBroadEquijoin_ShouldAvoidQuadraticCandidateGrowth()
    {
        await using var engine = SqlDatabaseEngine.Create("hash-join-work", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long small = await MeasureAsync(80, "small");
        long large = await MeasureAsync(800, "large");
        small.ShouldBeGreaterThanOrEqualTo(80);
        large.ShouldBeGreaterThanOrEqualTo(800);
        large.ShouldBeLessThanOrEqualTo(small * 20);

        async Task<long> MeasureAsync(int count, string suffix)
        {
            foreach (string side in new[] { "l", "r" })
            {
                await session.ExecuteAsync($"CREATE TABLE {side}_{suffix}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
                // The join key has no index; identical primary indexes remain present on both sides.
                await session.ExecuteAsync($"INSERT INTO {side}_{suffix} VALUES " +
                    string.Join(',', Enumerable.Range(1, count).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
            }
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                $"SELECT l.id,r.id FROM l_{suffix} l JOIN r_{suffix} r ON l.k=r.k ORDER BY l.id", cancellationToken: CancellationToken.None);
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                Convert.ToInt32(row.GetValue(0)).ShouldBe(++rows);
                Convert.ToInt32(row.GetValue(1)).ShouldBe(rows);
            }
            rows.ShouldBe(count);
            metrics.RecordsExamined.ShouldBe(2L * count);
            return metrics.JoinCandidatePairs;
        }
    }
}

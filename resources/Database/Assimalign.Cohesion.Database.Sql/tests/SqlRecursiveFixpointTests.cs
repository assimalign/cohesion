using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlRecursiveFixpointTests
{
    private readonly ITestOutputHelper _output;
    public SqlRecursiveFixpointTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: generated cyclic graph closure matches an iterative oracle")]
    [InlineData(1)] [InlineData(17)] [InlineData(53)] [InlineData(101)]
    public async Task Execute_GeneratedGraph_ShouldMatchWorkingTableOracle(int seed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-graph", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var random = new Random(seed);
        var edges = Enumerable.Range(0, 36).Select(_ => (From: random.Next(12), To: random.Next(12))).ToArray();
        await session.ExecuteAsync("CREATE TABLE edges (src INT, dst INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_src ON edges(src);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO edges VALUES " + string.Join(',', edges.Select(e => $"({e.From},{e.To})")), cancellationToken: CancellationToken.None);
        var expected = new HashSet<long> { 0 };
        var working = new HashSet<long> { 0 };
        while (working.Count > 0)
        {
            var next = edges.Where(e => working.Contains(e.From)).Select(e => (long)e.To).Where(expected.Add).ToHashSet();
            working = next;
        }

        // Act
        var actual = await RowsAsync(session, "WITH RECURSIVE reach(n) AS (SELECT 0 UNION SELECT CAST(e.dst AS BIGINT) FROM reach r JOIN edges e ON r.n=e.src) SELECT n FROM reach ORDER BY n;");

        // Assert
        actual.Select(row => Convert.ToInt64(row[0], CultureInfo.InvariantCulture)).ShouldBe(expected.Order());
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: generated paths preserve UNION ALL multiplicities and UNION equality")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_GeneratedPaths_ShouldMatchBagOracle(bool distinct)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-paths", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var edges = Enumerable.Range(1, 9).SelectMany(n => new[] { (From: n / 2, To: n), (From: n / 2, To: n) }).ToArray();
        await session.ExecuteAsync("CREATE TABLE edges(src INT,dst INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO edges VALUES " + string.Join(',', edges.Select(e => $"({e.From},{e.To})")), cancellationToken: CancellationToken.None);
        var expected = new List<(long Node, long Depth)> { (0, 0) };
        var working = expected.ToList();
        var seen = expected.ToHashSet();
        while (working.Count > 0)
        {
            working = working.SelectMany(p => edges.Where(e => e.From == p.Node).Select(e => ((long)e.To, p.Depth + 1)))
                .Where(p => !distinct || seen.Add(p)).ToList();
            expected.AddRange(working);
        }

        // Act
        var actual = await RowsAsync(session, $"WITH RECURSIVE paths(n,d) AS (SELECT 0,0 UNION {(distinct ? "" : "ALL")} SELECT CAST(e.dst AS BIGINT),p.d+1 FROM paths p JOIN edges e ON p.n=e.src) SELECT n,d FROM paths ORDER BY n,d;");

        // Assert
        actual.Select(row => (Convert.ToInt64(row[0]), Convert.ToInt64(row[1]))).ShouldBe(expected.OrderBy(p => p.Node).ThenBy(p => p.Depth));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: outer LIMIT stops infinite producers and independent readers share progress")]
    [InlineData("SELECT n FROM r LIMIT 10", 10, 10)]
    [InlineData("SELECT a.n,b.n FROM r a CROSS JOIN r b LIMIT 10", 10, 10)]
    [InlineData("(SELECT n FROM r LIMIT 10) UNION ALL (SELECT n FROM r LIMIT 20)", 30, 20)]
    [InlineData("SELECT n FROM (SELECT n FROM r) d LIMIT 10", 10, 10)]
    [InlineData("SELECT n FROM r LIMIT 0", 0, 0)]
    public async Task Execute_InfiniteProducer_ShouldStopAtFurthestReader(string consumer, int count, int produced)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-limit", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long candidates = 0;
        SqlPlanExecutor.RecursiveProgress = (_, visits, _) => candidates = visits;
        try
        {
            // Act
            var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) " + consumer);

            // Assert
            rows.Count.ShouldBe(count);
            candidates.ShouldBe(produced);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: DISTINCT compares NULL and collated character values")]
    [InlineData("SELECT NULL UNION SELECT n FROM r", null)]
    [InlineData("SELECT 'A' COLLATE case_insensitive UNION SELECT LOWER(n) FROM r", "A")]
    [InlineData("SELECT CAST('a ' AS CHAR(2)) UNION SELECT CAST('a' AS CHAR(2)) FROM r", "a ")]
    public async Task Execute_DistinctIdentity_ShouldTerminateCycle(string body, string? expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-identity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS ({body}) SELECT n FROM r;");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(expected);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: independent subqueries execute once across iterations")]
    public async Task Execute_IndependentChild_ShouldKeepStatementCache()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-child-cache", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+(SELECT COUNT(*) FROM t) FROM r WHERE n<7) SELECT n FROM r");

        // Assert
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1, 4, 7 });
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: shared-reader binary delivery owns each returned cell")]
    public async Task Execute_SharedBinary_ShouldDetachReturnedRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-binary", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n,b) AS (SELECT 1,X'01' UNION ALL SELECT n+1,b FROM r WHERE n<3) SELECT a.b FROM r a CROSS JOIN r b LIMIT 3");
        ((byte[])rows[0][0]!)[0] = 9;

        // Assert
        ((byte[])rows[1][0]!)[0].ShouldBe((byte)1);
        ((byte[])rows[2][0]!)[0].ShouldBe((byte)1);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: ordered LIMIT defers independent sequence projection")]
    public async Task Execute_OrderedSequence_ShouldProjectSelectedRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-sequence", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3) SELECT NEXT VALUE FOR s FROM r ORDER BY n DESC LIMIT 1");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT NEXT VALUE FOR s")).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: finite consumers execute through existing query operators")]
    [InlineData("SELECT SUM(n) FROM r", 6)]
    [InlineData("SELECT (SELECT SUM(n) FROM r)", 6)]
    [InlineData("SELECT COUNT(*) FROM r a JOIN r b ON a.n=b.n", 3)]
    [InlineData("SELECT MAX(n) FROM r", 3)]
    [InlineData("SELECT n FROM r ORDER BY n DESC LIMIT 1", 3)]
    [InlineData("SELECT MAX(n) FROM (SELECT n,ROW_NUMBER() OVER(ORDER BY n) AS pos FROM (SELECT n FROM r LIMIT 10) limited) d", 3)]
    public async Task Execute_FiniteConsumers_ShouldCompose(string consumer, long expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-consumers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3) " + consumer);

        // Assert
        Convert.ToInt64(rows.ShouldHaveSingleItem()[0]).ShouldBe(expected);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: finite recursive terms compose with derived queries, preserved joins and set operations")]
    [InlineData("SELECT (SELECT n+1) FROM r WHERE n<3")]
    [InlineData("SELECT d.n+1 FROM (SELECT n FROM r) d WHERE n<3")]
    [InlineData("SELECT n+ROW_NUMBER() OVER() FROM r WHERE n<3")]
    [InlineData("SELECT r.n+1 FROM r LEFT JOIN t ON r.n=t.id WHERE r.n<3")]
    [InlineData("SELECT r.n+1 FROM t RIGHT JOIN r ON r.n=t.id WHERE r.n<3")]
    [InlineData("SELECT n+1 FROM r WHERE n<3 INTERSECT SELECT CAST(id AS BIGINT) FROM t")]
    [InlineData("SELECT n+1 FROM r WHERE n<3 EXCEPT SELECT CAST(99 AS BIGINT)")]
    public async Task Execute_RecursiveTermComposition_ShouldUsePreviousIteration(string term)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-terms", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL ({term})) SELECT n FROM r");

        // Assert
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1, 2, 3 });
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: query sources feed INSERT, UPDATE, DELETE and CTAS")]
    [InlineData("INSERT INTO t SELECT n+10 FROM r", 6)]
    [InlineData("UPDATE t SET id=r.n+10 FROM r WHERE t.id=r.n", 3)]
    [InlineData("DELETE FROM t USING r WHERE t.id=r.n", 0)]
    [InlineData("CREATE TABLE copied AS SELECT n FROM r", 3)]
    public async Task Execute_RecursiveDmlSource_ShouldCompleteBeforeWrites(string consumer, int expectedCount)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);

        // Act
        const string prefix = "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3) ";
        var result = await session.ExecuteAsync(consumer.StartsWith("CREATE", StringComparison.Ordinal)
            ? "CREATE TABLE copied AS " + prefix + "SELECT n FROM r" : prefix + consumer,
            cancellationToken: CancellationToken.None);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        var rows = await RowsAsync(session, consumer.StartsWith("CREATE", StringComparison.Ordinal) ? "SELECT n FROM copied" : "SELECT id FROM t");
        rows.Count.ShouldBe(expectedCount);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: an INSERT source LIMIT stops an infinite shared CTE")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_LimitedInsertSource_ShouldPullOnlySelectedRows(bool sourcePrefix)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-insert-limit", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id BIGINT)", cancellationToken: CancellationToken.None);

        // Act
        const string prefix = "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) ";
        var result = await session.ExecuteAsync(sourcePrefix ? "INSERT INTO t " + prefix + "SELECT n FROM r LIMIT 10"
            : prefix + "INSERT INTO t SELECT n FROM r LIMIT 10",
            cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(10);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(10L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: CTAS WITH NO DATA infers an infinite source without evaluating it")]
    public async Task Execute_NoDataRecursiveSource_ShouldCreateOnlyMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-no-data", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync("CREATE TABLE copied AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r WITH NO DATA",
            cancellationToken: CancellationToken.None);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT COUNT(*) FROM copied")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: EXISTS and scalar subqueries stop at their own demand")]
    [InlineData("SELECT EXISTS(SELECT n FROM r)", true)]
    [InlineData("SELECT (SELECT n FROM r LIMIT 1)", 1L)]
    public async Task Execute_SubqueryDemand_ShouldStopInfiniteInput(string consumer, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-subquery", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, _, rows) => produced = rows;
        try
        {
            // Act
            var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) " + consumer);

            // Assert
            rows.ShouldHaveSingleItem()[0].ShouldBe(expected);
            produced.ShouldBe(1);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: unbounded scalar input fails cardinality after two rows")]
    public async Task Execute_ScalarCardinality_ShouldRefuseAfterTwoRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-cardinality", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, _, rows) => produced = rows;
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT (SELECT n FROM r)", cancellationToken: CancellationToken.None));

            // Assert
            error.Diagnostics.ShouldContain(d => d.Code == SqlExecutionException.CardinalityCode);
            produced.ShouldBe(2);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: unbounded window demand exhausts the statement budget and releases every grant")]
    public async Task Execute_UnboundedWindow_ShouldReportMemoryRefusal()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-window", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, _, rows) => produced = rows;
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT ROW_NUMBER() OVER() FROM r LIMIT 10", cancellationToken: CancellationToken.None));

            // Assert
            error.Diagnostics.ShouldContain(d => d.Code == "CS05067");
            produced.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: cancellation in a growing iteration releases every table")]
    public async Task Execute_CancelMidIteration_ShouldReleaseGrant()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-cancel", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE fan(n INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO fan VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10);", cancellationToken: CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        long live = 0;
        long observed = 0;
        SqlPlanExecutor.RecursiveProgress = (iteration, _, rows) =>
        {
            if (iteration == 1 && rows == 6) { observed = rows; live = session.LastStatementMetrics!.HashMemoryBytes; cancellation.Cancel(); }
        };
        try
        {
            // Act
            var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "WITH RECURSIVE r(n) AS (SELECT 0 UNION ALL SELECT r.n+1 FROM r CROSS JOIN fan) SELECT n FROM r", cancellationToken: cancellation.Token));

            // Assert
            error.CancellationToken.ShouldBe(cancellation.Token);
            observed.ShouldBe(6);
            live.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            (await RowsAsync(session, "SELECT 7")).ShouldHaveSingleItem()[0].ShouldBe(7);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: runaway ALL and DISTINCT release a refused shared memory grant")]
    [InlineData("ALL")] [InlineData("")]
    public async Task Execute_MemoryRefusal_ShouldReleaseGrant(string quantifier)
    {
        // Arrange
        const long budget = 32_768;
        await using var engine = SqlDatabaseEngine.Create("recursive-memory", new() { StatementMemoryBudget = budget });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var result = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"WITH RECURSIVE r(n) AS (SELECT 1 UNION {quantifier} SELECT n+1 FROM r) SELECT n FROM r", cancellationToken: CancellationToken.None));

        // Assert
        result.Diagnostics.ShouldContain(d => d.Code == "CS05067");
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeInRange(1, budget);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT 7")).ShouldHaveSingleItem()[0].ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: candidate work is linear and has no hundred-level ceiling")]
    [InlineData(200)] [InlineData(400)]
    public async Task Execute_LongChain_ShouldVisitEachWorkingRowOnce(int length)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-work", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long candidates = 0, iterations = 0;
        SqlPlanExecutor.RecursiveProgress = (iteration, visits, _) => { candidates = visits; iterations = iteration; };
        try
        {
            // Act
            var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS (SELECT 1 UNION SELECT n+1 FROM r WHERE n<{length}) SELECT n FROM r");

            // Assert
            rows.Count.ShouldBe(length);
            candidates.ShouldBe(length);
            iterations.ShouldBe(length - 1);
            _output.WriteLine($"chain={length}; candidates={candidates}; producing iterations={iterations}; peak bytes={session.LastStatementMetrics!.PeakHashMemoryBytes}");
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: persisted view reloads its working-table plan")]
    public async Task Open_RecursiveView_ShouldRebindAndExecute()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("recursive-view", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE VIEW v AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r;", cancellationToken: CancellationToken.None);
            (await RowsAsync(session, "SELECT n FROM v LIMIT 10")).Count.ShouldBe(10);
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("recursive-view", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var rows = await RowsAsync(reader, "SELECT n FROM v LIMIT 10");

        // Assert
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(Enumerable.Range(1, 10).Select(n => (long)n));
        reader.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: cached view references own independent CTE scopes")]
    public async Task Execute_RepeatedRecursiveView_ShouldIsolateItsCommonTables()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-view-scopes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW v AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<2) SELECT n FROM r", cancellationToken: CancellationToken.None);
        int produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, _, _) => produced++;
        try
        {
            // Act
            var rows = await RowsAsync(session, "SELECT a.n,b.n FROM v a CROSS JOIN v b ORDER BY a.n,b.n");

            // Assert
            rows.Count.ShouldBe(4);
            produced.ShouldBe(4);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive fixpoint: EXPLAIN reports union, work scan, iterations and rows")]
    public async Task Explain_RecursiveUnion_ShouldReportActualFixpointWork()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-explain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string query = "WITH RECURSIVE r(n) AS (SELECT 1 UNION SELECT n+1 FROM r WHERE n<3) SELECT n FROM r";

        // Act
        var plain = await RowsAsync(session, "EXPLAIN " + query);
        var analyzed = await RowsAsync(session, "EXPLAIN ANALYZE " + query);

        // Assert
        plain.ShouldContain(row => Equals(row[2], "RecursiveUnion"));
        plain.ShouldContain(row => Equals(row[2], "WorkTableScan"));
        string detail = string.Join(' ', analyzed.SelectMany(row => row).OfType<string>());
        detail.ShouldContain("iterations=3", Case.Sensitive);
        detail.ShouldContain("rows=3", Case.Sensitive);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var execution = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        execution.Status.ShouldBe(DatabaseResultStatus.Success, string.Join(';', execution.Diagnostics.Select(d => d.Message)));
        await using var result = execution.ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}

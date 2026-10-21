using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL bounded execution allocation")]
public sealed class SqlBoundedSelectionRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Top-N: a limit covering input does no heap comparisons (#1523)")]
    [InlineData(3)]
    [InlineData(20)]
    public void Add_LimitCoversInput_ShouldDeferHeapWork(int limit)
    {
        // Arrange
        int comparisons = 0;
        var ordering = new SqlBoundOrdering(new SqlBoundSlot(0), false, false, new SqlBoundCollation(Collation.Binary));
        var collector = new SqlRowCollector([ordering], null, new SqlRowCount(null, "LIMIT", null, limit), null, CancellationToken.None);

        // Act
        for (int value = 3; value > 0; value--)
        {
            collector.Add([value], [new CountingKey(value, () => comparisons++)]);
        }

        // Assert
        comparisons.ShouldBe(0);
        collector.Finish().Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        comparisons.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Empty ordering: deferred collations preserve empty results (#1523)")]
    [InlineData("SELECT id FROM t WHERE id < 0 ORDER BY " + DeferredKey + " LIMIT 1")]
    [InlineData("SELECT id, name FROM t GROUP BY id, name HAVING id < 0 ORDER BY ('x' IN (SELECT u.name COLLATE case_insensitive FROM t u)) || name LIMIT 1")]
    [InlineData("SELECT (SELECT u.id FROM t u WHERE u.id = t.id) FROM t WHERE id < 0 ORDER BY " + DeferredKey + " LIMIT 1")]
    public async Task ExecuteAsync_EmptyDeferredOrdering_ShouldPreserveEmptyResults(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("empty-deferred-ordering", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Empty deferred sort: collation resolution still reports its error (#1523)")]
    public void Finish_EmptyDeferredOrdering_ShouldResolveCollation()
    {
        // Arrange
        var failure = new DatabaseException("Deferred collation failure");
        var collation = new SqlBoundCollation(new SqlDeferredCollation(
            SqlCollationCandidate.Deferred(new SqlCollationFailureTerm(() => throw failure)), default, Collation.Binary));
        var collector = new SqlRowCollector([new SqlBoundOrdering(new SqlBoundSlot(0), false, false, collation)],
            null, new SqlRowCount(null, "LIMIT", null, 1), null, CancellationToken.None);

        // Act
        var actual = Should.Throw<DatabaseException>(() => collector.Finish());

        // Assert
        actual.ShouldBeSameAs(failure);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Deferred ordering: rejected trailing rows preserve qualifying collation (#1523)")]
    [InlineData("")]
    [InlineData(" LIMIT 1")]
    public async Task ExecuteAsync_DeferredOrderingWithRejectedTail_ShouldUseQualifyingCollation(string window)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("deferred-ordering", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "SELECT id FROM t WHERE id < 3 ORDER BY " + DeferredKey + window);

        // Assert
        rows.Select(row => row[0]).ShouldBe(window.Length == 0 ? new object?[] { 1, 2 } : [1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Deferred DISTINCT: rejected trailing rows preserve qualifying collation (#1523)")]
    public async Task ExecuteAsync_DeferredDistinctWithRejectedTail_ShouldUseQualifyingCollation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("deferred-distinct", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "SELECT DISTINCT " + DeferredKey + " FROM t WHERE id < 3");

        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { "FALSEa" });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Volatile output: independent projections follow sorted output order (#1523)")]
    [InlineData("tick(id)", "", 3)]
    [InlineData("tick(id)", " LIMIT 2", 2)]
    [InlineData("CASE WHEN id > 0 THEN COALESCE(tick(id), 0) ELSE 0 END", " LIMIT 2", 2)]
    public async Task ExecuteAsync_IndependentVolatileProjection_ShouldEvaluateInOutputOrder(string projection, string window, int count)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("volatile-output");
        builder.Functions.Add(SqlScalarFunction.Create("tick", (int value) => ++calls));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, $"SELECT {projection} AS n FROM t ORDER BY id DESC{window}");

        // Assert
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(Enumerable.Range(1, count).Select(value => (long)value));
        calls.ShouldBe(3); // Conservative full-sort fallback preserves the base's call count too.
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Final projection: cancellation and statement expiry survive early stop (#1523)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledFinalProjection_ShouldFailStatement(bool timeout)
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        Action cancel = caller.Cancel;
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("final-projection-cancellation");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_row", (int value) => { calls++; cancel(); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        CancellationToken statementToken = default;
        if (timeout)
        {
            session.StatementTimeout = TimeSpan.FromMinutes(1);
            session.StatementTimerArmed = token => statementToken = token;
            cancel = () =>
            {
                session.ExpireStatementTimerForTest();
                statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            };
        }

        // Act / Assert
        if (timeout)
        {
            var failure = await Should.ThrowAsync<SqlTimeoutException>(async () => await session.ExecuteAsync(
                "SELECT cancel_row(id) FROM t LIMIT 1", cancellationToken: caller.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE043");
            statementToken.IsCancellationRequested.ShouldBeTrue();
        }
        else
        {
            var failure = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "SELECT cancel_row(id) FROM t LIMIT 1", cancellationToken: caller.Token));
            failure.CancellationToken.ShouldBe(caller.Token);
        }
        calls.ShouldBe(1);
        session.StatementTimeout = TimeSpan.Zero;
        (await RowsAsync(session, "SELECT id FROM t LIMIT 1")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered selection: executor retention is bounded as input grows (#1523)")]
    [InlineData(1000)]
    [InlineData(4096)]
    public async Task ExecuteAsync_OrderedWindow_ShouldBoundRetainedRows(int size)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-retention", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        for (int start = 0; start < size; start += 128)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(start, Math.Min(128, size - start))
                .Select(id => $"({id})")), cancellationToken: CancellationToken.None);
        }

        // Act
        var rows = await RowsAsync(session, "SELECT id FROM t ORDER BY id DESC LIMIT 10 OFFSET 7");

        // Assert
        rows.Select(row => (int)row[0]!).ShouldBe(Enumerable.Range(size - 17, 10).Reverse());
        session.LastStatementMetrics.ShouldNotBeNull().PeakOrderedRows.ShouldBe(17);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(size);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered window: source keys, ordinals and boundary counts return fixed rows (#1523)")]
    [InlineData("SELECT name FROM t ORDER BY id DESC LIMIT 2", "c,A")]
    [InlineData("SELECT id, name FROM t ORDER BY 2 DESC, 1 ASC LIMIT 2", "3,1")]
    [InlineData("SELECT id FROM t ORDER BY id DESC LIMIT 20", "3,2,1")]
    [InlineData("SELECT id FROM t ORDER BY id DESC LIMIT 3", "3,2,1")]
    [InlineData("SELECT id FROM t ORDER BY id DESC LIMIT 1 OFFSET 20", "")]
    public async Task ExecuteAsync_OrderedWindowEdges_ShouldReturnExpectedRows(string sql, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-edges", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        string.Join(',', rows.Select(row => row[0])).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered window: grouped and correlated outputs stop projection and HAVING (#1523)")]
    [InlineData("SELECT observe(id) FROM t GROUP BY id HAVING observe(id) > 0 LIMIT 1", 2)]
    [InlineData("SELECT observe((SELECT u.id FROM t u WHERE u.id = t.id)) FROM t LIMIT 1", 1)]
    [InlineData("SELECT observe(a.id) FROM t a JOIN t b ON a.id = b.id LIMIT 1", 1)]
    [InlineData("SELECT observe(id) FROM (SELECT id FROM t UNION ALL SELECT id FROM t) u LIMIT 1", 1)]
    public async Task ExecuteAsync_UnorderedRelationShapes_ShouldStopOutputEvaluation(string sql, int expectedCalls)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("unordered-shapes");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1);
        calls.ShouldBe(expectedCalls);
    }

    private const string DeferredKey = "('x' IN (SELECT u.name COLLATE case_insensitive FROM t u WHERE u.id = t.id)) || name";

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t (id INT, name TEXT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,'a'),(2,'A'),(3,'c')", cancellationToken: CancellationToken.None);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private sealed class CountingKey : IComparable
    {
        private readonly int _value;
        private readonly Action _compared;

        internal CountingKey(int value, Action compared)
        {
            _value = value;
            _compared = compared;
        }

        public int CompareTo(object? obj)
        {
            _compared();
            return _value.CompareTo(((CountingKey)obj!)._value);
        }
    }
}

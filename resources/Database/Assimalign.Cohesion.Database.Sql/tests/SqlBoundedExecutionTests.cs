using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL bounded execution allocation")]
public sealed class SqlBoundedExecutionTests
{
    private readonly ITestOutputHelper _output;

    public SqlBoundedExecutionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIMIT: allocation is measured against the eager query (#1523)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LimitedQuery_ShouldBoundAllocation(bool ordered)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bounded-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, payload TEXT)", cancellationToken: CancellationToken.None);
        for (int start = 0; start < 4096; start += 256)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(start, 256)
                .Select(id => $"({id}, RPAD('x', 512, 'x'))")), cancellationToken: CancellationToken.None);
        }
        string query = "SELECT payload FROM t" + (ordered ? " ORDER BY id DESC" : "");
        Measure(query);
        Measure(query + " LIMIT 10");

        // Act
        long eager = long.MaxValue;
        long limited = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            eager = Math.Min(eager, Measure(query));
            limited = Math.Min(limited, Measure(query + " LIMIT 10"));
        }

        // Assert
        _output.WriteLine($"ordered={ordered}, eager={eager}, limited={limited}");
        if (!ordered) { limited.ShouldBeLessThan(eager / 10); }
        else { limited.ShouldBeLessThan(eager); }

        long Measure(string sql)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var pending = session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();
            pending.IsCompletedSuccessfully.ShouldBeTrue("The allocation probe must stay on the measuring thread.");
            var result = pending.GetAwaiter().GetResult();
            ((DatabaseResultSet)result).DisposeAsync().GetAwaiter().GetResult();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIMIT: scans visit only the requested qualifying window (#1523)")]
    [InlineData("LIMIT 0", 0, 0)]
    [InlineData("LIMIT 3", 6, 3)]
    [InlineData("LIMIT 3 OFFSET 2", 10, 3)]
    [InlineData("OFFSET 8", 20, 2)]
    public async Task ExecuteAsync_UnorderedWindow_ShouldStopAfterQualifyingRows(string window, int examined, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bounded-scan", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, 20).Select(id => $"({id})")), cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"SELECT id FROM t WHERE id % 2 = 0 {window}");

        // Assert
        rows.Count.ShouldBe(count);
        session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(examined);
        if (window == "LIMIT 3 OFFSET 2") { rows.Select(row => row[0]).ShouldBe(new object?[] { 6, 8, 10 }); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - top-N: retained rows stay bounded for growing input (#1523)")]
    [InlineData(1000)]
    [InlineData(100000)]
    public void Add_OrderedWindow_ShouldRetainOnlyOffsetPlusLimit(int size)
    {
        // Arrange
        var ordering = new SqlBoundOrdering(new SqlBoundSlot(0), false, false, new SqlBoundCollation(Collation.Binary));
        var collector = new SqlRowCollector([ordering], null,
            new SqlRowCount(null, "LIMIT", null, 10), new SqlRowCount(null, "OFFSET", null, 7), CancellationToken.None);
        int peak = 0;

        // Act
        for (int i = size - 1; i >= 0; i--)
        {
            collector.Add([i], [i]);
            peak = Math.Max(peak, collector.RetainedCount);
        }
        var rows = collector.Finish();

        // Assert
        peak.ShouldBe(17);
        rows.Select(row => (int)row[0]!).ShouldBe(Enumerable.Range(7, 10));
        _output.WriteLine($"input={size}, peak retained={peak}, output={rows.Count}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - sorting: cancellation in the last comparison survives an empty output window (#1523)")]
    public void Finish_CanceledLastComparison_ShouldPreserveCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var ordering = new SqlBoundOrdering(new SqlBoundSlot(0), false, false, new SqlBoundCollation(Collation.Binary));
        var collector = new SqlRowCollector([ordering], null, null,
            new SqlRowCount(null, "OFFSET", null, long.MaxValue), cancellation.Token);
        collector.Add([1], [new CancelingKey(cancellation)]);
        collector.Add([2], [new CancelingKey(cancellation)]);

        // Act
        var error = Should.Throw<OperationCanceledException>(() => collector.Finish());

        // Assert
        error.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - top-N: limited output matches stable full ordering across relation shapes (#1523)")]
    [InlineData("SELECT id, name AS label FROM t")]
    [InlineData("SELECT a.id, a.name AS label FROM t a JOIN t b ON a.id = b.id")]
    [InlineData("SELECT id, MAX(name) AS label FROM t GROUP BY id")]
    [InlineData("SELECT id, name AS label FROM t WHERE id < 5 UNION ALL SELECT id, name AS label FROM t WHERE id >= 5")]
    [InlineData("SELECT id, (SELECT u.name FROM t u WHERE u.id = t.id) AS label FROM t")]
    public async Task ExecuteAsync_OrderedWindow_ShouldMatchFullSort(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bounded-parity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, name TEXT COLLATE case_insensitive)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,'b'),(2,NULL),(3,'A'),(4,'a'),(5,'B'),(6,'c'),(7,NULL),(8,'b')", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string direction in new[] { "ASC NULLS FIRST", "ASC NULLS LAST", "DESC NULLS FIRST", "DESC NULLS LAST" })
        {
            string ordered = query + $" ORDER BY label {direction}";
            var all = await RowsAsync(session, ordered);
            var limited = await RowsAsync(session, ordered + " LIMIT 3 OFFSET 2");
            limited.Select(row => row[0]).ShouldBe(all.Skip(2).Take(3).Select(row => row[0]), ordered);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - top-N: DISTINCT completes before pagination (#1523)")]
    public async Task ExecuteAsync_DistinctWindow_ShouldKeepUniqueRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bounded-distinct", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (3),(3),(3),(2),(2),(1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await RowsAsync(session, "SELECT DISTINCT id FROM t ORDER BY id LIMIT 2 OFFSET 1")).Select(row => row[0]).ShouldBe(new object?[] { 2, 3 });
        (await RowsAsync(session, "SELECT DISTINCT id FROM t LIMIT 2 OFFSET 1")).Select(row => row[0]).ShouldBe(new object?[] { 2, 1 });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - top-N: ordering aliases evaluate volatile projections once per input (#1523)")]
    public async Task ExecuteAsync_VolatileAlias_ShouldEvaluateOnce()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("bounded-alias");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (5),(4),(3),(2),(1)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT observe(id) AS observed FROM t ORDER BY observed + 1 LIMIT 2");

        // Assert
        calls.ShouldBe(5);
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - top-N: cancellation during ordering preserves the statement token (#1523)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledKey_ShouldPreserveCancellation(bool correlated)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int keys = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("bounded-cancellation");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_key", (int value) =>
        {
            if (++keys == 5) { cancellation.Cancel(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (8),(7),(6),(5),(4),(3),(2),(1)", cancellationToken: CancellationToken.None);
        string projection = correlated ? "(SELECT u.id FROM t u WHERE u.id = t.id)" : "id";

        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            $"SELECT {projection} FROM t ORDER BY cancel_key(id) LIMIT 2", cancellationToken: cancellation.Token));

        // Assert
        keys.ShouldBe(5);
        error.CancellationToken.ShouldBe(cancellation.Token);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id LIMIT 1")).ShouldHaveSingleItem()[0].ShouldBe(1);
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

    private sealed class CancelingKey : IComparable
    {
        private readonly CancellationTokenSource _cancellation;

        internal CancelingKey(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int CompareTo(object? obj)
        {
            _cancellation.Cancel();
            return 0;
        }
    }
}

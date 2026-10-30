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
using Assimalign.Cohesion.Database.Transactions;

using static Assimalign.Cohesion.Database.Sql.Tests.SqlPartialIndexTests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOrderedIndexTests
{
    private readonly ITestOutputHelper _output;

    public SqlOrderedIndexTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered index oracle: mixed directions, NULLs, prefixes, ties and windows equal forced sorts (#1631)")]
    [InlineData(0, 3, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(2, 1, 3)]
    [InlineData(1, 2, 0)]
    public async Task ExecuteAsync_GeneratedOrderings_ShouldEqualForcedSort(int first, int second, int third)
    {
        // Arrange: duplicate ordering keys and collation-equivalent strings are deliberate.
        await using var engine = SqlDatabaseEngine.Create("ordered-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] names = ["a", "b", "c", "n", "id"];
        int[] directions = [first, second, third, 3, 0];
        var data = Enumerable.Range(0, 144).Select(i => $"({i},{Value(i % 4)},{Value(i / 4 % 4)},{Value(i / 16 % 4)}," +
            (i % 7 == 0 ? "NULL" : i % 2 == 0 ? "'Ab'" : "'ab'") + ")").ToArray();
        foreach (string table in new[] { "t", "plain" })
        {
            await Execute(session, "CREATE TABLE " + table + "(id INT,a INT,b INT,c INT,n TEXT COLLATE case_insensitive)");
            await Execute(session, "INSERT INTO " + table + " VALUES" + string.Join(',', data));
        }
        await Execute(session, "CREATE INDEX ix ON t(" + Order(0, false, true) + ")");
        int cases = 0;

        // Act / Assert: a unique last key pins exact row identity across LIMIT/OFFSET;
        // omitting it compares only the SQL-defined ordering values across tied rows.
        foreach (var (predicate, prefix) in new[] { ("", 0), (" WHERE a=0", 1), (" WHERE a=0 AND b=0", 2),
            (" WHERE a IS NULL", 1), (" WHERE a=0 AND b IS NULL", 2) })
        {
            foreach (bool reverse in new[] { false, true })
            {
                foreach (bool unique in new[] { false, true })
                {
                    foreach (string window in new[] { " LIMIT 7 OFFSET 3", " LIMIT 0", " LIMIT 200 OFFSET 1", " OFFSET 1000" })
                    {
                        string order = Order(prefix, reverse, unique);
                        string projection = unique ? "id" : string.Join(',', names.Skip(prefix).Take(4 - prefix));
                        string query = "SELECT " + projection + " FROM {0}" + predicate + " ORDER BY " + order + window;
                        var plan = Plan(database, string.Format(query, "t"));
                        plan.Access.ShouldBeOfType<SqlOrderedIndexPath>().Reverse.ShouldBe(reverse);
                        var expected = await Rows(session, string.Format(query, "plain"));
                        var actual = await Rows(session, string.Format(query, "t"));
                        actual.Select(row => row.Select(value => value is string text ? text.ToUpperInvariant() : value).ToArray()).ToArray()
                            .ShouldBe(expected.Select(row => row.Select(value => value is string text ? text.ToUpperInvariant() : value).ToArray()).ToArray(), query);
                        cases++;
                    }
                }
            }
        }
        var explanation = await Rows(session, "EXPLAIN SELECT id FROM t WHERE a IS NULL ORDER BY " + Order(1, false, true) + " LIMIT 2");
        explanation.ShouldContain(row => Equals(row[2], "OrderedIndexScan") && (row[5] as string ?? "").Contains("a IS NULL", StringComparison.Ordinal));
        _output.WriteLine($"Ordered oracle: {cases} windows per direction vector; forced-sort and index results agree.");

        string Order(int prefix, bool reverse, bool unique) => string.Join(',', names.Skip(prefix).Take((unique ? 5 : 4) - prefix)
            .Select((name, i) => name + Suffix(directions[prefix + i] ^ (reverse ? 3 : 0))));
        static string Value(int value) => value == 0 ? "NULL" : (value - 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered access: incompatible expressions, collations and partial reversals retain sorts (#1631)")]
    [InlineData("a ASC NULLS LAST,b ASC NULLS FIRST")]
    [InlineData("a ASC NULLS FIRST,b DESC NULLS FIRST")]
    [InlineData("a+0 ASC NULLS LAST,b DESC NULLS FIRST")]
    [InlineData("b DESC NULLS FIRST")]
    [InlineData("n COLLATE binary DESC NULLS FIRST")]
    public async Task Plan_IncompatibleOrder_ShouldKeepSort(string order)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-mismatch", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(a INT,b INT,n TEXT COLLATE case_insensitive)");
        await Execute(session, "CREATE INDEX ix ON t(a ASC,b DESC,n DESC)");

        // Act / Assert
        Plan(database, "SELECT * FROM t ORDER BY " + order + " LIMIT 2").Access.ShouldNotBeOfType<SqlOrderedIndexPath>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered access: aliases, ordinals and matching COLLATE follow source-column identity (#1631)")]
    [InlineData("SELECT b AS v FROM t WHERE a=1 ORDER BY v DESC NULLS FIRST", true)]
    [InlineData("SELECT b AS v FROM t WHERE a=1 ORDER BY 1 ASC NULLS LAST", true)]
    [InlineData("SELECT b+1 AS b FROM t WHERE a=1 ORDER BY b DESC NULLS FIRST", false)]
    [InlineData("SELECT n FROM t WHERE a=1 AND b=2 ORDER BY n COLLATE case_insensitive DESC NULLS FIRST", true)]
    [InlineData("SELECT n FROM t WHERE a=1 AND b=2 ORDER BY n COLLATE binary DESC NULLS FIRST", false)]
    public async Task Plan_OutputBindings_ShouldRequirePassThroughColumns(string sql, bool matches)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-output", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(a INT,b INT,n TEXT COLLATE case_insensitive)");
        await Execute(session, "CREATE INDEX ix ON t(a,b DESC,n DESC)");

        // Act / Assert
        (Plan(database, sql + " LIMIT 2").Access is SqlOrderedIndexPath).ShouldBe(matches);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered costing: top-N and selective access win while a broad heap-fetch path loses (#1631)")]
    public async Task ExecuteAsync_OrderCostAndTopN_ShouldBoundWorkAndRemoveSort()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-cost", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,k INT)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 1024).Select(i => $"({i},{1023-i})")));
        await Execute(session, "CREATE INDEX ix ON t(k)");

        // Act / Assert
        var expected = await Rows(session, "SELECT id FROM t ORDER BY k+0 ASC NULLS LAST LIMIT 7 OFFSET 3");
        var sorted = session.LastStatementMetrics!;
        var actual = await Rows(session, "SELECT id FROM t ORDER BY k ASC NULLS LAST LIMIT 7 OFFSET 3");
        var ordered = session.LastStatementMetrics!;
        actual.ShouldBe(expected);
        sorted.RecordsExamined.ShouldBe(1024);
        ordered.RecordsExamined.ShouldBe(10);
        sorted.PeakOrderedRows.ShouldBe(10);
        ordered.PeakOrderedRows.ShouldBe(0);
        ordered.AccessPath.ShouldBe("ordered:ix:forward");
        _output.WriteLine($"Top-N before/after: records {sorted.RecordsExamined}/{ordered.RecordsExamined}; retained sort rows {sorted.PeakOrderedRows}/{ordered.PeakOrderedRows}.");

        await Rows(session, "SELECT id FROM t WHERE k>=1010 ORDER BY k ASC NULLS LAST");
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(14);
        await Rows(session, "SELECT id FROM t ORDER BY k ASC NULLS LAST");
        session.LastStatementMetrics!.AccessPath.ShouldBe("scan");
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(1024);

        // A mostly qualifying inequality keeps the ordered walk profitable after selectivity costing.
        expected = await Rows(session, "SELECT id FROM t WHERE id>=18 ORDER BY k+0 DESC NULLS FIRST LIMIT 7 OFFSET 3");
        actual = await Rows(session, "SELECT id FROM t WHERE id>=18 ORDER BY k DESC NULLS FIRST LIMIT 7 OFFSET 3");
        actual.ShouldBe(expected);
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:backward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(28);
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        var explain = await Rows(session, "EXPLAIN SELECT id FROM t ORDER BY k DESC NULLS FIRST LIMIT 7 OFFSET 3");
        explain.ShouldContain(row => Equals(row[2], "OrderedIndexScan") && (row[5] as string ?? "").Contains("direction=backward; sort_removed=true", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial ordered scan: implication authorizes order and EXPLAIN retains its proof (#1631)")]
    public async Task ExecuteAsync_PartialOrder_ShouldRequireAndExplainProof()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-partial", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,active BOOLEAN,k INT)");
        await Execute(session, "INSERT INTO t VALUES(1,TRUE,NULL),(2,TRUE,7),(3,FALSE,8),(4,TRUE,9),(5,NULL,10)");
        await Execute(session, "CREATE INDEX ix ON t(k DESC NULLS LAST) WHERE active");

        // Act / Assert
        Plan(database, "SELECT id FROM t ORDER BY k DESC NULLS LAST LIMIT 3").Access.ShouldBeOfType<SqlScanPath>();
        Plan(database, "SELECT id FROM t WHERE active OR id=3 ORDER BY k DESC NULLS LAST LIMIT 3").Access.ShouldBeOfType<SqlScanPath>();
        (await Rows(session, "SELECT id FROM t WHERE active ORDER BY k DESC NULLS LAST LIMIT 3")).Select(row => row[0]).ShouldBe([4, 2, 1]);
        session.LastStatementMetrics!.PeakOrderedRows.ShouldBe(0);
        var explain = await Rows(session, "EXPLAIN SELECT id FROM t WHERE active ORDER BY k ASC NULLS FIRST LIMIT 3");
        explain.ShouldContain(row => Equals(row[2], "OrderedIndexScan") && (row[5] as string ?? "").Contains("partial_predicate=", StringComparison.Ordinal) && (row[5] as string ?? "").Contains("implied_by=", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered fallback: a detached same-name replacement retains the sorted result (#1631)")]
    public async Task ExecuteAsync_DetachedOrderPlan_ShouldRestoreSort()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-detached", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,k INT)");
        await Execute(session, "INSERT INTO t VALUES(1,3),(2,1),(3,2)");
        await Execute(session, "CREATE INDEX ix ON t(k)");
        var plan = Plan(database, "SELECT id FROM t ORDER BY k ASC NULLS LAST LIMIT 2");
        plan.Access.ShouldBeOfType<SqlOrderedIndexPath>();
        await Execute(session, "DROP INDEX ix ON t");
        await Execute(session, "CREATE INDEX ix ON t(id DESC)");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var context = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            await using var result = (await executor.ExecuteAsync(plan, context, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = new List<object?>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row.GetValue(0)); }

            // Assert
            rows.ShouldBe([2, 3]);
            context.Metrics.AccessPath.ShouldBe("scan");
            context.Metrics.PeakOrderedRows.ShouldBe(2);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    internal static SqlSelectPlan Plan(SqlDatabase database, string sql)
        => new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlSelectPlan>();

    private static string Suffix(int order) => ((order & 2) != 0 ? " DESC" : " ASC") + ((order & 1) != 0 ? " NULLS FIRST" : " NULLS LAST");
}

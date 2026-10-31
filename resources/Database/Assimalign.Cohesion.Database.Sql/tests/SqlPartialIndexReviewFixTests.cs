using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlPartialIndexReviewFixTests
{
    public static IEnumerable<object[]> SignedZeroCases()
    {
        foreach (string type in new[] { "REAL", "DOUBLE PRECISION" })
        {
            foreach (string zero in new[] { "0", "0.0" })
            {
                foreach (string operation in new[] { "SELECT", "UPDATE", "DELETE", "MERGE" })
                { yield return [type, zero, operation]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial join access: converted zero bounds preserve negative zero rows (#1630)")]
    [MemberData(nameof(SignedZeroCases))]
    public async Task ExecuteAsync_ConvertedZeroJoinBound_ShouldPreserveMatchingRows(string type, string zero, string operation)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-zero-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, $"CREATE TABLE m(k INT,f {type},flag BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX m_f ON m(f) WHERE flag");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE o(k INT)");
        await SqlPartialIndexTests.Execute(session, $"INSERT INTO m VALUES(1,CAST('-0' AS {type}),TRUE)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO o VALUES(1)");
        string condition = $"m.flag AND m.f={zero}";
        string sql = operation switch
        {
            "SELECT" => $"SELECT m.k FROM m JOIN o ON o.k=m.k WHERE {condition}",
            "UPDATE" => $"UPDATE m SET k=2 FROM o WHERE o.k=m.k AND {condition} RETURNING m.k",
            "DELETE" => $"DELETE FROM m USING o WHERE o.k=m.k AND {condition} RETURNING m.k",
            _ => $"MERGE INTO m USING o ON o.k=m.k AND {condition} WHEN MATCHED THEN UPDATE SET k=2 RETURNING m.k",
        };

        // Act
        var rows = await SqlPartialIndexTests.Rows(session, sql);

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(operation is "UPDATE" or "MERGE" ? 2 : 1);
        var stored = await SqlPartialIndexTests.Rows(session, "SELECT k FROM m");
        if (operation == "DELETE") { stored.ShouldBeEmpty(); }
        else { stored.ShouldHaveSingleItem()[0].ShouldBe(operation is "UPDATE" or "MERGE" ? 2 : 1); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial join access: unused partial indexes retain ordinary input plans (#1630)")]
    [InlineData("SELECT m.k FROM (m JOIN o ON o.k=m.k) WHERE m.k=1 AND o.k=1")]
    [InlineData("SELECT m.k FROM (m JOIN o ON o.k=m.k) WHERE o.k=1")]
    public async Task Plan_UnusedPartialIndex_ShouldRetainOriginalAccessPaths(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-plan-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE m(k INT,flag BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE o(k INT)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX m_k ON m(k)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX o_k ON o(k)");
        var expected = Canonical(await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query));
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX unused ON m(k) WHERE flag");

        // Act
        var actual = Canonical(await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query));

        // Assert
        actual.ShouldNotContain("partial_predicate=", Case.Sensitive);
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial inference conformance: invalid target calls refuse before writing (#1630)")]
    [InlineData("b AND missing_function(k)>0", "CS01016")]
    [InlineData("b AND ABS(DISTINCT k)>0", "CS05032")]
    [InlineData("b AND random()>0", "CS01016")]
    [InlineData("b AND missing_function(k)>0 AND EXISTS(SELECT 1)", "CS05092")]
    public async Task ExecuteAsync_InvalidInferenceCall_ShouldRefuseWithoutWriting(string predicate, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-validation-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX uq ON t(k) WHERE b");

        // Act / Assert
        await SqlPartialIndexTests.Refuses(session,
            $"INSERT INTO t VALUES(1,TRUE) ON CONFLICT(k) WHERE {predicate} DO NOTHING", code);
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBeEmpty();
        if (code != "CS05092")
        { await SqlPartialIndexTests.Refuses(session, $"SELECT k FROM t WHERE {predicate}", code); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial proof conformance: folded parameter calls cannot supply membership witnesses (#1630)")]
    [InlineData("UPDATE t SET b=TRUE WHERE k>CAST(ABS(@p) AS INT)")]
    [InlineData("SELECT t.k FROM t JOIN o ON t.k=o.k WHERE t.k>CAST(ABS(@p) AS INT)")]
    [InlineData("SELECT t.k FROM t JOIN o ON t.k=o.k AND t.k>CAST(ABS(ABS(@p)) AS INT)")]
    [InlineData("SELECT t.k FROM t JOIN o ON t.k=o.k WHERE t.k>-(-ABS(@p))")]
    public async Task Plan_FoldedParameterWitness_ShouldDeclinePartialIndexAndMatchScans(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-parameter-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE o(k INT)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,FALSE),(7,FALSE),(11,FALSE),(20,FALSE)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO o VALUES(1),(7),(11),(20)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(k) WHERE k>5");
        var parameters = new Dictionary<string, object?> { ["p"] = 10 };
        await SqlPartialIndexTests.Execute(session, "BEGIN");
        var expected = await RunAsync();
        await SqlPartialIndexTests.Execute(session, "ROLLBACK");
        var plan = Canonical(await RowsAsync("EXPLAIN " + sql));
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");

        // Act
        var actual = await RunAsync();

        // Assert
        plan.ShouldNotContain("partial_predicate=", Case.Sensitive);
        actual.ShouldBe(expected);

        async Task<string> RunAsync()
        {
            if (sql.StartsWith("UPDATE", StringComparison.Ordinal))
            {
                (await session.ExecuteAsync(SqlQueryRequest.FromSql(sql, parameters), CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
                return Canonical(await SqlPartialIndexTests.Rows(session, "SELECT k,b FROM t ORDER BY k"));
            }
            return Canonical(await RowsAsync(sql));
        }

        async Task<object?[][]> RowsAsync(string query)
        {
            await using var result = (await session.ExecuteAsync(SqlQueryRequest.FromSql(query, parameters), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
            return rows.ToArray();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial expression conformance: representation-sensitive constants cannot prove different expressions (#1630)")]
    // Bare fractional literals normalize scale; typmod casts retain the differing decimal bits.
    [InlineData("CAST(1.001 AS DECIMAL(4,2))", "CAST(1.01 AS DECIMAL(4,1))")]
    [InlineData("CAST('-0' AS REAL)", "CAST('0' AS REAL)")]
    [InlineData("CAST('-0' AS DOUBLE PRECISION)", "CAST('0' AS DOUBLE PRECISION)")]
    [InlineData("CAST('2020-01-01 01:00:00+01:00' AS TIMESTAMP WITH TIME ZONE)", "CAST('2020-01-01 00:00:00+00:00' AS TIMESTAMP WITH TIME ZONE)")]
    public async Task ExecuteAsync_DifferentConstantRepresentations_ShouldMatchIndexFreeResults(string indexed, string queried)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-representation-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,name TEXT)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,'x')");
        string text = (string)(await SqlPartialIndexTests.Rows(session, $"SELECT name || {queried} FROM t"))[0][0]!;
        (await SqlPartialIndexTests.Rows(session, $"SELECT name || {indexed} FROM t"))[0][0].ShouldNotBe(text);
        string comparison = "='" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        string query = $"SELECT k FROM t WHERE name || {queried}{comparison} AND k=1";
        var expected = Canonical(await SqlPartialIndexTests.Rows(session, query));
        await SqlPartialIndexTests.Execute(session, $"CREATE INDEX ix ON t(k) WHERE name || {indexed}{comparison}");

        // Act
        var actual = Canonical(await SqlPartialIndexTests.Rows(session, query));
        var plan = Canonical(await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query));

        // Assert
        expected.ShouldBe("1");
        actual.ShouldBe(expected);
        plan.ShouldNotContain("partial_predicate=", Case.Sensitive);
    }

    private static string Canonical(IEnumerable<object?[]> rows)
        => string.Join("\n", rows.Select(row => string.Join('|', row.Select(value => value?.ToString() ?? "NULL"))));
}

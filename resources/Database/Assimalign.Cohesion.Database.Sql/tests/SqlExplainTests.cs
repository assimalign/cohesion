using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlExplainTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN: each ordering key describes bound direction and NULL placement")]
    [InlineData("SELECT id FROM t ORDER BY age {0}, id DESC")]
    [InlineData("SELECT q.id FROM (SELECT id, age FROM t) q ORDER BY q.age {0}, q.id DESC")]
    [InlineData("SELECT t.id FROM t JOIN t q ON t.id = q.id ORDER BY q.age {0}, t.id DESC")]
    [InlineData("SELECT age, COUNT(*) FROM t GROUP BY age ORDER BY age {0}, 2 DESC")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA {0}, TABLE_NAME DESC")]
    [InlineData("SELECT id FROM t UNION ALL SELECT id FROM t ORDER BY id {0}, 1 DESC")]
    [InlineData("WITH q AS (SELECT 1 AS id ORDER BY id {0}, id DESC) SELECT id FROM q")]
    public async Task Execute_NullPlacement_ShouldDescribeEachOrderingKeyAsync(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("explain-null-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, age INT)", cancellationToken: CancellationToken.None);

        // Act
        var first = await RowsAsync(session, "EXPLAIN " + string.Format(System.Globalization.CultureInfo.InvariantCulture, query, "ASC NULLS FIRST"));
        var last = await RowsAsync(session, "EXPLAIN " + string.Format(System.Globalization.CultureInfo.InvariantCulture, query, "ASC NULLS LAST"));
        string firstDetail = first.Single(row => row[5] is string detail && detail.Contains("order_keys=2", StringComparison.Ordinal))[5].ShouldBeOfType<string>();
        string lastDetail = last.Single(row => row[5] is string detail && detail.Contains("order_keys=2", StringComparison.Ordinal))[5].ShouldBeOfType<string>();

        // Assert
        firstDetail.ShouldContain("order_by=[ASC NULLS FIRST, DESC NULLS LAST]", Case.Sensitive);
        lastDetail.ShouldContain("order_by=[ASC NULLS LAST, DESC NULLS LAST]", Case.Sensitive);
        firstDetail.ShouldNotBe(lastDetail);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN: one derived source has a Select root and multiple sources retain join steps")]
    [InlineData("SELECT * FROM (SELECT id FROM t) d", "Select", 0)]
    [InlineData("SELECT * FROM t JOIN (SELECT id FROM t) d ON t.id = d.id", "NestedLoopJoin", 1)]
    public async Task Execute_DerivedSource_ShouldDescribeRelationShape(string sql, string root, int steps)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("explain-derived", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "EXPLAIN " + sql);

        // Assert
        rows[0][2].ShouldBe(root);
        rows[0][5].ShouldBeOfType<string>().StartsWith("; ", StringComparison.Ordinal).ShouldBeFalse();
        rows.Count(row => Equals(row[2], "JoinStep")).ShouldBe(steps);
        rows.Count(row => Equals(row[2], "DerivedTable")).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN: chosen relational operators and index bounds are described")]
    [InlineData("SELECT id FROM t", "TableScan", false)]
    [InlineData("SELECT id FROM t WHERE age >= 20 AND age < 40", "IndexSeek", true)]
    [InlineData("SELECT t.id FROM t JOIN u ON t.id = u.id", "NestedLoopJoin", false)]
    [InlineData("SELECT t.id FROM t JOIN u ON t.id = u.id", "IndexSeek", true)]
    [InlineData("SELECT age, COUNT(*) FROM t GROUP BY age", "Group", false)]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES", "SystemView", false)]
    [InlineData("SELECT s.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES s JOIN INFORMATION_SCHEMA.COLUMNS c ON s.TABLE_NAME = c.TABLE_NAME", "SystemView", false)]
    [InlineData("SELECT t.id FROM t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.id = c.ORDINAL_POSITION", "SystemView", false)]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT id FROM u)", "Subqueries", false)]
    [InlineData("SELECT t.id FROM t WHERE EXISTS (SELECT u.id FROM u WHERE u.id = t.id)", "CorrelatedSubqueries", false)]
    [InlineData("INSERT INTO t VALUES (3, 30)", "Insert", false)]
    [InlineData("INSERT INTO t SELECT id, age FROM t", "InsertSelect", false)]
    [InlineData("UPDATE t SET age = 99 WHERE id = 1", "Update", false)]
    [InlineData("DELETE FROM t WHERE id = 1", "Delete", false)]
    public async Task Execute_DataPlan_ShouldDescribeChosenOperatorsAsync(string statement, string expected, bool indexes)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-explain", new());
        var database = await engine.CreateDatabaseAsync("plans", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, age INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u (id INT);", cancellationToken: CancellationToken.None);
        if (indexes)
        {
            await session.ExecuteAsync("CREATE INDEX by_age ON t (age);", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("CREATE INDEX by_id ON u (id);", cancellationToken: CancellationToken.None);
        }
        // Act
        var rows = await RowsAsync(session, "EXPLAIN " + statement + ";");
        // Assert
        rows.ShouldContain(row => Equals(row[2], expected));
        if (expected == "CorrelatedSubqueries")
        {
            rows.Count(row => Equals(row[2], "TableScan")).ShouldBe(2);
            rows.ShouldContain(row => Equals(row[2], "TableScan") && Equals(row[3], "dbo.u"));
        }
        if (statement.Contains("age >=", StringComparison.Ordinal))
        {
            var seek = rows.Single(row => Equals(row[2], "IndexSeek"));
            seek[3].ShouldBe("dbo.t");
            seek[4].ShouldBe("by_age");
            seek[5].ShouldBe("age >= 20 AND age < 40");
        }
        if (indexes && statement.Contains("JOIN", StringComparison.Ordinal))
        {
            var seek = rows.Single(row => Equals(row[2], "IndexSeek"));
            seek[4].ShouldBe("by_id");
            seek[5].ShouldBe("id = t.id");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN: mutations leave rows unchanged through autocommit and explicit commit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ExplainedWrites_ShouldLeaveDataUnchangedAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-explain-writes", new());
        var database = await engine.CreateDatabaseAsync("writes", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, age INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 20);", cancellationToken: CancellationToken.None);
        if (explicitTransaction) { await session.ExecuteAsync("BEGIN;", cancellationToken: CancellationToken.None); }
        // Act
        foreach (string write in new[] { "INSERT INTO t VALUES (2, 30)", "INSERT INTO t SELECT * FROM t", "UPDATE t SET age = 99", "DELETE FROM t" })
        { await RowsAsync(session, "EXPLAIN " + write + ";"); }
        if (explicitTransaction) { await session.ExecuteAsync("COMMIT;", cancellationToken: CancellationToken.None); }
        // Assert
        var rows = await RowsAsync(session, "SELECT * FROM t;");
        rows.ShouldHaveSingleItem().ShouldBe([1, 20]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - EXPLAIN: every concrete plan record has a closed describe mapping")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only enumeration of the closed plan family; shipped descriptions use pattern matching.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test-only uninitialized records exercise type dispatch without evaluating record fields.")]
    public void Describe_EveryPlanRecord_ShouldHaveMapping()
    {
        // Arrange / Act
        var types = typeof(SqlPlan).Assembly.GetTypes().Where(type => !type.IsAbstract && typeof(SqlPlan).IsAssignableFrom(type)).ToArray();
        // Assert
        types.ShouldNotBeEmpty();
        foreach (var type in types)
        { SqlPlanner.DescribeOperator((SqlPlan)RuntimeHelpers.GetUninitializedObject(type)).ShouldNotBeNullOrEmpty(type.Name); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN: DDL records retain mappings while DDL prefixes remain refused")]
    [InlineData("CREATE TABLE new_table (id INT)", "CreateTable")]
    [InlineData("DROP TABLE t", "DropTable")]
    [InlineData("ALTER TABLE t ADD a INT", "AddColumn")]
    [InlineData("ALTER TABLE t ALTER COLUMN age INT", "AlterColumn")]
    [InlineData("ALTER TABLE t DROP age", "DropColumn")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK (age > 0)", "AddConstraint")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT ck", "DropConstraint")]
    [InlineData("CREATE INDEX ix ON t (age)", "CreateIndex")]
    [InlineData("DROP INDEX ix ON t", "DropIndex")]
    [InlineData("CREATE DOMAIN age_type AS INT", "Definition")]
    public async Task Describe_DdlRecord_ShouldMapWithoutExecutingAsync(string statement, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-explain-ddl", new());
        var database = await engine.CreateDatabaseAsync("ddl", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, age INT, CONSTRAINT ck CHECK (age > 0));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON t (age);", cancellationToken: CancellationToken.None);
        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(statement + ";").Statement.SqlExpression);
        await using var description = SqlPlanner.Describe(plan);
        // Assert
        var rows = new List<DatabaseRow>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        rows[0].GetString(2).ShouldBe(expected);
        new SqlQueryParser().Parse("EXPLAIN " + statement).Diagnostics.ShouldContain(d => d.Code == "COHDBL001");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - EXPLAIN: binary seek bounds preserve the bytes as hexadecimal")]
    public async Task Execute_BinaryParameterSeek_ShouldDescribeBytesAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-explain-bytes", new());
        var database = await engine.CreateDatabaseAsync("bytes", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (key_bytes VARBINARY(4));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_bytes ON t (key_bytes);", cancellationToken: CancellationToken.None);
        // Act
        await using var result = (await session.ExecuteAsync("EXPLAIN SELECT key_bytes FROM t WHERE key_bytes = @key;",
            new Dictionary<string, object?> { ["key"] = new byte[] { 0, 0xAB, 0xFF } }, CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var bounds = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            if (row.GetString(2) == "IndexSeek") { bounds.Add(row.GetString(5)); }
        }
        // Assert
        bounds.ShouldBe(["key_bytes = X'00ABFF'"]);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string source)
    {
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}

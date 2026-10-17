using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDerivedTableExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: clauses joins and nesting compose")]
    [InlineData("SELECT d.renamed FROM (SELECT id FROM t WHERE id > 1 ORDER BY id DESC LIMIT 1 OFFSET 1) d (renamed)", 2)]
    [InlineData("SELECT d.id FROM (SELECT id FROM t GROUP BY id HAVING COUNT(*) = 1) d WHERE d.id = 2", 2)]
    [InlineData("SELECT d.id FROM t base JOIN (SELECT id FROM t) d ON base.id = d.id WHERE base.id = 2", 2)]
    [InlineData("SELECT d.id FROM t base, (SELECT id FROM t) d WHERE base.id = d.id AND d.id = 2", 2)]
    [InlineData("SELECT b.id FROM (SELECT id FROM t) a JOIN (SELECT * FROM (SELECT id FROM t) nested) b ON a.id = b.id WHERE a.id = 2", 2)]
    [InlineData("SELECT CAST(SUM(d.id) AS BIGINT) FROM (SELECT id FROM t WHERE id > 1) d", 5L)]
    [InlineData("SELECT COUNT(*) FROM (SELECT id FROM t WHERE id < 0) d", 0L)]
    [InlineData("SELECT (SELECT MAX(id) FROM (SELECT id FROM t) inner_d) FROM (SELECT id FROM t LIMIT 1) outer_d", 3)]
    public async Task Execute_DerivedRelation_ShouldApplyRelationalOperators(string sql, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([expected]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: empty metadata and collations survive renaming")]
    public async Task Execute_DerivedOutputs_ShouldPreserveMetadataAndCollations()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-types", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (name VARCHAR(12) COLLATE case_insensitive, amount DECIMAL(9,2));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES ('Ada', 2.50);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE plain_names (name TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO plain_names VALUES ('ADA');", cancellationToken: CancellationToken.None);

        // Act
        await using var empty = (DatabaseResultSet)await session.ExecuteAsync("SELECT d.label, d.cost FROM (SELECT name, amount FROM t WHERE 1 = 0) d (label, cost);", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT d.label FROM (SELECT name FROM t) d (label) WHERE d.label = 'ADA';");
        var joined = await RowsAsync(session, "SELECT d.name FROM (SELECT name FROM plain_names) d JOIN t ON d.name = t.name;");
        var query = SqlQueryRequest.FromSql("SELECT * FROM (SELECT name, amount FROM t) d;");
        var plan = new SqlPlanner(database.Catalog, null).Plan(query.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var grouped = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT * FROM (SELECT amount FROM t GROUP BY amount) d;").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Assert
        empty.Columns.Select(c => c.Name).ShouldBe(["label", "cost"]);
        empty.Columns.Select(c => c.Type).ShouldBe([DatabaseType.String, DatabaseType.Decimal]);
        empty.Columns[0].MaxLength.ShouldBe(12);
        (await ReadAsync(empty)).ShouldBeEmpty();
        rows.ShouldHaveSingleItem().ShouldBe(["Ada"]);
        joined.ShouldHaveSingleItem().ShouldBe(["ADA"]);
        plan.Bindings[0].Source.Columns[1].Type.Precision.ShouldBe(9);
        plan.Bindings[0].Source.Columns[1].Type.Scale.ShouldBe(2);
        grouped.Bindings[0].Source.Columns[0].Type.Precision.ShouldBe(9);
        grouped.Bindings[0].Source.Columns[0].Type.Scale.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: invalid names and shapes fail before enumeration")]
    [InlineData("SELECT * FROM (SELECT id FROM t WHERE 1 = 0) d (a, b)", "COHSQLE013")]
    [InlineData("SELECT t.id FROM (SELECT id FROM t) d", "COHSQLE015")]
    [InlineData("SELECT bogus.x FROM (SELECT id FROM t) d (x)", "COHSQLE015")]
    [InlineData("SELECT d.id FROM (SELECT id, id FROM t) d", "COHSQLE015")]
    [InlineData("SELECT * FROM t, (SELECT missing FROM t) d", "COHDBL001")]
    public async Task Execute_InvalidDerivedRelation_ShouldRejectBeforeReading(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);

        // Act / Assert: unsupported binding keeps the engine's existing error-result contract.
        if (code == "COHDBL001")
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            result.Status.ShouldBe(DatabaseResultStatus.Error);
            var diagnostic = result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(code);
            diagnostic.Message!.ShouldContain("LATERAL", Case.Sensitive);
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: system relations share auto-commit catalog capture")]
    public async Task Execute_SystemDerivedTable_ShouldJoinCapturedCatalog()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-catalog", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "SELECT d.name, t.id FROM (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES) d (name) JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_NAME = d.name JOIN t ON t.id = 2 WHERE d.name = 't';");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(["t", 2]);
        await using var explanation = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT * FROM (SELECT id FROM t) d;", cancellationToken: CancellationToken.None);
        var operators = await ReadAsync(explanation);
        operators.ShouldContain(row => Equals(row[2], "DerivedTable"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: empty LEFT inputs and INSERT sources preserve shape")]
    public async Task Execute_EmptyDerivedAndInsertSource_ShouldPreserveTypedRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-insert", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE copied (id INT, absent INT);", cancellationToken: CancellationToken.None);

        // Act
        var insert = await session.ExecuteAsync("INSERT INTO copied SELECT t.id, d.id FROM t LEFT JOIN (SELECT id FROM t WHERE 1 = 0) d ON t.id = d.id;", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT id, absent FROM copied ORDER BY id;");

        // Assert
        insert.AffectedCount.ShouldBe(3);
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe([1, null]);
        rows[1].ShouldBe([2, null]);
        rows[2].ShouldBe([3, null]);
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2), (3);", cancellationToken: CancellationToken.None);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}

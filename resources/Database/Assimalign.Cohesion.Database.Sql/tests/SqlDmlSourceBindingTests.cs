using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlSourceBindingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: refusal precedes source and RETURNING callbacks and preserves the transaction (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SourceCallbacks_ShouldRefuseBeforeEvaluation(bool delete)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-callbacks");
        builder.Functions.Add(SqlScalarFunction.Create("source_probe", (long value) => { calls++; return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        string sql = "WITH q AS (SELECT source_probe(id) AS id FROM t) " + (delete
            ? "DELETE FROM t USING q WHERE EXISTS (SELECT source_probe(q.id)) RETURNING source_probe(t.id)"
            : "UPDATE t SET id=q.id FROM q WHERE EXISTS (SELECT source_probe(q.id)) RETURNING source_probe(t.id)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        calls.ShouldBe(0);
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
        await using var rows = (await session.ExecuteAsync("SELECT id FROM t", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var ids = new List<int>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { ids.Add(row.GetInt32(0)); }
        ids.ShouldBe([1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: refusal keeps its code and the wire connection usable (#1714)")]
    [InlineData("UPDATE users SET name=s.name FROM users AS s WHERE users.id=s.id RETURNING s.name")]
    [InlineData("DELETE FROM users USING users AS s WHERE users.id=s.id RETURNING s.name")]
    public async Task ExecuteAsync_OverWire_ShouldRefuseWithoutMutation(string sql)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "dml-sources", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: TestTimeout.Token()))
            .Select(row => row["id"]).ShouldBe([1, 2]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: SET and RETURNING bind to physical source slots and retain target identity (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_SourceColumns_ShouldRetainTargetIdentity(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-binding", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, label VARCHAR(23))", cancellationToken: CancellationToken.None);
        string sql = delete
            ? "DELETE FROM t AS x USING s LEFT JOIN t AS y ON s.id=y.id WHERE x.id=s.id RETURNING *, s.label AS source_label, y.v"
            : "UPDATE t AS x SET v=y.v FROM s LEFT JOIN t AS y ON s.id=y.id WHERE x.id=s.id RETURNING *, s.label AS source_label, y.v";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var source = delete ? plan.ShouldBeOfType<SqlDeletePlan>().Source : plan.ShouldBeOfType<SqlUpdatePlan>().Source;
        var returning = delete ? ((SqlDeletePlan)plan).Returning : ((SqlUpdatePlan)plan).Returning;

        // Assert
        source.ShouldNotBeNull();
        source.TargetBinding.ShouldBe(0);
        var join = source.Associations;
        join.Bindings.Count.ShouldBe(3);
        join.Bindings[0].Offset.ShouldBe(0);
        join.Bindings[0].Reference.Alias.ShouldBe("x");
        join.Bindings[2].Reference.Alias.ShouldBe("y");
        join.Bindings[0].Source.Table!.ObjectId.ShouldBe(join.Bindings[2].Source.Table!.ObjectId);
        join.Bindings.Select(binding => binding.Offset).ShouldBe([0, 2, 4]);
        join.Projections.Count.ShouldBe(6);
        join.Where.ShouldNotBeNull();
        returning.ShouldNotBeNull();
        returning.Select(column => column.Name).ShouldBe(["id", "v", "source_label", "v"]);
        returning[2].ColumnOrdinal.ShouldBe(3);
        returning[2].Type.ShouldBe(DatabaseType.String);
        returning[2].MaxLength.ShouldBe(23);
        returning[3].ColumnOrdinal.ShouldBe(5);
        returning[3].IsNullable.ShouldBeTrue();
        if (!delete)
        {
            var assignment = ((SqlUpdatePlan)plan).Assignments.ShouldHaveSingleItem();
            assignment.Ordinal.ShouldBe(1);
            assignment.Value.ShouldBeOfType<SqlBoundColumn>().Ordinal.ShouldBe(5);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: invalid namespaces fail before execution, even over empty inputs (#1714)")]
    [InlineData("UPDATE t SET v = id FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = missing FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = s.missing FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = s.v FROM s WHERE id=1", "CS01015")]
    [InlineData("UPDATE t AS x SET v=s.v FROM s WHERE t.id=s.id", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s AS q", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s RETURNING id", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s RETURNING missing.id", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM t", "CS01019")]
    [InlineData("UPDATE t AS x SET v=1 FROM s AS x", "CS01019")]
    [InlineData("UPDATE t SET v=1 FROM s JOIN s AS q ON t.id=q.id", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM s, s AS q JOIN s AS z ON s.id=z.id", "CS01015")]
    [InlineData("UPDATE t SET v=q.v FROM (SELECT t.v) AS q", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM absent", "CS01014")]
    [InlineData("DELETE FROM t USING s WHERE id=1", "CS01015")]
    [InlineData("DELETE FROM t USING s RETURNING id", "CS01015")]
    [InlineData("DELETE FROM t AS x USING s WHERE t.id=s.id", "CS01015")]
    [InlineData("DELETE FROM t USING s RETURNING s.missing", "CS01015")]
    public async Task Plan_InvalidSources_ShouldReportBindingCode(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-names", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, v INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: bound forms explain sources and refuse execution without writes (#1714)")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING s.v")]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id RETURNING s.v")]
    [InlineData("UPDATE t SET v=q.v FROM (SELECT id,v FROM s) q WHERE t.id=q.id")]
    [InlineData("WITH q AS (SELECT id,v FROM s) UPDATE t SET v=q.v FROM q WHERE t.id=q.id")]
    [InlineData("WITH q AS (SELECT id,v FROM s) DELETE FROM t USING q WHERE t.id=q.id")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE EXISTS (SELECT 1 WHERE s.id=t.id)")]
    [InlineData("DELETE FROM t USING s JOIN t AS q ON EXISTS (SELECT 1 WHERE s.id=q.id) WHERE t.id=s.id")]
    [InlineData("UPDATE t SET v=1 FROM INFORMATION_SCHEMA.TABLES AS s WHERE s.TABLE_NAME='t'")]
    public async Task ExecuteAsync_BoundSourceForms_ShouldRefuseAndExplain(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES (1,20),(1,30)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        await using var description = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var targets = new List<string?>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None)) { targets.Add(row.GetString(3)); }

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        failure.Message.ShouldContain("execution is not implemented yet", Case.Sensitive);
        targets.ShouldContain("dbo.t");
        targets.ShouldContain(sql.Contains("INFORMATION_SCHEMA") ? "INFORMATION_SCHEMA.TABLES" : "dbo.s");
        await using var result = (await session.ExecuteAsync("SELECT v FROM t", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<int>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetInt32(0)); }
        values.ShouldBe([10]);
    }
}

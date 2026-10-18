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
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCteExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: chained joins, subqueries and grouping execute")]
    [InlineData("WITH a AS (SELECT id FROM t WHERE id > 1 ORDER BY id LIMIT 2), b (n) AS (SELECT id FROM a) SELECT b.n FROM b JOIN t ON b.n = t.id ORDER BY b.n DESC LIMIT 1", 3L)]
    [InlineData("WITH a AS (SELECT id FROM t), b AS (SELECT id FROM a) SELECT x.id FROM a x JOIN b y ON x.id = y.id ORDER BY x.id LIMIT 1", 1L)]
    [InlineData("WITH a AS (SELECT id FROM t) SELECT id FROM t WHERE id IN (SELECT id FROM a) AND EXISTS (SELECT id FROM a) ORDER BY id LIMIT 1", 1L)]
    [InlineData("WITH a AS (SELECT MAX(id) AS n FROM t) SELECT (SELECT n FROM a) FROM t LIMIT 1", 3L)]
    [InlineData("WITH a AS (SELECT id, COUNT(*) AS n FROM t GROUP BY id HAVING COUNT(*) > 0 ORDER BY id DESC LIMIT 2) SELECT SUM(id) FROM a", 5L)]
    [InlineData("WITH a AS (SELECT id FROM t WHERE id IN (SELECT id FROM t)) SELECT MAX(id) FROM a", 3L)]
    [InlineData("WITH a AS (SELECT COUNT(*) AS n) SELECT n FROM a", 1L)]
    [InlineData("WITH a AS (SELECT id FROM t ORDER BY id LIMIT 1) SELECT (SELECT id FROM a) FROM t LIMIT 1", 1L)]
    [InlineData("WITH recent AS (SELECT id FROM t), top AS (SELECT id FROM recent ORDER BY id LIMIT 1) SELECT top.id FROM top", 1L)]
    [InlineData("WITH ties (key_id) AS (SELECT id FROM t ORDER BY id LIMIT 1) SELECT key_id FROM ties", 1L)]
    public async Task Execute_ChainedCommonTables_ShouldReturnExpectedValue(string sql, long expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-read", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        Convert.ToInt64(rows.ShouldHaveSingleItem()[0]).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: repeated references materialize once across query scopes")]
    public async Task Execute_RepeatedCommonTable_ShouldReadStoredRowsOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-once", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "WITH c AS (SELECT * FROM t) SELECT x.id FROM c x JOIN c y ON x.id = y.id WHERE x.id IN (SELECT id FROM c) ORDER BY x.id;");

        // Assert
        rows.Select(row => row[0]).ShouldBe([1, 2, 3]);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: ordinary derived output ordering retains its capability boundary")]
    [InlineData("WITH a AS (SELECT id FROM t) SELECT d.id FROM a JOIN (SELECT id FROM t) d ON a.id = d.id ORDER BY d.id;")]
    [InlineData("WITH a AS (SELECT d.id FROM (SELECT id FROM t) d ORDER BY d.id) SELECT * FROM a;")]
    public async Task Execute_OrderedDerivedInput_ShouldKeepDerivedOrderingPin(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-derived-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Error);
        result.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == "COHDBL001");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: referenced bodies retain index seeks and evaluated row windows")]
    public async Task Execute_ReferencedSeekAndLimit_ShouldKeepNormalPlanning()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-seek", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "WITH c AS (SELECT id FROM t WHERE id = 2 LIMIT 1) SELECT id FROM c;");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([2]);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: read shadowing, schema bypass and self-reference follow declaration scope")]
    public async Task Execute_ShadowedRelation_ShouldKeepCatalogTargetsAndSelfReferences()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-scope", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act / Assert
        (await RowsAsync(session, "WITH t AS (SELECT id + 10 AS id FROM t) SELECT MIN(id) FROM t;")).ShouldHaveSingleItem()[0].ShouldBe(11L);
        (await RowsAsync(session, "WITH t AS (SELECT 42 AS id) SELECT MIN(id) FROM dbo.t;")).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await session.ExecuteAsync("WITH t AS (SELECT id FROM dbo.t LIMIT 1 / 0) UPDATE t SET id = id;", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(3);
        (await session.ExecuteAsync("WITH t AS (SELECT 42 AS id) DELETE FROM t;", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(3);
        (await RowsAsync(session, "SELECT id FROM t;")).ShouldBeEmpty();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "WITH only_cte AS (SELECT 1 AS id) DELETE FROM only_cte;", cancellationToken: CancellationToken.None));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: DML reads the pre-write statement image")]
    [InlineData("WITH s AS (SELECT id + 100 AS id FROM t) INSERT INTO t (id) SELECT id FROM s;", 6)]
    [InlineData("INSERT INTO t (id) WITH s AS (SELECT id + 100 AS id FROM t) SELECT id FROM s;", 6)]
    [InlineData("WITH a AS (SELECT id + 100 AS id FROM t) INSERT INTO t WITH s AS (SELECT id FROM a) SELECT id FROM s;", 6)]
    [InlineData("WITH s AS (SELECT id FROM t) UPDATE t SET id = id + 100 WHERE id IN (SELECT id FROM s);", 3)]
    [InlineData("WITH s AS (SELECT id FROM t) DELETE FROM t WHERE id IN (SELECT id FROM s);", 0)]
    public async Task Execute_WithDml_ShouldMaterializeBeforeWriting(string sql, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(3);
        (await RowsAsync(session, "SELECT id FROM t;")).Count.ShouldBe(count);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: all definitions bind before any row is read")]
    [InlineData("WITH a AS (SELECT id FROM t), A AS (SELECT id FROM t) SELECT * FROM a", "COHSQLE026")]
    [InlineData("WITH a AS (SELECT id FROM b), b AS (SELECT id FROM t) SELECT * FROM a", "COHSQLE026")]
    [InlineData("WITH a (x, y) AS (SELECT id FROM t) SELECT * FROM a", "COHSQLE013")]
    [InlineData("WITH unused AS (SELECT missing FROM t) SELECT id FROM t", "COHSQLE015")]
    [InlineData("WITH unused AS (SELECT id FROM t LIMIT missing) SELECT id FROM t", "COHSQLE005")]
    [InlineData("WITH unused AS (SELECT id FROM t LIMIT TRUE) SELECT id FROM t", "COHSQLE018")]
    public async Task Execute_InvalidDefinition_ShouldFailBeforeReading(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var exception = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        exception.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: unused dependency chains, counts and seek bounds bind but never evaluate")]
    [InlineData("WITH a AS (SELECT 1 / (id - id) AS n FROM t), b AS (SELECT n FROM a) SELECT id FROM t;")]
    [InlineData("WITH a AS (SELECT id FROM t LIMIT 1 / 0) SELECT id FROM t;")]
    [InlineData("WITH a AS (SELECT id FROM t WHERE id = 1 / 0) SELECT id FROM t;")]
    [InlineData("WITH a AS (SELECT id FROM t WHERE id = 1 / 0 LIMIT 1 / 0), b AS (SELECT id FROM a) SELECT id FROM t;")]
    [InlineData("WITH t AS (SELECT id FROM t LIMIT 1 / 0) SELECT id FROM dbo.t;")]
    public async Task Execute_UnusedCommonTable_ShouldAvoidEvaluatingItsDependencies(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-unused", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.Count.ShouldBe(3);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: unused counts and index bounds never invoke application functions")]
    public async Task Execute_UnusedApplicationCall_ShouldBindWithoutInvoking()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("cte-unused-call");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (long value) => { calls++; return value; },
            volatility: SqlFunctionVolatility.Stable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "WITH a AS (SELECT id FROM t WHERE id = observe(2) LIMIT observe(1)), b AS (SELECT id FROM a) SELECT id FROM t;");

        // Assert
        rows.Count.ShouldBe(3);
        calls.ShouldBe(0);
        (await RowsAsync(session, "WITH a AS (SELECT id FROM t WHERE id = observe(2) LIMIT observe(1)) SELECT id FROM a;")).ShouldHaveSingleItem().ShouldBe([2]);
        calls.ShouldBeGreaterThan(0);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: failures preserve statement atomicity and explicit transactions")]
    public async Task Execute_FailingCteOrInsert_ShouldRollbackOnlyTheStatement()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-atomic", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("BEGIN;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (4);", cancellationToken: CancellationToken.None);

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "WITH s AS (SELECT 10 / (id - 2) AS n FROM t) UPDATE t SET id = 99 WHERE EXISTS (SELECT n FROM s);", cancellationToken: CancellationToken.None));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "WITH s AS (SELECT CASE WHEN id = 1 THEN 100 ELSE id END AS n FROM t) INSERT INTO t SELECT n FROM s ORDER BY CASE WHEN n = 100 THEN 0 ELSE 1 END, n;", cancellationToken: CancellationToken.None));

        // Assert
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id;")).Select(row => row[0]).ShouldBe([1, 2, 3, 4]);
        (await session.ExecuteAsync("UPDATE t SET id = 5 WHERE id = 4;", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        await session.ExecuteAsync("ROLLBACK;", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id;")).Select(row => row[0]).ShouldBe([1, 2, 3]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: catalog capture, output metadata and identity use the existing contracts")]
    public async Task Execute_MetadataAndIdentity_ShouldPreserveRelationContracts()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-metadata", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (name VARCHAR(12) COLLATE case_insensitive, cost DECIMAL(9, 2));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES ('Ada', 1.25), ('ADA', 2.25);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE identities (id INT GENERATED ALWAYS AS IDENTITY, name TEXT);", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await RowsAsync(session, "WITH a AS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES) SELECT TABLE_NAME FROM a ORDER BY TABLE_NAME;")).Select(row => row[0]).ShouldBe(["identities", "t"]);
        (await RowsAsync(session, "WITH a (label) AS (SELECT name FROM t) SELECT COUNT(*) FROM a WHERE label = 'ada';")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        await using var empty = (DatabaseResultSet)await session.ExecuteAsync("WITH a (label, price) AS (SELECT name, cost FROM t WHERE 1 = 0) SELECT * FROM a;", cancellationToken: CancellationToken.None);
        empty.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.Decimal]);
        empty.Columns[0].MaxLength.ShouldBe(12);
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("WITH a AS (SELECT cost FROM t) SELECT * FROM a;").Statement.SqlExpression).ShouldBeOfType<SqlWithPlan>();
        plan.Relations[0].Columns[0].Type.Precision.ShouldBe(9);
        plan.Relations[0].Columns[0].Type.Scale.ShouldBe(2);
        (await session.ExecuteAsync("WITH a AS (SELECT name FROM t) INSERT INTO identities (name) SELECT name FROM a;", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM identities ORDER BY id;")).Select(row => row[0]).ShouldBe([1, 2]);
        (await RowsAsync(session, "EXPLAIN WITH a AS (SELECT 1 AS id) SELECT * FROM a;")).ShouldContain(row => Equals(row[2], "Materialize"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: data-modifying bodies reject without deleting rows")]
    public async Task Execute_ModifyingBody_ShouldRejectAndPreserveRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-modifying", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d;", cancellationToken: CancellationToken.None));

        // Assert
        result.Message.ShouldContain("COHDBL001");
        (await RowsAsync(session, "SELECT id FROM t;")).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: every definition uses the statement's captured MVCC view")]
    public async Task Execute_CommitAfterSnapshot_ShouldKeepAllCommonTablesOnCapturedView()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(writer);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var captured = new SqlStatementContext(transaction, database.Coordinator);
            var query = SqlQueryRequest.FromSql("WITH a AS (SELECT id FROM t), b AS (SELECT id FROM t) SELECT MAX(a.id), MAX(b.id) FROM a CROSS JOIN b;");
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            await writer.ExecuteAsync("INSERT INTO t VALUES (4);", cancellationToken: CancellationToken.None);
            await using var oldResult = (DatabaseResultSet)await executor.ExecuteAsync(query, captured, CancellationToken.None);
            var oldRows = new List<object?>();
            await foreach (var row in oldResult.GetRowsAsync(CancellationToken.None)) { oldRows.Add(row.GetValue(0)); oldRows.Add(row.GetValue(1)); }
            var current = await RowsAsync(writer, query.Statement.SqlExpression.Text!);

            // Assert
            oldRows.ShouldBe([3, 3]);
            current.ShouldHaveSingleItem().ShouldBe([4, 4]);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2), (3);", cancellationToken: CancellationToken.None);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var execution = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        execution.Status.ShouldBe(DatabaseResultStatus.Success, string.Join("; ", (execution.Diagnostics ?? []).Select(diagnostic => diagnostic.Message)));
        await using var result = execution.ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}

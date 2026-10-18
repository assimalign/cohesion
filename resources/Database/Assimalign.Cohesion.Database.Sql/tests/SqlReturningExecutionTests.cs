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
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlReturningExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: all INSERT forms expose stored defaults, coercions and identities")]
    public async Task ExecuteAsync_InsertReturning_ShouldExposeStoredValuesInSourceOrder()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY, name CHAR(3) DEFAULT 'x', age INT DEFAULT (2 + 3))");

        // Act
        await using var values = await QueryAsync(session, "INSERT INTO t (name, age) VALUES ('a', '7'), (DEFAULT, DEFAULT) RETURNING *");
        await using var selected = await QueryAsync(session, "INSERT INTO t (name) SELECT name FROM t ORDER BY id DESC RETURNING id, name, age");
        await using var defaults = await QueryAsync(session, "INSERT INTO t DEFAULT VALUES RETURNING id");
        await using var identity = await QueryAsync(session, "INSERT INTO t (name) VALUES ('x') RETURNING id");

        // Assert
        values.Columns.Select(column => (column.Name, column.Type, column.MaxLength)).ShouldBe(
            [("id", DatabaseType.Int32, null), ("name", DatabaseType.Character, 3), ("age", DatabaseType.Int32, null)]);
        values.AffectedCount.ShouldBe(2);
        CheckRows(await ReadAsync(values), [[1, "a  ", 7], [2, "x  ", 5]]);
        selected.AffectedCount.ShouldBe(2);
        CheckRows(await ReadAsync(selected), [[3, "x  ", 5], [4, "a  ", 5]]);
        CheckRows(await ReadAsync(defaults), [[5]]);
        CheckRows(await ReadAsync(identity), [[6]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: UPDATE projects new rows and DELETE excludes cascades")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u) RETURNING *")]
    [InlineData("WITH c AS (SELECT id FROM u) DELETE FROM t WHERE id IN (SELECT id FROM c) RETURNING *")]
    public async Task ExecuteAsync_UpdateDeleteReturning_ShouldProjectOnlyDirectTargets(string delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, age INT DEFAULT 9)");
        await ExecuteAsync(session, "CREATE TABLE child (id INT, parent INT REFERENCES t(id) ON DELETE CASCADE)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 10), (2, 20)");
        await ExecuteAsync(session, "INSERT INTO child VALUES (100, 1), (200, 2)");
        await ExecuteAsync(session, "CREATE TABLE u (id INT)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (1), (2)");

        // Act
        await using var updated = await QueryAsync(session, "UPDATE t AS x SET age = age + 1 RETURNING x.id, age * 2, age AS years");
        await using var deleted = await QueryAsync(session, delete);

        // Assert: no UPDATE/DELETE scan order contract.
        updated.Columns.Select(column => column.Name).ShouldBe(["id", "column2", "years"]);
        updated.AffectedCount.ShouldBe(2);
        CheckRows((await ReadAsync(updated)).OrderBy(row => row[0]).ToList(), [[1, 22L, 11], [2, 42L, 21]]);
        deleted.AffectedCount.ShouldBe(2);
        CheckRows((await ReadAsync(deleted)).OrderBy(row => row[0]).ToList(), [[1, 11], [2, 21]]);
        await using var remaining = await QueryAsync(session, "SELECT * FROM child");
        (await ReadAsync(remaining)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: empty writes retain typed metadata and zero count")]
    [InlineData("INSERT INTO t SELECT * FROM t RETURNING *")]
    [InlineData("UPDATE t SET age = 1 RETURNING *")]
    [InlineData("DELETE FROM t RETURNING *")]
    public async Task ExecuteAsync_EmptyReturning_ShouldRetainMetadata(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, age INT)");

        // Act
        await using var result = await QueryAsync(session, sql);

        // Assert
        result.AffectedCount.ShouldBe(0);
        result.Columns.Select(column => (column.Name, column.Type)).ShouldBe([("id", DatabaseType.Int32), ("age", DatabaseType.Int32)]);
        (await ReadAsync(result)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: a later projection failure rolls back the whole statement")]
    [InlineData("INSERT INTO t VALUES (3, 1), (4, 0) RETURNING 10 / age", false)]
    [InlineData("INSERT INTO t VALUES (3, 1), (4, 0) RETURNING 10 / age", true)]
    [InlineData("UPDATE t SET age = age - 1 RETURNING 10 / age", false)]
    [InlineData("UPDATE t SET age = age - 1 RETURNING 10 / age", true)]
    [InlineData("DELETE FROM t RETURNING 10 / (age - 1)", false)]
    [InlineData("DELETE FROM t RETURNING 10 / (age - 1)", true)]
    public async Task ExecuteAsync_ReturningFailure_ShouldRollbackStatement(string sql, bool transaction)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, age INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 2), (2, 1)");
        if (transaction) { await ExecuteAsync(session, "BEGIN"); await ExecuteAsync(session, "INSERT INTO t VALUES (9, 2)"); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE001");
        await using var result = await QueryAsync(session, "SELECT id, age FROM t WHERE id < 9 ORDER BY id");
        CheckRows(await ReadAsync(result), [[1, 2], [2, 1]]);
        if (transaction)
        {
            await ExecuteAsync(session, "COMMIT");
            await using var previous = await QueryAsync(session, "SELECT id FROM t WHERE id = 9");
            CheckRows(await ReadAsync(previous), [[9]]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: constraint failures publish neither rows nor writes")]
    [InlineData("INSERT INTO t VALUES (2, 2, 1), (1, 3, 1) RETURNING returning_probe(id)", "UNIQUE", 1)]
    [InlineData("INSERT INTO t VALUES (2, 2, 1), (3, -1, 1) RETURNING *", "CHECK", 0)]
    [InlineData("INSERT INTO t VALUES (2, 2, 1), (3, NULL, 1) RETURNING *", "COHSQLE024", 0)]
    [InlineData("INSERT INTO t VALUES (2, 2, 1), (3, 3, 99) RETURNING *", "FOREIGN KEY", 0)]
    [InlineData("UPDATE t SET age = -1 RETURNING *", "CHECK", 0)]
    public async Task ExecuteAsync_ConstraintFailure_ShouldLeaveTableUnchanged(string sql, string failureKind, int expectedCalls)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("returning");
        builder.Functions.Add(SqlScalarFunction.Create("returning_probe", (int value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p (id INT PRIMARY KEY)");
        await ExecuteAsync(session, "INSERT INTO p VALUES (1)");
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, age INT NOT NULL CHECK (age > 0), parent INT REFERENCES p(id))");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 1, 1)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        if (failureKind == "COHSQLE024")
        {
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failureKind);
        }
        else
        {
            // UNIQUE, CHECK and FOREIGN KEY use the existing typed constraint contract,
            // which has no diagnostic code. A parser rejection cannot pass this assertion.
            failure.ShouldBeOfType<SqlConstraintViolationException>().ConstraintKind.ShouldBe(failureKind);
        }
        calls.ShouldBe(expectedCalls);
        await using var result = await QueryAsync(session, "SELECT * FROM t");
        CheckRows(await ReadAsync(result), [[1, 1, 1]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: results remain materialized after rollback")]
    public async Task ExecuteAsync_ReturningThenRollback_ShouldRetainResultOnly()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, value BYTEA)");
        await ExecuteAsync(session, "BEGIN");

        // Act
        await using var result = await QueryAsync(session, "INSERT INTO t VALUES (1, X'0102') RETURNING id, value, value");
        await ExecuteAsync(session, "ROLLBACK");

        // Assert
        var row = (await ReadAsync(result)).ShouldHaveSingleItem();
        row[0].ShouldBe(1);
        var first = row[1].ShouldBeOfType<byte[]>();
        first[0] = 9;
        row[2].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 1, 2 });
        await using var empty = await QueryAsync(session, "SELECT * FROM t");
        empty.AffectedCount.ShouldBe(-1);
        (await ReadAsync(empty)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: binding validates expressions even without targets")]
    [InlineData("UPDATE t SET age = 1 RETURNING COUNT(*)", "COHSQLE017")]
    [InlineData("DELETE FROM t RETURNING STRING_AGG(CAST(id AS TEXT), ',')", "COHSQLE017")]
    [InlineData("INSERT INTO t VALUES (1, 2) RETURNING wrong.id", "COHSQLE015")]
    [InlineData("INSERT INTO t SELECT * FROM t AS source RETURNING source.id", "COHSQLE015")]
    [InlineData("UPDATE t AS x SET age = 1 RETURNING t.id", "COHSQLE015")]
    [InlineData("DELETE FROM t RETURNING missing_function(id)", "COHSQLE016")]
    public async Task ExecuteAsync_InvalidReturningBinding_ShouldFailBeforeWrites(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, age INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: contextual names remain valid target qualifiers")]
    [InlineData("UPDATE old.t AS old SET age = 2 RETURNING old.id")]
    [InlineData("UPDATE old.t output SET age = 1 RETURNING output.id")]
    [InlineData("UPDATE old.t AS new SET age = 2 RETURNING new.id")]
    [InlineData("DELETE FROM old.t RETURNING old.t.id")]
    [InlineData("DELETE FROM old.t AS output RETURNING output.id")]
    public async Task ExecuteAsync_ContextualQualifier_ShouldBindTarget(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SCHEMA old");
        await ExecuteAsync(session, "CREATE TABLE old.t (id INT, age INT)");
        await ExecuteAsync(session, "INSERT INTO old.t VALUES (1, 1)");

        // Act
        await using var result = await QueryAsync(session, sql);

        // Assert
        CheckRows(await ReadAsync(result), [[1]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: excluded constructs cannot execute a consumed prefix")]
    [InlineData("INSERT INTO t OUTPUT inserted.* VALUES (2, 2)")]
    [InlineData("DELETE FROM t OUTPUT deleted.* WHERE id = 1")]
    [InlineData("DELETE FROM t output WHERE id = 1")]
    [InlineData("DELETE FROM t RETURNING id INTO v")]
    [InlineData("DELETE FROM t RETURNING OLD.age")]
    [InlineData("UPDATE t SET age = 0 RETURNING NEW.age")]
    [InlineData("DELETE FROM t RETURNING WITH (OLD AS o) *")]
    [InlineData("UPDATE t SET age = 0 RETURNING (SELECT MAX(id) FROM t)")]
    [InlineData("INSERT INTO t VALUES (2, 2) RETURNING (SELECT 1)")]
    [InlineData("INSERT INTO t SELECT * FROM t RETURNING (SELECT 1)")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING (SELECT 1)")]
    [InlineData("UPDATE t SET age = 0 RETURNING ROW_NUMBER() OVER ()")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("SELECT id FROM t RETURNING id")]
    public async Task ExecuteAsync_ExcludedReturning_ShouldLeaveRowsUnchanged(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, age INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 1)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == (sql.StartsWith("SELECT", StringComparison.Ordinal) ? "SQL0003" : "COHDBL001"));
        await using var result = await QueryAsync(session, "SELECT * FROM t");
        CheckRows(await ReadAsync(result), [[1, 1]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: volatile defaults are evaluated for each inserted row")]
    public async Task ExecuteAsync_PerRowDefault_ShouldReturnDistinctStoredValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, token UUID DEFAULT GEN_RANDOM_UUID())");

        // Act
        await using var result = await QueryAsync(session, "INSERT INTO t (id) VALUES (1), (2) RETURNING token");

        // Assert
        var rows = await ReadAsync(result);
        rows.Count.ShouldBe(2);
        rows[0][0].ShouldBeOfType<Guid>().ShouldNotBe(rows[1][0].ShouldBeOfType<Guid>());
        await using var stored = await QueryAsync(session, "SELECT token FROM t ORDER BY id");
        CheckRows(await ReadAsync(stored), rows.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: even immutable calls run per row after unique checks")]
    public async Task ExecuteAsync_FoldableReturning_ShouldEvaluateOnlyInsideApply()
    {
        // Arrange: the counter observes invocation timing, even for a foldable signature.
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("returning");
        builder.Functions.Add(SqlScalarFunction.Create("returning_probe", (long value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act / Assert: index validation precedes RETURNING, with no planning-time call.
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES (1) RETURNING returning_probe(7)"));
        calls.ShouldBe(0);

        // Act / Assert: successful rows each evaluate the expression once inside apply.
        await using var result = await QueryAsync(session, "INSERT INTO t VALUES (2), (3) RETURNING returning_probe(7)");
        calls.ShouldBe(2);
        CheckRows(await ReadAsync(result), [[7L], [7L]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: EXPLAIN includes expanded projection names without executing")]
    [InlineData("INSERT INTO t VALUES (3, 30) RETURNING *", "Insert", "values rows=1; returning=2; columns=id, age")]
    [InlineData("INSERT INTO t SELECT * FROM t RETURNING id AS written, age + 1", "InsertSelect", "returning=2; columns=written, column2")]
    [InlineData("UPDATE t SET age = 30 RETURNING id AS written, age + 1", "Update", "assignments=1; filter=False; returning=2; columns=written, column2")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING *", "Delete", "filter=True; returning=2; columns=id, age")]
    public async Task ExecuteAsync_ExplainReturning_ShouldDescribeProjectionWithoutWrites(string sql, string operation, string detail)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, age INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 10), (2, 20)");

        // Act
        await using var result = await QueryAsync(session, "EXPLAIN " + sql);
        var rows = await ReadAsync(result);

        // Assert
        var root = rows.Single(row => Equals(row[2], operation));
        root[5].ShouldBe(detail);
        await using var stored = await QueryAsync(session, "SELECT * FROM t ORDER BY id");
        CheckRows(await ReadAsync(stored), [[1, 10], [2, 20]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: self-cascades return each direct target once")]
    [InlineData("id = 1", 1)]
    [InlineData("id IN (1, 2)", 2)]
    public async Task ExecuteAsync_SelfCascadeReturning_ShouldReturnOnlyDirectTargets(string predicate, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, parent INT REFERENCES t(id) ON DELETE CASCADE)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, NULL), (2, 1)");

        // Act
        await using var result = await QueryAsync(session, "DELETE FROM t WHERE " + predicate + " RETURNING id");

        // Assert
        result.AffectedCount.ShouldBe(count);
        (await ReadAsync(result)).Select(row => row[0]).OrderBy(id => id).ShouldBe(
            Enumerable.Range(1, count).Cast<object?>());
        await using var stored = await QueryAsync(session, "SELECT * FROM t");
        (await ReadAsync(stored)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: parameters bind in every written-row projection")]
    [InlineData("INSERT INTO t VALUES (1) RETURNING @p, id + @p")]
    [InlineData("INSERT INTO t SELECT id FROM t RETURNING @p, id + @p")]
    [InlineData("UPDATE t SET id = 1 RETURNING @p, id + @p")]
    [InlineData("DELETE FROM t RETURNING @p, id + @p")]
    public async Task ExecuteAsync_ParameterizedReturning_ShouldBindParameters(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        if (!sql.StartsWith("INSERT INTO t VALUES", StringComparison.Ordinal)) { await ExecuteAsync(session, "INSERT INTO t VALUES (1)"); }

        // Act
        await using var result = (await session.ExecuteAsync(sql, new Dictionary<string, object?> { ["p"] = 7 }, CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.AffectedCount.ShouldBe(1);
        CheckRows(await ReadAsync(result), [[7, 8L]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RETURNING: wildcard metadata preserves target facets")]
    [InlineData("INSERT INTO t VALUES (1.25, 'abc', 'AbC') RETURNING *")]
    [InlineData("INSERT INTO t SELECT amount, label, folded FROM t RETURNING *")]
    [InlineData("UPDATE t SET amount = 1.25 RETURNING *")]
    [InlineData("DELETE FROM t RETURNING *")]
    public async Task ExecuteAsync_WildcardReturning_ShouldPreserveMetadata(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (amount DECIMAL(10,2) NOT NULL, label VARCHAR(12), folded VARCHAR(8) COLLATE case_insensitive NOT NULL)");
        if (!sql.StartsWith("INSERT INTO t VALUES", StringComparison.Ordinal)) { await ExecuteAsync(session, "INSERT INTO t VALUES (1.25, 'abc', 'AbC')"); }
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var (table, returning) = plan switch
        {
            SqlInsertPlan insert => (insert.Table, insert.Returning),
            SqlInsertSelectPlan insert => (insert.Table, insert.Returning),
            SqlUpdatePlan update => (update.Table, update.Returning),
            SqlDeletePlan delete => (delete.Table, delete.Returning),
            _ => throw new InvalidOperationException(),
        };

        // Act
        await using var result = await QueryAsync(session, sql);

        // Assert: precision/scale and collation belong to bound source metadata; the
        // public result contract carries type, length and nullability only.
        returning.ShouldNotBeNull().Select(column => column.ColumnOrdinal).ShouldBe(new int?[] { 0, 1, 2 });
        var amount = table.Columns[returning[0].ColumnOrdinal!.Value];
        amount.Type.Precision.ShouldBe(10);
        amount.Type.Scale.ShouldBe(2);
        returning[2].Collation.Resolve(null).ShouldBe(table.Columns[2].Collation);
        returning[2].Collation.Resolve(null).Name.ShouldBe("case_insensitive");
        result.Columns.Select(column => (column.Name, column.Type, column.MaxLength, column.IsNullable)).ShouldBe(
            [("amount", DatabaseType.Decimal, null, false), ("label", DatabaseType.String, 12, true), ("folded", DatabaseType.String, 8, false)]);
        CheckRows(await ReadAsync(result), [[1.25m, "abc", "AbC"]]);
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken = default)
        => session.ExecuteAsync(sql, cancellationToken: cancellationToken).AsTask();

    private static async Task<DatabaseResultSet> QueryAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken = default)
        => (await ExecuteAsync(session, sql, cancellationToken)).ShouldBeAssignableTo<DatabaseResultSet>();

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result, CancellationToken cancellationToken = default)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static void CheckRows(IReadOnlyList<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int index = 0; index < actual.Count; index++) { actual[index].ShouldBe(expected[index]); }
    }
}

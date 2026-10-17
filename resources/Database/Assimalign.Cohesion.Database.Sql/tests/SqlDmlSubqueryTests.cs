using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlSubqueryTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DML subqueries: predicates use pre-statement values and exact counts")]
    public async Task Execute_SubqueryPredicates_ShouldUsePreStatementSnapshot()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-subquery", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, x INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 1), (2, 2), (3, 9);", cancellationToken: CancellationToken.None);

        // Act
        var update = await session.ExecuteAsync("UPDATE t SET x = x + 10 WHERE id IN (SELECT id FROM t WHERE x < (SELECT AVG(x) FROM t));", cancellationToken: CancellationToken.None);
        var delete = await session.ExecuteAsync("DELETE FROM t WHERE x > (SELECT AVG(x) FROM t);", cancellationToken: CancellationToken.None);

        // Assert
        update.AffectedCount.ShouldBe(2);
        delete.AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT id, x FROM t;")).ShouldHaveSingleItem().ShouldBe([3, 9]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DML subqueries: cascades and uncommitted source rows roll back together")]
    public async Task Execute_TransactionalSubqueries_ShouldSeeOwnWritesAndRollbackCascades()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-subquery-tx", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE parent (id INT PRIMARY KEY);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE child (id INT PRIMARY KEY, parent_id INT REFERENCES parent(id) ON DELETE CASCADE);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE chosen (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO parent VALUES (1), (2);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO child VALUES (10, 1), (20, 2);", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO chosen VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        var update = await session.ExecuteAsync("UPDATE child SET id = id + 1 WHERE EXISTS (SELECT id FROM chosen);", cancellationToken: CancellationToken.None);
        var delete = await session.ExecuteAsync("DELETE FROM parent WHERE id IN (SELECT id FROM (SELECT id FROM chosen) d);", cancellationToken: CancellationToken.None);
        var during = await RowsAsync(session, "SELECT id FROM child;");
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        update.AffectedCount.ShouldBe(2);
        delete.AffectedCount.ShouldBe(1);
        during.ShouldHaveSingleItem().ShouldBe([21]);
        (await RowsAsync(session, "SELECT id FROM parent ORDER BY id;")).Select(row => row[0]).ShouldBe([1, 2]);
        (await RowsAsync(session, "SELECT id FROM child ORDER BY id;")).Select(row => row[0]).ShouldBe([10, 20]);
        (await RowsAsync(session, "SELECT id FROM chosen;")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DML subqueries: auto-commit predicates capture system relations")]
    public async Task Execute_CatalogSubqueries_ShouldCaptureUpdateAndDeleteSources()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-subquery-catalog", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE requested (name TEXT, found INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO requested VALUES ('requested', 0), ('absent', 0);", cancellationToken: CancellationToken.None);

        // Act
        var update = await session.ExecuteAsync("UPDATE requested SET found = 1 WHERE name IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES);", cancellationToken: CancellationToken.None);
        var delete = await session.ExecuteAsync("DELETE FROM requested WHERE name IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES);", cancellationToken: CancellationToken.None);

        // Assert
        update.AffectedCount.ShouldBe(1);
        delete.AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT name, found FROM requested;")).ShouldHaveSingleItem().ShouldBe(["absent", 0]);
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
}

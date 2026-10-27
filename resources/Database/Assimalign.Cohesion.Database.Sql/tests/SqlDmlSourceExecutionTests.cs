using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlSourceExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - UPDATE FROM: associations preserve physical identities and original self-source values (#1714)")]
    public async Task ExecuteAsync_SelfSource_ShouldCollectBeforeChangingKeys()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-self", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10),(2,20),(3,30)", cancellationToken: CancellationToken.None);

        // Act: each source is itself another target, and every target key moves.
        var rows = await RowsAsync(session, "UPDATE t SET id=t.id+10,v=s.v FROM t AS s WHERE s.id=4-t.id RETURNING t.id,t.v,s.id,s.v");

        // Assert
        rows.OrderBy(row => row[0]).ToArray().ShouldBe(new object?[][] { [11,30,3,30], [12,20,2,20], [13,10,1,10] });
        (await RowsAsync(session, "SELECT id,v FROM t ORDER BY id")).ShouldBe(new object?[][] { [11,30], [12,20], [13,10] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: equal unkeyed targets remain distinct and DELETE deduplicates source matches (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EqualTargetValues_ShouldUsePhysicalIdentity(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-equal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,0),(1,0)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync(delete ? "INSERT INTO s VALUES (1,7),(1,7)" : "INSERT INTO s VALUES (1,7)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, delete ? "DELETE FROM t USING s WHERE t.id=s.id RETURNING t.v,s.v"
            : "UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING t.v,s.v");

        // Assert
        rows.ShouldBe(new object?[][] { [delete ? 0 : 7,7], [delete ? 0 : 7,7] });
        (await RowsAsync(session, "SELECT v FROM t")).Length.ShouldBe(delete ? 0 : 2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - UPDATE FROM: a later duplicate match refuses before SET and keeps prior transaction work (#1714)")]
    public async Task ExecuteAsync_MultipleMatches_ShouldFailBeforeAnyAssignment()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-cardinality");
        builder.Functions.Add(SqlScalarFunction.Create("assignment_probe", (long value) => { calls++; return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,0),(2,0)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES (1,10),(2,20),(2,20)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (3,5)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPDATE t SET v=assignment_probe(s.v) FROM s WHERE t.id=s.id RETURNING t.v,s.v", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05068");
        calls.ShouldBe(0);
        (await RowsAsync(session, "SELECT v FROM t ORDER BY id")).ShouldBe(new object?[][] { [0], [0], [5] });
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: CTE, derived, comma, USING, LEFT and correlated sources return visible columns (#1714)")]
    public async Task ExecuteAsync_SourceForms_ShouldPreserveVisibleStarAndNullExtension()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-forms", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t (id INT,v INT)", "CREATE TABLE s (id INT,label VARCHAR(23))", "CREATE TABLE u (id INT,amount INT)",
            "INSERT INTO t VALUES (1,0),(2,0)", "INSERT INTO s VALUES (1,'a'),(2,'b')", "INSERT INTO u VALUES (1,8)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act
        var rows = await RowsAsync(session, "WITH q AS (SELECT id,label FROM s) UPDATE t SET v=COALESCE(u.amount,9) FROM q LEFT JOIN u USING(id), (SELECT 1 AS n) AS z WHERE EXISTS(SELECT 1 WHERE t.id=q.id) RETURNING *");

        // Assert
        rows.OrderBy(row => row[0]).ToArray().ShouldBe(new object?[][] { [1,8,1,"a",8,1], [2,9,2,"b",null,1] });
        (await RowsAsync(session, "DELETE FROM t USING s LEFT JOIN u USING(id) WHERE t.id=s.id RETURNING t.v,s.label,u.amount"))
            .OrderBy(row => row[0]).ToArray().ShouldBe(new object?[][] { [8,"a",8], [9,"b",null] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - UPDATE FROM: existing constraint helpers preserve atomic failure (#1714)")]
    [InlineData("v=s.v", "(1,11,1),(2,11,1)", "CS03037")]
    [InlineData("v=s.v", "(1,11,1),(2,-1,1)", "CS03038")]
    [InlineData("v=s.v", "(1,11,1),(2,NULL,1)", "CS03024")]
    [InlineData("pid=s.pid", "(1,11,1),(2,12,99)", "CS03039")]
    public async Task ExecuteAsync_SourceConstraintFailure_ShouldPreserveRows(string assignments, string sourceRows, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-constraints", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE p (id INT PRIMARY KEY)", "INSERT INTO p VALUES(1)",
            "CREATE TABLE t (id INT PRIMARY KEY,v INT NOT NULL UNIQUE CHECK(v>0),pid INT REFERENCES p(id))",
            "CREATE TABLE s (id INT,v INT,pid INT)", "INSERT INTO t VALUES(1,10,1),(2,20,1)", "INSERT INTO s VALUES " + sourceRows })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPDATE t SET " + assignments + " FROM s WHERE t.id=s.id", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await RowsAsync(session, "SELECT v,pid FROM t ORDER BY id")).ShouldBe(new object?[][] { [10,1], [20,1] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: RETURNING failure rolls back all target and cascade effects (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ReturningFailure_ShouldUndoStatement(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-returning-failure", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t (id INT PRIMARY KEY,v INT)", "CREATE TABLE s (id INT,v INT)",
            "CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE CASCADE)", "INSERT INTO t VALUES(1,10),(2,20)",
            "INSERT INTO s VALUES(1,11),(2,21)", "INSERT INTO c VALUES(1),(2)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(delete
            ? "DELETE FROM t USING s WHERE t.id=s.id RETURNING 1/(s.id-2)"
            : "UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING 1/(s.id-2)", cancellationToken: CancellationToken.None));

        // Assert
        (await RowsAsync(session, "SELECT v FROM t ORDER BY id")).ShouldBe(new object?[][] { [10], [20] });
        (await RowsAsync(session, "SELECT pid FROM c ORDER BY pid")).ShouldBe(new object?[][] { [1], [2] });
        (await session.ExecuteAsync("DELETE FROM t USING s WHERE t.id=s.id", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT pid FROM c")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: source cancellation disposes reads and permits the next writer (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SourceCancellation_ShouldLeaveRowsAndReleaseReads(bool delete)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-source-cancel");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_source", (long value) =>
        { calls++; canceled.Cancel(); return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in new[] { "CREATE TABLE t (id INT,v INT)", "CREATE TABLE s (id INT)",
            "INSERT INTO t VALUES(1,10)", "INSERT INTO s VALUES(1),(2)" })
        { await session.ExecuteAsync(sql, cancellationToken: timeout.Token); }

        // Act
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        string sqlText = delete
            ? "DELETE FROM t USING s WHERE cancel_source(s.id)=t.id" : "UPDATE t SET v=s.id FROM s WHERE cancel_source(s.id)=t.id";
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sqlText, cancellationToken: canceled.Token));

        // Assert
        calls.ShouldBe(1);
        (await RowsAsync(session, "SELECT v FROM t")).ShouldBe(new object?[][] { [10] });
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        transaction.State.ShouldBe(TransactionState.Active);
        database.Catalog.TryGetTable("dbo", "s", out var source).ShouldBeTrue();
        var probe = new TransactionSequence(ulong.MaxValue - 1714);
        try
        {
            database.Coordinator.LockManager.TryAcquire(probe,
                LockResource.Object(source.ObjectId), LockMode.Exclusive).ShouldBeTrue();
        }
        finally { database.Coordinator.LockManager.ReleaseAll(probe); }
        (await writer.ExecuteAsync("UPDATE s SET id=id+10", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(2);
        await writer.ExecuteAsync("DROP TABLE s", cancellationToken: timeout.Token);
        await transaction.RollbackAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - UPDATE FROM: retained binary source cells detach for SET and every RETURNING delivery (#1714)")]
    public async Task ExecuteAsync_SharedBinarySource_ShouldReturnIndependentCells()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-binary", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t (id INT,b BYTEA)", "CREATE TABLE s (b BYTEA)",
            "INSERT INTO t VALUES(1,NULL),(2,NULL)", "INSERT INTO s VALUES(CAST('\\x0102' AS BYTEA))" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act
        var rows = await RowsAsync(session, "UPDATE t SET b=s.b FROM s RETURNING t.b,s.b");
        ((byte[])rows[0][0]!)[0] = 99;

        // Assert
        ((byte[])rows[0][1]!).ShouldBe(new byte[] { 1, 2 });
        ((byte[])rows[1][0]!).ShouldBe(new byte[] { 1, 2 });
        ((byte[])rows[1][1]!).ShouldBe(new byte[] { 1, 2 });
        ((byte[])(await RowsAsync(session, "SELECT b FROM s"))[0][0]!).ShouldBe(new byte[] { 1, 2 });
    }

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        if (result.AffectedCount >= 0) { result.AffectedCount.ShouldBe(rows.Count); }
        return rows.ToArray();
    }
}

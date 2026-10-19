using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlSeekTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: scan and seek preserve comparison and RETURNING semantics (#1274)")]
    [InlineData("x.id = @key", true)]
    [InlineData("x.id BETWEEN 2 AND 4", true)]
    [InlineData("x.name = 'ALICE'", true)]
    [InlineData("x.name >= 'alice' AND x.name < 'charlie'", true)]
    [InlineData("x.name COLLATE binary = 'alice'", false)]
    [InlineData("x.id > CAST('1.5' AS DECIMAL)", false)]
    [InlineData("x.id = NULL", false)]
    [InlineData("x.name IS NULL", false)]
    [InlineData("x.id = 2 AND x.v = 999", true)]
    [InlineData("x.id = 2 OR x.id = 4", false)]
    public async Task ExecuteAsync_IndexedPredicates_ShouldMatchScanAndReturning(string predicate, bool seek)
    {
        // Arrange: the same table definition and input, with and without secondary indexes.
        await using var engine = SqlDatabaseEngine.Create("dml-semantics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["key"] = 2L };
        foreach (bool delete in new[] { false, true })
        {
            List<object?[]>? expected = null;
            List<object?[]>? remaining = null;
            foreach (bool indexed in new[] { false, true })
            {
                await session.ExecuteAsync("CREATE TABLE t (id INT, name TEXT COLLATE case_accent_insensitive, v INT)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t VALUES (1,'Álice',10),(2,'alice',20),(3,'Bob',30),(4,NULL,40),(5,'charlie',50)", cancellationToken: CancellationToken.None);
                if (indexed)
                {
                    await session.ExecuteAsync("CREATE INDEX ix_id ON t(id)", cancellationToken: CancellationToken.None);
                    await session.ExecuteAsync("CREATE INDEX ix_name ON t(name)", cancellationToken: CancellationToken.None);
                }

                // Act: aliases and schema-qualified references share the DML binder's scope.
                string sql = delete ? $"DELETE FROM dbo.t AS x WHERE {predicate} RETURNING id,name,v"
                    : $"UPDATE dbo.t AS x SET v = v + 1 WHERE {predicate} RETURNING id,name,v";
                var result = await session.ExecuteAsync(sql, parameters, CancellationToken.None);
                var metrics = session.LastStatementMetrics.ShouldNotBeNull();
                var actual = await RowsAsync(result);
                var after = await RowsAsync(await session.ExecuteAsync("SELECT id,name,v FROM t ORDER BY id", cancellationToken: CancellationToken.None));

                // Assert
                string expectedPath = indexed && seek
                    ? predicate.Contains("name", StringComparison.Ordinal) ? "seek:ix_name" : "seek:ix_id"
                    : "scan";
                metrics.AccessPath.ShouldBe(expectedPath);
                if (expected is null) { expected = actual; remaining = after; }
                else
                {
                    actual.Count.ShouldBe(expected.Count);
                    for (int i = 0; i < actual.Count; i++) { actual[i].ShouldBe(expected[i]); }
                    after.Count.ShouldBe(remaining!.Count);
                    for (int i = 0; i < after.Count; i++) { after[i].ShouldBe(remaining[i]); }
                }
                result.AffectedCount.ShouldBe(actual.Count);
                await session.ExecuteAsync("DROP TABLE t", cancellationToken: CancellationToken.None);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: indexed candidates retain independent and correlated WHERE subqueries (#1274)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WhereSubqueries_ShouldFilterCandidatesBeforeMutation(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-subqueries", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10),(2,20),(3,30),(4,40)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (5,50)", cancellationToken: CancellationToken.None);

        // Act: the self-subquery must keep seeing all original rows, including session writes.
        string filter = "x.id BETWEEN 2 AND 5 AND x.id IN (SELECT id FROM t WHERE v >= 30) " +
            "AND EXISTS (SELECT y.id FROM t y WHERE y.id = x.id - 1)";
        string sql = delete ? $"DELETE FROM t AS x WHERE {filter} RETURNING id,v" : $"UPDATE t AS x SET v = v + 100 WHERE {filter} RETURNING id,v";
        var rows = await RowsAsync(await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        rows.Select(row => (int)row[0]!).ShouldBe([3, 4, 5]);
        rows.Select(row => (int)row[1]!).ShouldBe(delete ? [30, 40, 50] : [130, 140, 150]);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(await session.ExecuteAsync("SELECT id FROM t", cancellationToken: CancellationToken.None))).Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DML seek: moving keys within the sought index visits and updates each original row once (#1274)")]
    public async Task ExecuteAsync_MovingSoughtKeys_ShouldMaterializeTargetsOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-halloween", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,0),(2,0),(3,0),(100,0)", cancellationToken: CancellationToken.None);

        // Act: replacement keys are still in the seek range, and overlap old unique mappings.
        var result = await session.ExecuteAsync("UPDATE t SET id = id + 1, v = v + 1 WHERE id BETWEEN 1 AND 10 RETURNING id,v", cancellationToken: CancellationToken.None);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        var rows = await RowsAsync(result);

        // Assert: the row-visit counter also catches a seek widened into a full scan.
        result.AffectedCount.ShouldBe(3);
        metrics.AccessPath.ShouldStartWith("seek:");
        metrics.RecordsExamined.ShouldBe(3);
        rows.Select(row => (int)row[0]!).ShouldBe([2, 3, 4]);
        rows.ShouldAllBe(row => Equals(row[1], 1));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: a detached tree falls back to the residual-filtered scan (#1274)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_DetachedIndex_ShouldFallBackToScan(bool delete, bool replaced)
    {
        // Arrange: retain a bound seek after its index is dropped. Missing runtime trees
        // still registered in the catalog must fail maintenance, never bypass constraints.
        await using var engine = SqlDatabaseEngine.Create("dml-detached", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10),(2,20),(3,30)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON t(id)", cancellationToken: CancellationToken.None);
        string sql = delete ? "DELETE FROM t WHERE id = 2 RETURNING id,v" : "UPDATE t SET v = 99 WHERE id = 2 RETURNING id,v";
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var access = delete ? ((SqlDeletePlan)plan).Access : ((SqlUpdatePlan)plan).Access;
        var seek = access.ShouldBeOfType<SqlIndexSeekPath>();
        seek.Index.Name.ShouldBe("ix");
        await session.ExecuteAsync("DROP INDEX ix ON t", cancellationToken: CancellationToken.None);
        if (replaced) { await session.ExecuteAsync("CREATE INDEX ix ON t(v)", cancellationToken: CancellationToken.None); }
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var statement = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            var result = await executor.ExecuteAsync(plan, statement, CancellationToken.None);
            var rows = await RowsAsync(result);

            // Assert
            result.AffectedCount.ShouldBe(1);
            rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 2, delete ? 20 : 99 });
            statement.Metrics.AccessPath.ShouldBe("scan");
            statement.Metrics.RecordsExamined.ShouldBe(3);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: empty targets and failures release cursor and autocommit locks (#1274)")]
    [InlineData("UPDATE t SET v = 1 WHERE id = 1 AND v = 999", false)]
    [InlineData("DELETE FROM t WHERE id = 1 AND v = 999", false)]
    [InlineData("UPDATE t SET v = 1 WHERE id = 1 AND 1 / (id - 1) > 0", true)]
    [InlineData("DELETE FROM t WHERE id = 1 AND 1 / (id - 1) > 0", true)]
    [InlineData("UPDATE t SET v = 1 / (id - 1) WHERE id = 1", true)]
    [InlineData("UPDATE t SET id = 2 WHERE id = 1", true)]
    [InlineData("UPDATE t SET v = 1 WHERE id = 1 RETURNING 1 / (id - 1)", true)]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING 1 / (id - 1)", true)]
    public async Task ExecuteAsync_EmptyOrFailedStatement_ShouldReleaseResources(string sql, bool fails)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var engine = SqlDatabaseEngine.Create("dml-seek-failure", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10),(2,20)", cancellationToken: timeout.Token);

        // Act
        if (fails) { await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: timeout.Token)); }
        else { (await session.ExecuteAsync(sql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0); }

        // Assert: another transaction can take the same row/key and mutate the same tree.
        await using var other = await database.CreateSessionAsync(timeout.Token);
        var result = await other.ExecuteAsync("UPDATE t SET id = 3 WHERE id = 1 RETURNING v", cancellationToken: timeout.Token);
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 10 });
        other.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: a missing registered maintenance tree still refuses writes (#1274)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MissingRegisteredTree_ShouldRefuseWrites(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-missing-tree", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON t(id)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10)", cancellationToken: CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await database.IndexManager.DropIndexAsync(transaction.Context, table.ObjectId, "ix", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            delete ? "DELETE FROM t WHERE id = 1" : "UPDATE t SET v = 20 WHERE id = 1", cancellationToken: CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("writes cannot bypass its constraints", Case.Sensitive);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(await session.ExecuteAsync("SELECT v FROM t", cancellationToken: CancellationToken.None)))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 10 });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: cancellation during candidate evaluation disposes the cursor before any write (#1274)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledResidual_ShouldReleaseCursorAndLeaveTargetsUnchanged(bool delete)
    {
        // Arrange: cancel from the first residual evaluation, with another candidate still pending.
        using var canceled = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-seek-cancel");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_probe", (long value) =>
        {
            calls++;
            canceled.Cancel();
            return true;
        }));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10),(2,20)", cancellationToken: timeout.Token);
        string filter = "id BETWEEN 1 AND 2 AND cancel_probe(id)";

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            delete ? $"DELETE FROM t WHERE {filter}" : $"UPDATE t SET v = 99 WHERE {filter}", cancellationToken: canceled.Token));

        // Assert: a fresh writer proves that no cursor latch or autocommit row grant leaked.
        calls.ShouldBe(1);
        await using var other = await database.CreateSessionAsync(timeout.Token);
        var result = await other.ExecuteAsync("UPDATE t SET id = id + 10 WHERE id BETWEEN 1 AND 2 RETURNING v", cancellationToken: timeout.Token);
        (await RowsAsync(result)).Select(row => (int)row[0]!).ShouldBe([10, 20]);
    }

    private static async Task<List<object?[]>> RowsAsync(DatabaseResult result)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return values.OrderBy(row => row[0]).ToList();
    }
}

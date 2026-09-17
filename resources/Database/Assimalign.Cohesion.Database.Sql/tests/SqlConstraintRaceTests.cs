using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlConstraintRaceTests
{
    [Fact]
    public async Task CreateUniqueIndex_DuplicateBackfill_ShouldReportTheSameConstraintViolation()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-unique-backfill" });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (7), (7)");
        var violation = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await session.ExecuteAsync("CREATE UNIQUE INDEX uq ON t(id)"));
        violation.ConstraintName.ShouldBe("uq");
        violation.Table.ShouldBe("dbo.t");
        violation.ConstraintKind.ShouldBe("UNIQUE");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.TryGetIndex(table.ObjectId, "uq", out _).ShouldBeFalse();
        (await Rows(session, "SELECT id FROM t")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UniqueViolation_ShouldIncludeSafeScalarOffendingValue()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-safe-value" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT CONSTRAINT uq UNIQUE)");
        await session.ExecuteAsync("INSERT INTO t VALUES (7)");
        var violation = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await session.ExecuteAsync("INSERT INTO t VALUES (7)"));
        violation.ConstraintName.ShouldBe("uq");
        violation.OffendingValue.ShouldBe(7);
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD CONSTRAINT positive CHECK(val > 0)", "INSERT INTO t VALUES (3, -1)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT positive CHECK(val > 0)", "UPDATE t SET val = -1 WHERE id = 2")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT uq UNIQUE(val)", "INSERT INTO t VALUES (3, 1)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT uq UNIQUE(val)", "UPDATE t SET val = 1 WHERE id = 2")]
    [InlineData("CREATE UNIQUE INDEX uq ON t(val)", "INSERT INTO t VALUES (3, 1)")]
    [InlineData("CREATE UNIQUE INDEX uq ON t(val)", "UPDATE t SET val = 1 WHERE id = 2")]
    public async Task DdlPublishingWhileDmlWaits_ShouldNeverBypassNewConstraint(string ddl, string write)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-ddl-race" });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT, val INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 1), (2, 2)");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();

        var coordinator = database.Coordinator;
        var ddlContext = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try
        {
            // Pause DDL before it publishes, with its real object lock held. The
            // following call runs synchronously until DML waits on that lock.
            await coordinator.LockManager.AcquireAsync(ddlContext.Sequence,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            var pendingWrite = session.ExecuteAsync(write, cancellationToken: TestTimeout.Token()).AsTask();
            pendingWrite.IsCompleted.ShouldBeFalse();

            // Execute actual DDL under the held context, avoiding timing sleeps
            // and production hooks. This exercises the same planner/apply/catalog
            // path as the session's auto-commit bracket.
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager);
            await executor.ExecuteAsync(SqlQueryRequest.FromSql(ddl),
                new SqlStatementContext(ddlContext, coordinator), TestTimeout.Token());
            await coordinator.CommitAsync(ddlContext, TestTimeout.Token());

            await Should.ThrowAsync<DatabaseException>(async () => await pendingWrite);
            // A changed definition may demand a retry; a fresh statement must
            // report the actual newly published constraint, never accept data.
            await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync(write));
            var rows = await Rows(session, "SELECT id, val FROM t ORDER BY id");
            rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
            rows.Select(row => row[1]).ShouldBe(new object?[] { 1, 2 });
        }
        finally
        {
            if (ddlContext.State == TransactionState.Active)
            {
                await coordinator.RollbackAsync(ddlContext);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CascadeAndRestrictPaths_ShouldUseTheCompleteDeleteSetRegardlessOfDeclarationOrder(bool cascadeFirst)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-cascade-order" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        const string cascade = "CONSTRAINT fk_cascade FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE";
        const string restrict = "CONSTRAINT fk_restrict FOREIGN KEY(pid) REFERENCES p(id) ON DELETE RESTRICT";
        string constraints = cascadeFirst ? $"{cascade}, {restrict}" : $"{restrict}, {cascade}";
        await session.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT, {constraints})");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("INSERT INTO c VALUES (2, 1)");
        await session.ExecuteAsync("DELETE FROM p");
        (await Rows(session, "SELECT id FROM p")).ShouldBeEmpty();
        (await Rows(session, "SELECT id FROM c")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("(1, NULL), (2, 1)")]
    [InlineData("(2, 1), (1, NULL)")]
    public async Task SelfReference_DeleteAll_ShouldNotDependOnPhysicalRowOrder(string values)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-self-order" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE nodes (id INT PRIMARY KEY, parent INT REFERENCES nodes(id) ON DELETE RESTRICT)");
        await session.ExecuteAsync($"INSERT INTO nodes VALUES {values}");
        await session.ExecuteAsync("DELETE FROM nodes");
        (await Rows(session, "SELECT id FROM nodes")).ShouldBeEmpty();
    }

    [Fact]
    public async Task CyclicCascade_ShouldDeleteEachRowOnceAndRollbackTogether()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-cycle" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE a (id INT PRIMARY KEY, bid INT)");
        await session.ExecuteAsync("CREATE TABLE b (id INT PRIMARY KEY, aid INT REFERENCES a(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("ALTER TABLE a ADD CONSTRAINT fk_b FOREIGN KEY(bid) REFERENCES b(id) ON DELETE CASCADE");
        await session.ExecuteAsync("INSERT INTO a VALUES (1, NULL)");
        await session.ExecuteAsync("INSERT INTO b VALUES (2, 1)");
        await session.ExecuteAsync("UPDATE a SET bid = 2");
        await session.ExecuteAsync("BEGIN");
        (await session.ExecuteAsync("DELETE FROM a")).AffectedCount.ShouldBe(1);
        (await Rows(session, "SELECT id FROM a")).ShouldBeEmpty();
        (await Rows(session, "SELECT id FROM b")).ShouldBeEmpty();
        await session.ExecuteAsync("ROLLBACK");
        (await Rows(session, "SELECT id FROM a")).Count.ShouldBe(1);
        (await Rows(session, "SELECT id FROM b")).Count.ShouldBe(1);
    }

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync())
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlConstraintTests
{
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

    [Fact]
    public async Task Constraints_InsertUpdateDelete_ShouldEnforceAndCascadeAtomically()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-basic" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE parent (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE child (id INT PRIMARY KEY, parent_id INT REFERENCES parent(id) ON DELETE CASCADE, qty INT CHECK (qty > 0), email TEXT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO parent VALUES (1), (2)");
        var orphan = await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO child VALUES (1, 7, 1, 'x')"));
        orphan.ConstraintKind.ShouldBe("FOREIGN KEY");
        orphan.Table.ShouldBe("dbo.child");
        orphan.OffendingValue.ShouldBe(7);
        await session.ExecuteAsync("INSERT INTO child VALUES (1, 1, 1, 'a'), (2, 1, 2, 'b'), (3, 2, 3, 'c')");
        var check = await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE child SET qty = 0 WHERE id = 1"));
        check.ConstraintKind.ShouldBe("CHECK");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE child SET parent_id = 9 WHERE id = 1"));
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE parent SET id = 9 WHERE id = 1"));
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE child SET email = 'a' WHERE id = 2"));
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("DELETE FROM parent WHERE id = 1");
        (await Rows(session, "SELECT id FROM child ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 3 });
        await session.ExecuteAsync("ROLLBACK");
        (await Rows(session, "SELECT id FROM child")).Count.ShouldBe(3);
        await session.ExecuteAsync("DELETE FROM parent WHERE id = 1");
        (await Rows(session, "SELECT id FROM child ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 3 });
    }

    [Fact]
    public async Task RestrictAndRecursiveCascade_ShouldNotPartiallyDelete()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-chain" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE g (id INT, cid INT, CONSTRAINT fk_g FOREIGN KEY(cid) REFERENCES c(id) ON DELETE RESTRICT)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("INSERT INTO c VALUES (2, 1)");
        await session.ExecuteAsync("INSERT INTO g VALUES (3, 2)");
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("DELETE FROM p"));
        failure.ConstraintName.ShouldBe("fk_g");
        (await Rows(session, "SELECT id FROM p")).Count.ShouldBe(1);
        (await Rows(session, "SELECT id FROM c")).Count.ShouldBe(1);
        await session.ExecuteAsync("ALTER TABLE g DROP CONSTRAINT fk_g");
        await session.ExecuteAsync("ALTER TABLE g ADD CONSTRAINT fk_g FOREIGN KEY(cid) REFERENCES c(id) ON DELETE CASCADE");
        await session.ExecuteAsync("DELETE FROM p");
        (await Rows(session, "SELECT id FROM g")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unique_ConcurrentExplicitTransactions_ExactlyOneWins()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-race" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, email TEXT, CONSTRAINT uq_email UNIQUE(email))");
        await first.ExecuteAsync("BEGIN");
        await second.ExecuteAsync("BEGIN");
        await first.ExecuteAsync("INSERT INTO t VALUES (1, 'same')");
        var contender = second.ExecuteAsync("INSERT INTO t VALUES (2, 'same')", cancellationToken: TestTimeout.Token()).AsTask();
        contender.IsCompleted.ShouldBeFalse();
        await first.ExecuteAsync("COMMIT");
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(async () => await contender);
        failure.ConstraintName.ShouldBe("uq_email");
        failure.OffendingValue.ShouldBeNull();
        await second.ExecuteAsync("ROLLBACK");
        (await Rows(first, "SELECT id FROM t")).Select(row => row[0]).ShouldBe(new object?[] { 1 });
    }

    [Fact]
    public async Task FailedMultirowUniqueStatement_ShouldPreservePriorStatementAndRollbackKeys()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-statement" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT, CONSTRAINT uq_id UNIQUE(id))");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES (2), (2)"));
        (await Rows(session, "SELECT id FROM t")).Select(row => row[0]).ShouldBe(new object?[] { 1 });
        await session.ExecuteAsync("INSERT INTO t VALUES (2)");
        await session.ExecuteAsync("ROLLBACK");
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2)");
        (await Rows(session, "SELECT id FROM t")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ForeignKey_ConcurrentChildCommit_ShouldBlockSnapshotParentDelete()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-skew" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var parent = await database.CreateSessionAsync();
        await using var child = await database.CreateSessionAsync();
        await parent.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await parent.ExecuteAsync("CREATE TABLE c (pid INT REFERENCES p(id))");
        await parent.ExecuteAsync("INSERT INTO p VALUES (1)");
        await parent.ExecuteAsync("BEGIN");
        await child.ExecuteAsync("BEGIN");
        await child.ExecuteAsync("INSERT INTO c VALUES (1)");
        var deletion = parent.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token()).AsTask();
        deletion.IsCompleted.ShouldBeFalse();
        await child.ExecuteAsync("COMMIT");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await deletion);
        await parent.ExecuteAsync("ROLLBACK");
        (await Rows(child, "SELECT id FROM p")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForeignKey_SnapshotMustSeeParentAndLatestDeletedParentCannotAuthorizeInsert()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-snapshot" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var parent = await database.CreateSessionAsync();
        await using var child = await database.CreateSessionAsync();
        await parent.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await parent.ExecuteAsync("CREATE TABLE c (pid INT REFERENCES p(id))");
        await child.ExecuteAsync("BEGIN");
        await parent.ExecuteAsync("INSERT INTO p VALUES (1)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await child.ExecuteAsync("INSERT INTO c VALUES (1)"));
        await child.ExecuteAsync("ROLLBACK");
        await child.ExecuteAsync("BEGIN");
        await parent.ExecuteAsync("DELETE FROM p");
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await child.ExecuteAsync("INSERT INTO c VALUES (1)"));
        (await Rows(parent, "SELECT pid FROM c")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ChecksAndNullableReferences_ShouldUseSqlNullSemantics()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-null" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (a INT, b INT, CONSTRAINT uq_pair UNIQUE(a,b))");
        await session.ExecuteAsync("CREATE TABLE c (a INT, b INT, qty INT CHECK (qty > 0), CONSTRAINT fk_pair FOREIGN KEY(a,b) REFERENCES p(a,b))");
        await session.ExecuteAsync("INSERT INTO c VALUES (NULL, 7, NULL), (7, NULL, 1)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO c VALUES (NULL, NULL, 0)"));
        await session.ExecuteAsync("INSERT INTO p VALUES (1, 2)");
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 2, 3)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO c VALUES (2, 1, 3)"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("qty")]
    [InlineData("qty AND TRUE")]
    [InlineData("qty > @minimum")]
    [InlineData("COUNT(qty) > 0")]
    public async Task CheckInvalidPredicates_ShouldFailBeforeTablePublication(string predicate)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-invalid" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"CREATE TABLE t (qty INT CHECK ({predicate}))"));
        ((SqlDatabaseInstance)database).Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AlterConstraints_ShouldValidateExistingRowsAndProtectDependencies()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-alter" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (pid INT, qty INT)");
        await session.ExecuteAsync("INSERT INTO c VALUES (1, -1)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT positive CHECK(qty > 0)"));
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT parent FOREIGN KEY(pid) REFERENCES p(id)"));
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("UPDATE c SET qty = 1");
        await session.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT positive CHECK(qty > 0)");
        await session.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT parent FOREIGN KEY(pid) REFERENCES p(id)");
        await session.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT uq_qty UNIQUE(qty)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("DROP TABLE p"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE c DROP COLUMN qty"));
        await session.ExecuteAsync("ALTER TABLE c DROP CONSTRAINT positive");
        await session.ExecuteAsync("ALTER TABLE c DROP CONSTRAINT uq_qty");
        await session.ExecuteAsync("UPDATE c SET qty = -1");
        await session.ExecuteAsync("ALTER TABLE c ADD COLUMN extra INT CHECK(extra > 0)");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE c SET extra = -1"));
    }

    [Fact]
    public async Task ForeignKeyLookups_ShouldSeekExistingIndexes()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-seek" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT, pid INT REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX child_parent ON c(pid)");
        await session.ExecuteAsync("INSERT INTO p VALUES " + string.Join(',', Enumerable.Range(1, 100).Select(id => $"({id})")));
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 50)");
        var metrics = ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();
        metrics.AccessPath.ShouldStartWith("constraint-seek:");
        metrics.RecordsExamined.ShouldBe(1);
        await session.ExecuteAsync("DELETE FROM p WHERE id = 50");
        metrics = ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();
        metrics.AccessPath.ShouldBe("constraint-seek:child_parent");
        metrics.RecordsExamined.ShouldBe(101);
        (await Rows(session, "SELECT id FROM c")).ShouldBeEmpty();
    }

    [Fact]
    public async Task CompositeForeignKey_ShouldSeekReorderedPartialChildIndex()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-prefix" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (a INT, b INT, CONSTRAINT uq_pair UNIQUE(a,b))");
        await session.ExecuteAsync("CREATE TABLE c (a INT, b INT, CONSTRAINT fk_pair FOREIGN KEY(a,b) REFERENCES p(a,b) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX child_b ON c(b)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1, 7), (2, 7), (3, 8)");
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 7), (2, 7), (3, 8)");
        await session.ExecuteAsync("DELETE FROM p WHERE a = 1");
        var metrics = ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();
        metrics.AccessPath.ShouldBe("constraint-seek:child_b");
        metrics.RecordsExamined.ShouldBe(5);
        (await Rows(session, "SELECT a FROM c ORDER BY a")).Select(row => row[0]).ShouldBe(new object?[] { 2, 3 });
    }

    [Fact]
    public async Task ConstraintsAndUniqueIndexes_ShouldSurviveRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-constraints", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-restart", RootPath = directory }))
            {
                var database = await engine.CreateDatabaseAsync("db");
                await using var session = await database.CreateSessionAsync();
                await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
                await session.ExecuteAsync("CREATE TABLE c (pid INT REFERENCES p(id) ON DELETE CASCADE, qty INT CHECK(qty > 0), email TEXT UNIQUE)");
                await session.ExecuteAsync("INSERT INTO p VALUES (1)");
                await session.ExecuteAsync("INSERT INTO c VALUES (1, 1, 'kept')");
            }
            await using var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "constraint-restart", RootPath = directory });
            var reopened = await reopenedEngine.OpenDatabaseAsync("db");
            await using var reopenedSession = await reopened.CreateSessionAsync();
            await Should.ThrowAsync<SqlConstraintViolationException>(async () => await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (9, 1, 'new')"));
            await Should.ThrowAsync<SqlConstraintViolationException>(async () => await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (1, -1, 'new')"));
            await Should.ThrowAsync<SqlConstraintViolationException>(async () => await reopenedSession.ExecuteAsync("INSERT INTO c VALUES (1, 1, 'kept')"));
            await reopenedSession.ExecuteAsync("DELETE FROM p");
            (await Rows(reopenedSession, "SELECT pid FROM c")).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}


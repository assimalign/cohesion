using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlReferentialActionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: snapshot conflicts name the action and preserve parent and child rows")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_NewerChildVersion_ShouldFailSnapshotCrosscheck(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("set-action-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var parent = await database.CreateSessionAsync(CancellationToken.None);
        await using var child = await database.CreateSessionAsync(CancellationToken.None);
        await parent.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT 2 REFERENCES p(id) ON DELETE {action})", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await using var transaction = await parent.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await child.ExecuteAsync("INSERT INTO c VALUES (10, 1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => parent.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None).AsTask());
        failure.Message.ShouldContain($"ON DELETE {action} reached", Case.Sensitive);
        failure.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        await transaction.RollbackAsync(CancellationToken.None);
        (await Rows(child, "SELECT id FROM p ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await Rows(child, "SELECT id, pid FROM c")).Single().ShouldBe(new object?[] { 10, 1 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET DEFAULT: identity follows ordinary UPDATE DEFAULT rules")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_IdentityDefault_ShouldFollowAssignmentRules(bool always)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("identity-set-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE c (id INT GENERATED {(always ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY PRIMARY KEY REFERENCES p(id) ON DELETE SET DEFAULT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c DEFAULT VALUES", cancellationToken: CancellationToken.None);

        // Act / Assert
        if (always)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe("CS03025");
            (await Rows(session, "SELECT id FROM c")).Single()[0].ShouldBe(1);
            (await Rows(session, "SELECT id FROM p WHERE id = 1")).Single()[0].ShouldBe(1);
        }
        else
        {
            await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None);
            (await Rows(session, "SELECT id FROM c")).Single()[0].ShouldBe(2);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Compiled schemas: new catalog delete actions cannot collapse to RESTRICT")]
    [InlineData(SqlCatalogReferentialAction.NoAction)]
    [InlineData(SqlCatalogReferentialAction.SetNull)]
    [InlineData(SqlCatalogReferentialAction.SetDefault)]
    public async Task ApplySchema_UnsupportedCatalogAction_ShouldRefuse(SqlCatalogReferentialAction action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("compiled-actions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
        [
            new CompiledSchemaTable("p", "P", [new CompiledSchemaColumn("id", DatabaseType.Int32, IsNullable: false)], new CompiledSchemaKey("pk_p", ["id"]), [], []),
            new CompiledSchemaTable("c", "C", [new CompiledSchemaColumn("pid", DatabaseType.Int32, IsNullable: true)], null, [],
                [new CompiledSchemaConstraint("fk_c", CompiledSchemaConstraintKind.Reference, ["pid"], "p", ["id"]) ]),
        ], []);
        await database.ApplySchemaAsync(schema, CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "c", out var table).ShouldBeTrue();
        var changed = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns, table.PrimaryKeyColumns,
            table.Owner, table.OwningSchema,
            [new SqlCatalogConstraint("fk_c", SqlCatalogConstraintKind.Reference, ["pid"], "dbo", "p", ["id"], onDelete: action)], table.DroppedColumnOrdinals);
        await database.Catalog.PublishTableAsync(changed, [], [], replaceExisting: true, cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(() => database.ApplySchemaAsync(schema, CancellationToken.None).AsTask());
        failure.Message.ShouldContain("RESTRICT and CASCADE", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SET NULL: composite references preserve snapshots and index versions")]
    public async Task Delete_SetNull_ShouldUpdateOnlyMatchingColumnsAndMaintainVersions()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("set-null", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE p (a INT, b INT, UNIQUE(a, b))", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, a INT, b INT, label TEXT, FOREIGN KEY(a, b) REFERENCES p(a, b) ON DELETE SET NULL)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE INDEX ix_a ON c(a)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO p VALUES (1, 2), (3, 4)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO c VALUES (1, 1, 2, 'keep'), (2, 3, 4, 'other')", cancellationToken: CancellationToken.None);
        await reader.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        (await Rows(reader, "SELECT a FROM c WHERE a = 1")).Single()[0].ShouldBe(1);

        // Act
        await writer.ExecuteAsync("DELETE FROM p WHERE a = 1", cancellationToken: CancellationToken.None);

        // Assert
        (await Rows(writer, "SELECT a, b, label FROM c WHERE id = 1")).Single().ShouldBe(new object?[] { null, null, "keep" });
        (await Rows(writer, "SELECT a FROM c WHERE id = 2")).Single()[0].ShouldBe(3);
        (await Rows(writer, "SELECT id FROM c WHERE a = 1")).ShouldBeEmpty();
        (await Rows(reader, "SELECT a, b FROM c WHERE id = 1")).Single().ShouldBe(new object?[] { 1, 2 });
        await reader.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        (await Rows(writer, "SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")).Single()[0].ShouldBe("SET NULL");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Delete actions: a row reached by CASCADE is deleted before SET actions")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    [InlineData("NO ACTION")]
    public async Task Delete_OverlappingCascade_ShouldDeleteOnce(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("overlap", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE c (pid INT NOT NULL, FOREIGN KEY(pid) REFERENCES p(id) ON DELETE {action}, FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT pid FROM c")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SET DEFAULT: literal and clock expressions use the delete transaction")]
    public async Task Delete_SetDefault_ShouldAssignEachColumnsDefault()
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("set-default", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT, stamp TIMESTAMPTZ, UNIQUE(id, stamp))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1, CURRENT_TIMESTAMP)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (id INT DEFAULT (1 + 1), stamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(id, stamp) REFERENCES p(id, stamp) ON DELETE SET DEFAULT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c SELECT id, stamp FROM p", cancellationToken: CancellationToken.None);
        clock.Instant += TimeSpan.FromDays(1);
        await session.ExecuteAsync("INSERT INTO p VALUES (2, CURRENT_TIMESTAMP)", cancellationToken: CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT id, stamp FROM c")).Single().ShouldBe(new object?[] { 2, clock.Instant });
        await SqlConstraintAssertions.ThrowAsync(() => session.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None).AsTask());
        (await Rows(session, "SELECT id FROM p")).Single()[0].ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Delete actions: child validation rolls back every parent and child mutation")]
    [InlineData("pid INT NOT NULL", "SET NULL", "")]
    [InlineData("pid INT CHECK(pid IS NOT NULL)", "SET NULL", "")]
    [InlineData("pid INT UNIQUE", "SET NULL", "INSERT INTO c VALUES (2, NULL)")]
    [InlineData("pid INT DEFAULT 9", "SET DEFAULT", "")]
    [InlineData("pid INT", "NO ACTION", "")]
    [InlineData("pid INT UNIQUE", "SET NULL", "CREATE TABLE g (pid INT REFERENCES c(pid)); INSERT INTO g VALUES (1)")]
    public async Task Delete_InvalidChildResult_ShouldRemainAtomic(string definition, string action, string extra)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("delete-atomic", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, {definition}, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES p(id) ON DELETE {action})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 1)", cancellationToken: CancellationToken.None);
        foreach (string sql in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        }

        // Act / Assert
        var failure = await SqlConstraintAssertions.ThrowAsync(() => session.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None).AsTask());
        if (definition == "pid INT NOT NULL") { failure.ConstraintName.ShouldBe("fk_child"); }
        (await Rows(session, "SELECT id FROM p")).Single()[0].ShouldBe(1);
        (await Rows(session, "SELECT pid FROM c WHERE id = 1")).Single()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NO ACTION: self references use the entire statement deletion set")]
    [InlineData("(1, NULL), (2, 1)")]
    [InlineData("(2, 1), (1, NULL)")]
    public async Task Delete_NoAction_ShouldHonorCompleteDeletionSet(string values)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("no-action", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, pid INT REFERENCES t(id))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO t VALUES {values}", cancellationToken: CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("DELETE FROM t", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT id FROM t")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: CASCADE reaches grandchildren and retains unaffected references")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_CascadeThenSetAction_ShouldUpdateGrandchildren(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cascade-set", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE g (id INT PRIMARY KEY, cid INT DEFAULT 20 REFERENCES c(id) ON DELETE {action})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (10, 1), (20, 2)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO g VALUES (100, 10), (200, 20)", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None);

        // Assert
        (await Rows(session, "SELECT id FROM c")).Single()[0].ShouldBe(20);
        var rows = await Rows(session, "SELECT id, cid FROM g ORDER BY id");
        rows[0].ShouldBe(new object?[] { 100, action == "SET NULL" ? null : 20 });
        rows[1].ShouldBe(new object?[] { 200, 20 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: fixed snapshots crosscheck the cascaded grandchild before mutation")]
    [InlineData("SET NULL", IsolationLevel.Snapshot)]
    [InlineData("SET DEFAULT", IsolationLevel.Snapshot)]
    [InlineData("SET NULL", IsolationLevel.Serializable)]
    [InlineData("SET DEFAULT", IsolationLevel.Serializable)]
    public async Task Delete_NewerGrandchildVersion_ShouldFailFixedSnapshotCrosscheck(string action, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cascade-set-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var parent = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await parent.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync($"CREATE TABLE g (id INT PRIMARY KEY, cid INT DEFAULT 20 REFERENCES c(id) ON DELETE {action})", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("INSERT INTO c VALUES (10, 1), (20, 2)", cancellationToken: CancellationToken.None);
        var transaction = await database.Coordinator.BeginAsync(isolation, CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO g VALUES (100, 10)", cancellationToken: CancellationToken.None);

        // Act / Assert
        // The session refuses Serializable BEGIN; exercise its fixed-snapshot executor context directly.
        var failure = await Should.ThrowAsync<TransactionAbortedException>(() =>
            new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .ExecuteAsync(SqlQueryRequest.FromSql("DELETE FROM p WHERE id = 1"), new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None));
        failure.Message.ShouldContain($"ON DELETE {action} reached", Case.Sensitive);
        failure.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        (await Rows(writer, "SELECT id FROM p ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await Rows(writer, "SELECT id FROM c ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 10, 20 });
        (await Rows(writer, "SELECT cid FROM g")).Single()[0].ShouldBe(10);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: self references change only direct surviving children")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_SelfReferencingSetAction_ShouldUpdateOnlyDirectChildren(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("self-set", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, pid INT DEFAULT 0 REFERENCES t(id) ON DELETE {action})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (0, NULL), (1, NULL), (2, 1), (3, 2), (4, 3)", cancellationToken: CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("DELETE FROM t WHERE id = 1", cancellationToken: CancellationToken.None);
        var rows = await Rows(session, "SELECT id, pid FROM t ORDER BY id");
        rows.Select(row => row[0]).ShouldBe(new object?[] { 0, 2, 3, 4 });
        rows[1][1].ShouldBe(action == "SET NULL" ? null : 0);
        rows[2][1].ShouldBe(2);
        rows[3][1].ShouldBe(3);
        await session.ExecuteAsync("DELETE FROM t WHERE id IN (2, 3)", cancellationToken: CancellationToken.None);
        rows = await Rows(session, "SELECT id, pid FROM t ORDER BY id");
        rows.Select(row => row[0]).ShouldBe(new object?[] { 0, 4 });
        rows[1][1].ShouldBe(action == "SET NULL" ? null : 0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SET DEFAULT: a self reference cannot default back to the deleted root")]
    public async Task Delete_SelfDefaultToDeletedRoot_ShouldRejectAtomically()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("self-deleted-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, pid INT DEFAULT 1 REFERENCES t(id) ON DELETE SET DEFAULT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, NULL), (2, 1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        await SqlConstraintAssertions.ThrowAsync(() => session.ExecuteAsync("DELETE FROM t WHERE id = 1", cancellationToken: CancellationToken.None).AsTask());
        (await Rows(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await Rows(session, "SELECT pid FROM t WHERE id = 2")).Single()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET DEFAULT: absent defaults assign NULL or fail NOT NULL atomically")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_NoColumnDefault_ShouldFollowNullability(bool required)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("absent-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE c (pid INT {(required ? "NOT NULL" : "")} REFERENCES p(id) ON DELETE SET DEFAULT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (1), (2)", cancellationToken: CancellationToken.None);

        // Act / Assert
        if (required)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe("CS03024");
            failure.Message.ShouldBe("Column 'pid' does not allow NULL and has no default.");
            (await Rows(session, "SELECT id FROM p ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
            (await Rows(session, "SELECT pid FROM c ORDER BY pid")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        }
        else
        {
            await session.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None);
            (await Rows(session, "SELECT id FROM p")).ShouldBeEmpty();
            (await Rows(session, "SELECT pid FROM c")).Select(row => row[0]).ShouldBe(new object?[] { null, null });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: cancellation during child validation preserves every row")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_CanceledChildValidation_ShouldRemainAtomic(string action)
    {
        // Arrange: cancel deterministically from a CHECK after collection has completed.
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("cancel-set-action");
        builder.Functions.Add(SqlScalarFunction.Create("validate_child", (int _) =>
        {
            if (armed && ++calls == 128) { cancellation.Cancel(); }
            return true;
        }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT 0 REFERENCES p(id) ON DELETE {action}, CHECK(validate_child(id)))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (0), (1)", cancellationToken: CancellationToken.None);
        string values = string.Join(", ", Enumerable.Range(1, 1_000).Select(id => $"({id}, 1)"));
        await session.ExecuteAsync("INSERT INTO c VALUES " + values, cancellationToken: CancellationToken.None);
        armed = true;

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: cancellation.Token).AsTask());
        calls.ShouldBe(128);
        armed = false;
        (await Rows(session, "SELECT COUNT(*) FROM p")).Single()[0].ShouldBe(2L);
        (await Rows(session, "SELECT COUNT(*) FROM c WHERE pid = 1")).Single()[0].ShouldBe(1_000L);
        await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: CancellationToken.None);
        (await Rows(session, action == "SET NULL" ? "SELECT COUNT(*) FROM c WHERE pid IS NULL" : "SELECT COUNT(*) FROM c WHERE pid = 0")).Single()[0].ShouldBe(1_000L);
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Proves read-time defaults remain correct across positional row rewrites and retained MVCC versions.</summary>
public sealed class SqlAddColumnLayoutTests
{
    /// <summary>Dropping an earlier column preserves an added default and a stored explicit null.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: defaults survive dropping another column")]
    public async Task AddColumn_DefaultThenDropOtherColumn_ShouldPreserveValues()
    {
        // Arrange: the old rows have no stored component for extra.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-layout" });
        var database = await engine.CreateDatabaseAsync("layout");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, obsolete TEXT, payload TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'remove-one', 'one'), (2, 'remove-two', 'two');");
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra INT DEFAULT 7;");
        var before = await RowsAsync(session, "SELECT id, obsolete, payload, extra FROM t ORDER BY id;");
        before.Count.ShouldBe(2);
        before[0].ShouldBe(new object?[] { 1, "remove-one", "one", 7 });
        before[1].ShouldBe(new object?[] { 2, "remove-two", "two", 7 });
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, 'remove-three', 'three', NULL);");

        // Act: DROP rewrites positional records, including the formerly missing tail.
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN obsolete;");
        await ExecuteAsync(session, "UPDATE t SET payload = 'one-updated' WHERE id = 1;");

        // Assert: materializing the rewrite preserves defaults and explicit nulls separately.
        var rows = await RowsAsync(session, "SELECT id, payload, extra FROM t ORDER BY id;");
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(new object?[] { 1, "one-updated", 7 });
        rows[1].ShouldBe(new object?[] { 2, "two", 7 });
        rows[2].ShouldBe(new object?[] { 3, "three", null });
    }

    /// <summary>A newly added column cannot reuse values that belonged to a dropped column at its ordinal.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: dropping and readding a column applies the new default")]
    public async Task AddColumn_AfterDropSameName_ShouldUseNewDefault()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-readd" });
        var database = await engine.CreateDatabaseAsync("readd");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, extra INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 99), (2, 88);");
        await ExecuteAsync(session, "UPDATE t SET extra = 77 WHERE id = 1;");

        // Act
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra;");
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra INT NOT NULL DEFAULT 7;");

        // Assert: original values remain in their own columns and the new column has no stale payload.
        var rows = await RowsAsync(session, "SELECT id, extra FROM t ORDER BY id;");
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, 7 });
        rows[1].ShouldBe(new object?[] { 2, 7 });
        await ExecuteAsync(session, "INSERT INTO t (id) VALUES (3);");
        (await RowsAsync(session, "SELECT id, extra FROM t WHERE id = 3;")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 3, 7 });
    }

    /// <summary>NOT NULL validation considers live rows while an older snapshot can still decode deleted versions.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: NOT NULL on an empty live table preserves retained snapshot rows")]
    public async Task AddColumn_NotNullAfterDelete_ShouldKeepOlderSnapshotReadable()
    {
        // Arrange: the snapshot pins a version which DELETE removes from the current table.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-deleted-version" });
        var database = await engine.CreateDatabaseAsync("deleted-version");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE t (id INT, payload TEXT);");
        await ExecuteAsync(writer, "INSERT INTO t VALUES (1, 'retained');");
        await using var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await ExecuteAsync(writer, "DELETE FROM t;");
        (await RowsAsync(writer, "SELECT * FROM t;")).ShouldBeEmpty();

        // Act: no live row requires a backfill, so an addition without a default is valid.
        await ExecuteAsync(writer, "ALTER TABLE t ADD COLUMN extra INT NOT NULL;");

        // Assert: the old version predates this nullability requirement and has no stored extra value.
        (await RowsAsync(reader, "SELECT id, payload, extra FROM t;")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 1, "retained", null });
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(writer, "INSERT INTO t (id, payload) VALUES (2, 'new');"));
        await ExecuteAsync(writer, "INSERT INTO t VALUES (2, 'new', 8);");
        (await RowsAsync(reader, "SELECT id, payload, extra FROM t;")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 1, "retained", null });
        await snapshot.RollbackAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT id, payload, extra FROM t;")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 2, "new", 8 });
    }

    private static async Task ExecuteAsync(IDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(QueryResultStatus.Success);
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<QueryResultSet>();
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index);
            }
            rows.Add(values);
        }
        return rows;
    }
}

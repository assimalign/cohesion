using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises ADD COLUMN defaults across historical row versions, restart and schema ownership.</summary>
public sealed class SqlAddColumnExecutionTests
{
    /// <summary>An older row snapshot sees complete defaults with its original values after concurrent DDL and DML.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: old snapshots preserve rows with complete defaults")]
    public async Task ExecuteAsync_OlderRowSnapshot_ShouldSeeOriginalRowsAndCompleteDefaults()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-column-mvcc" });
        var database = await engine.CreateDatabaseAsync("additions", cancellationToken: CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE additions (id INT PRIMARY KEY, label TEXT)");
        await ExecuteAsync(writer, "INSERT INTO additions VALUES (1, 'original'), (2, NULL)");
        await using var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var before = await RowsAsync(reader, "SELECT * FROM additions ORDER BY id");
        before[0].ShouldBe(new object?[] { 1, "original" });
        before[1].ShouldBe(new object?[] { 2, null });

        // Act: DDL is published once; later DML creates new physical versions and a new row.
        await ExecuteAsync(writer, "ALTER TABLE additions ADD COLUMN extra INT NOT NULL DEFAULT 7");
        await ExecuteAsync(writer, "CREATE INDEX ix_additions_extra ON additions (extra)");
        await ExecuteAsync(writer, "UPDATE additions SET label = 'current', extra = 19 WHERE id = 1");
        await ExecuteAsync(writer, "INSERT INTO additions (id, label) VALUES (3, 'new')");

        // Assert: schema is bound per statement, while the transaction retains its older row visibility.
        var historical = await RowsAsync(reader, "SELECT * FROM additions ORDER BY id");
        historical.Count.ShouldBe(2);
        historical[0].ShouldBe(new object?[] { 1, "original", 7 });
        historical[1].ShouldBe(new object?[] { 2, null, 7 });
        var indexed = await RowsAsync(reader, "SELECT id, label, extra FROM additions WHERE extra = 7 ORDER BY id");
        indexed.Count.ShouldBe(2);
        indexed[0].ShouldBe(new object?[] { 1, "original", 7 });
        indexed[1].ShouldBe(new object?[] { 2, null, 7 });

        await snapshot.RollbackAsync(CancellationToken.None);
        var current = await RowsAsync(reader, "SELECT * FROM additions ORDER BY id");
        current.Count.ShouldBe(3);
        current[0].ShouldBe(new object?[] { 1, "current", 19 });
        current[1].ShouldBe(new object?[] { 2, null, 7 });
        current[2].ShouldBe(new object?[] { 3, "new", 7 });
    }

    /// <summary>Catalog defaults, collations, old rows, omitted defaults and explicit nulls survive reopening durable images.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: defaults old rows and explicit NULL survive restart")]
    public async Task Restart_DefaultedColumns_ShouldRestoreOldRowsDefaultsCollationAndExplicitNulls()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "add-column-restart",
            StorageStrategy = storage,
        }))
        {
            var database = await engine.CreateDatabaseAsync("additions", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE additions (id INT PRIMARY KEY, label TEXT)");
            await ExecuteAsync(session, "INSERT INTO additions VALUES (1, 'original'), (2, NULL)");
            await ExecuteAsync(session, "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7");
            await ExecuteAsync(session,
                "ALTER TABLE additions ADD COLUMN category TEXT COLLATE case_accent_insensitive NOT NULL DEFAULT 'CAFÉ'");
            await ExecuteAsync(session, "INSERT INTO additions (id, label) VALUES (3, 'omitted')");
            await ExecuteAsync(session, "INSERT INTO additions (id, label, extra) VALUES (4, 'explicit null', NULL)");

            var beforeRestart = await RowsAsync(session, "SELECT id, label, extra, category FROM additions ORDER BY id");
            beforeRestart[0].ShouldBe(new object?[] { 1, "original", 7, "CAFÉ" });
            beforeRestart[1].ShouldBe(new object?[] { 2, null, 7, "CAFÉ" });
            beforeRestart[2].ShouldBe(new object?[] { 3, "omitted", 7, "CAFÉ" });
            beforeRestart[3].ShouldBe(new object?[] { 4, "explicit null", null, "CAFÉ" });
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "add-column-reopened",
            StorageStrategy = storage.CaptureDurableImages(),
        });
        var restored = await reopened.OpenDatabaseAsync("additions", cancellationToken: CancellationToken.None);
        await using var restoredSession = await restored.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(restoredSession, "INSERT INTO additions (id, label) VALUES (5, 'after reopen')");

        // Assert: a folded predicate also proves that the added collation survived catalog serialization.
        var rows = await RowsAsync(restoredSession,
            "SELECT id, label, extra, category FROM additions WHERE category = 'cafe' ORDER BY id");
        rows.Count.ShouldBe(5);
        rows[0].ShouldBe(new object?[] { 1, "original", 7, "CAFÉ" });
        rows[1].ShouldBe(new object?[] { 2, null, 7, "CAFÉ" });
        rows[2].ShouldBe(new object?[] { 3, "omitted", 7, "CAFÉ" });
        rows[3].ShouldBe(new object?[] { 4, "explicit null", null, "CAFÉ" });
        rows[4].ShouldBe(new object?[] { 5, "after reopen", 7, "CAFÉ" });
        var catalog = restored.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("dbo", "additions", out var table).ShouldBeTrue();
        table.FindColumn("extra").ShouldNotBeNull().DefaultLiteral.ShouldBe("7");
        table.FindColumn("category").ShouldNotBeNull().Collation.ShouldBe(Collation.CaseAccentInsensitive);
    }

    /// <summary>Schema ownership still blocks an otherwise valid defaulted addition to a populated table.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: schema-owned populated tables remain protected")]
    public async Task ExecuteAsync_SchemaOwnedTable_ShouldRejectDefaultedAdditionAndPreserveRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-column-owner" });
        var database = await engine.CreateDatabaseAsync("additions", cancellationToken: CancellationToken.None);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "additions", EngineModel.Sql, false, [],
            [new CompiledSchemaTable("additions", "Tests.Additions",
                [new CompiledSchemaColumn("id", DatabaseType.Int32, IsNullable: false),
                 new CompiledSchemaColumn("label", DatabaseType.String, IsNullable: true)], null, [], [])], [], [], [], []);
        await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema,
            cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "INSERT INTO additions VALUES (1, 'original'), (2, NULL)");

        // Act
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session,
            "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7"));

        // Assert
        error.Operation.ShouldBe("ALTER TABLE ADD COLUMN");
        error.OwningSchema.ShouldBe("additions");
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("dbo", "additions", out var table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.FindColumn("extra").ShouldBeNull();
        var rows = await RowsAsync(session, "SELECT * FROM additions ORDER BY id");
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, "original" });
        rows[1].ShouldBe(new object?[] { 2, null });
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string sql)
        => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}

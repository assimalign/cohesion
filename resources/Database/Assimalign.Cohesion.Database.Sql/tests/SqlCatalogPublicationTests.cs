using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlCatalogPublicationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Committed schema and table drops remove live indexes despite caller cancellation")]
    [InlineData(true, StorageCommitDurability.Synchronous)]
    [InlineData(true, StorageCommitDurability.Grouped)]
    [InlineData(true, StorageCommitDurability.None)]
    [InlineData(false, StorageCommitDurability.Synchronous)]
    [InlineData(false, StorageCommitDurability.Grouped)]
    [InlineData(false, StorageCommitDurability.None)]
    public async Task ExecuteAsync_CancelDuringDropPublication_ShouldRemoveHandlesAndReclaimPages(bool schema, StorageCommitDurability durability)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy(durability);
        await using var engine = SqlDatabaseEngine.Create("drop-publication", new()
        {
            StorageStrategy = storage, Durability = durability,
            PageWriteBackInterval = TimeSpan.FromHours(1), CheckpointJournalSize = 0
        });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE sales.t(id INT PRIMARY KEY, value INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE INDEX ix_value ON sales.t(value)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO sales.t VALUES (1, 7)", cancellationToken: timeout.Token);
        if (schema)
        {
            await session.ExecuteAsync("CREATE TABLE sales.other(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO sales.other VALUES (2)", cancellationToken: timeout.Token);
        }
        var retired = database.Catalog.Tables.Select(table => table.ObjectId).ToArray();
        var indexes = retired.SelectMany(database.Catalog.GetIndexes).ToArray();
        indexes.Length.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeTrue(); }
        foreach (ulong id in retired) { database.DataStorage.GetOwnerPages(id).ShouldNotBeEmpty(); }

        // Act
        await CancelDuringPublicationAsync(session, storage, schema ? "DROP SCHEMA sales CASCADE" : "DROP TABLE sales.t", timeout.Token);

        // Assert
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.GetIndexRegistrations().ShouldBeEmpty();
        foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeFalse(); }
        while (database.TableReclamation.HasWork || database.Catalog.GetPendingTableReclamations().Count != 0)
        {
            await Task.Delay(1, timeout.Token);
        }
        foreach (ulong id in retired)
        {
            database.DataStorage.GetOwnerPages(id).ShouldBeEmpty();
            database.CatalogStorage.GetOwnerPages(id).ShouldBeEmpty();
        }
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
        foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeFalse(); }
        engine.Workers.ShouldAllBe(worker => worker.FailureCount == 0);
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER completes published binding and index cleanup despite caller cancellation")]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT", "t", null)]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT NOT NULL DEFAULT 3", "t", null)]
    [InlineData("ALTER TABLE t DROP COLUMN value", "t", null)]
    [InlineData("ALTER TABLE t DROP CONSTRAINT ck", "t", null)]
    [InlineData("ALTER TABLE t RENAME TO renamed", "renamed", null)]
    [InlineData("ALTER TABLE t RENAME COLUMN id TO renamed", "t", null)]
    [InlineData("ALTER TABLE t DROP CONSTRAINT uq", "t", "uq")]
    [InlineData("DROP INDEX uq ON t", "t", "uq")]
    public async Task ExecuteAsync_CancelDuringAlterPublication_ShouldCompleteCleanup(string sql, string tableName, string? droppedIndex)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("alter-publication", new() { StorageStrategy = storage });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY, value INT, CONSTRAINT ck CHECK (id > 0), CONSTRAINT uq UNIQUE (id))", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 7)", cancellationToken: timeout.Token);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        await CancelDuringPublicationAsync(session, storage, sql, timeout.Token);

        // Assert
        database.Catalog.DdlGeneration.ShouldBe(generation + 1);
        database.Catalog.TryGetTable("dbo", tableName, out var table).ShouldBeTrue();
        database.Definitions.IsBound(table).ShouldBeTrue();
        if (droppedIndex is not null) { database.IndexManager.TryGetIndex(table.ObjectId, droppedIndex, out _).ShouldBeFalse(); }
        database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Committed CASCADE binds an uncached external child despite caller cancellation")]
    public async Task ExecuteAsync_CancelDuringCascadePublication_ShouldBindUncachedExternalChild()
    {
        // Arrange: a table published through the catalog has no cached source binding to adopt.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("cascade-binding", new() { StorageStrategy = storage });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE sales.parent(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        var indexes = database.Catalog.GetIndexes(database.Catalog.Tables.Single().ObjectId).ToArray();
        var child = await database.Catalog.ReserveTableAsync("dbo", "child",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true, "7")], [],
            [new SqlCatalogConstraint("fk", SqlCatalogConstraintKind.Reference, ["id"], "sales", "parent", ["id"]),
             new SqlCatalogConstraint("ck", SqlCatalogConstraintKind.Check, [], checkExpression: "id > 0")],
            DatabaseObjectOwner.Adhoc, null, timeout.Token);
        await database.Catalog.PublishTableAsync(child, [], database.Catalog.GetIndexRegistrations(), cancellationToken: timeout.Token);
        database.Definitions.IsBound(child).ShouldBeFalse();

        // Act
        await CancelDuringPublicationAsync(session, storage, "DROP SCHEMA sales CASCADE", timeout.Token);

        // Assert
        database.Catalog.TryGetTable("dbo", "child", out var published).ShouldBeTrue();
        database.Definitions.IsBound(published).ShouldBeTrue();
        published.Constraints.ShouldHaveSingleItem().Name.ShouldBe("ck");
        foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeFalse(); }
        await session.ExecuteAsync("INSERT INTO child DEFAULT VALUES", cancellationToken: timeout.Token);
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema drop and rename finish remaining cleanup before aggregating binding failures")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_PostCommitBindingFailures_ShouldFinishOtherBindingsAndIndexRemoval(bool drop)
    {
        // Arrange: the catalog deliberately stores opaque CHECK text. Two unbound, invalid
        // child definitions force independent failures only when post-commit binding runs.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("cleanup-failures", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE sales.parent(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        var indexes = database.Catalog.GetIndexes(database.Catalog.Tables.Single().ObjectId).ToArray();
        foreach (string name in new[] { "bad_first", "bad_second" })
        {
            var child = await database.Catalog.ReserveTableAsync("dbo", name, [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], [],
                [new SqlCatalogConstraint("fk", SqlCatalogConstraintKind.Reference, ["id"], "sales", "parent", ["id"]),
                 new SqlCatalogConstraint("bad_check", SqlCatalogConstraintKind.Check, [], checkExpression: "missing > 0")],
                DatabaseObjectOwner.Adhoc, null, timeout.Token);
            await database.Catalog.PublishTableAsync(child, [], database.Catalog.GetIndexRegistrations(), cancellationToken: timeout.Token);
        }
        await session.ExecuteAsync("CREATE TABLE good(id INT DEFAULT 7, CONSTRAINT ck CHECK (id > 0), CONSTRAINT fk FOREIGN KEY(id) REFERENCES sales.parent(id))", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "good", out var previous).ShouldBeTrue();
        database.Definitions.IsBound(previous).ShouldBeTrue();

        // Act
        var failure = await Should.ThrowAsync<AggregateException>(async () =>
            await session.ExecuteAsync(drop ? "DROP SCHEMA sales CASCADE" : "ALTER TABLE sales.parent RENAME TO renamed", cancellationToken: timeout.Token));

        // Assert
        failure.InnerExceptions.Count.ShouldBe(2);
        database.Catalog.TryGetTable("dbo", "good", out var published).ShouldBeTrue();
        published.ShouldNotBeSameAs(previous);
        database.Definitions.IsBound(published).ShouldBeTrue();
        if (drop)
        {
            database.Catalog.TryGetTable("sales", "parent", out _).ShouldBeFalse();
            foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeFalse(); }
        }
        else { database.Catalog.TryGetTable("sales", "renamed", out _).ShouldBeTrue(); }
        database.IsOffline.ShouldBeFalse();
    }

    private static async Task CancelDuringPublicationAsync(SqlDatabaseSession session, CrashCaptureSqlStorageStrategy storage,
        string sql, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int cancelled = 0;
        storage.StorageWriteStarting = catalog =>
        {
            // Grouped durability may publish the write on its dedicated flush thread.
            if (catalog && Interlocked.Exchange(ref cancelled, 1) == 0) { cancellation.Cancel(); }
        };
        try
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: cancellation.Token);
            result.Status.ShouldBeOneOf(DatabaseResultStatus.Success, DatabaseResultStatus.Cancelled);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            storage.StorageWriteStarting = null;
        }
        cancelled.ShouldBe(1);
    }
}

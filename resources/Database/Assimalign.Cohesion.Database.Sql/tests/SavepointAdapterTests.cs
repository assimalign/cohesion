using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointAdapterTests
{
    [Fact(DisplayName = "Cohesion Test [SQL] - Savepoint adapters: failed statements and repeated undo retain exactly the prefix lineage")]
    public async Task Undo_FailedStatementAndRepeatedReuse_ShouldRestoreLineageAndRetirement()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("lineage-markers", new());
        var database = await engine.CreateDatabaseAsync("lineage");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, value INT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 0), (2, 20)");
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("UPDATE t SET value = 1 WHERE id = 1");
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        int links = database.DataStorage.RowVersionLinks.Count;
        int retired = database.DataStorage.RowVersionLinks.RetainedCount;
        // Act: duplicate new keys fail after replacement publication in the physical bracket.
        await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("UPDATE t SET value = 9").AsTask());
        await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        for (int round = 0; round < 100; round++)
        {
            await session.ExecuteAsync("UPDATE t SET value = 2 WHERE id = 1");
            await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
            // Assert: retirement cannot grow behind the root writer's snapshot horizon.
            database.DataStorage.RowVersionLinks.Count.ShouldBe(links);
            database.DataStorage.RowVersionLinks.RetainedCount.ShouldBe(retired);
        }
        (await Values(session)).ShouldBe([1L, 20L]);
        await transaction.CommitAsync();
    }

    [Theory(DisplayName = "Cohesion Test [SQL] - Savepoint adapters: lineage, unique keys and crash images follow partial undo")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_ModelWrites_ShouldMatchOracleAndCrashImage(bool commit)
    {
        // Arrange
        var strategy = new TestObjects.FaultInjectingJournalSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("savepoints", new() { StorageStrategy = strategy, CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, value INT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 0)");
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("UPDATE t SET value = 1 WHERE id = 1");
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await session.ExecuteAsync("UPDATE t SET value = 2 WHERE id = 1");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, 9)");
        // Act
        await UndoReaderProbe.RunAsync(async () => { (await Values(observer)).ShouldBe([0L]); }, async () =>
        {
            await session.ExecuteAsync("UPDATE t SET value = 2 WHERE id = 1");
            await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        });
        (await Values(session)).ShouldBe([1L]);
        database.DataStorage.RowVersionLinks.Count.ShouldBe(1);
        await session.ExecuteAsync("UPDATE t SET value = 3 WHERE id = 1");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, 9)");
        if (commit) { await transaction.CommitAsync(); }
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: false);
        database.CatalogStorage.WriteBackDirtyPages(int.MaxValue);
        database.CatalogStorage.WriteAheadJournal.Flush(forceDurable: false);
        strategy.CopyDatabase("source", "image");
        var image = await engine.OpenDatabaseAsync("image");
        await using var reader = await image.CreateSessionAsync();
        // Assert: copied live bytes precede source transaction cleanup.
        (await Values(reader)).ShouldBe(commit ? new long[] { 3, 9 } : new long[] { 0 });
    }

    private static async Task<long[]> Values(SqlDatabaseSession session)
    {
        var result = (await session.ExecuteAsync("SELECT value FROM t ORDER BY id")).ShouldBeAssignableTo<DatabaseResultSet>()!;
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync()) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        return values.ToArray();
    }
}

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
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointAdapterTests
{
    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Savepoint adapters: primary keys, replacements and crash images follow undo")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_ModelWrites_ShouldMatchOracleAndCrashImage(bool commit)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = KeyValueDatabaseEngine.Create("savepoints", new() { StorageStrategy = strategy, CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 0 });
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        await using var committedObserver = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 1 });
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 2 });
        await database.PutAsync(session, new byte[] { 2 }, new byte[] { 9 });
        // Act
        await UndoReaderProbe.RunAsync(async () =>
        {
            (await database.GetAsync(observer, new byte[] { 1 }))!.Value.Value.ToArray().ShouldBe(new byte[] { 0 });
            await using var fresh = await committedObserver.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            (await database.GetAsync(committedObserver, new byte[] { 1 }))!.Value.Value.ToArray().ShouldBe(new byte[] { 0 });
            await fresh.CommitAsync();
        }, async () =>
        {
            await database.PutAsync(session, new byte[] { 1 }, new byte[] { 2 });
            await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        });
        (await database.GetAsync(session, new byte[] { 1 }))!.Value.Value.ToArray().ShouldBe(new byte[] { 1 });
        (await database.GetAsync(session, new byte[] { 2 })).ShouldBeNull();
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 3 });
        await database.PutAsync(session, new byte[] { 2 }, new byte[] { 9 });
        if (commit) { await transaction.CommitAsync(); }
        foreach (var storage in new[] { database.DataStorage, database.CatalogStorage })
        {
            storage.WriteBackDirtyPages(int.MaxValue);
            storage.WriteAheadJournal.Flush(forceDurable: false);
        }
        strategy.CopyDatabase("source", "image");
        var image = await engine.OpenDatabaseAsync("image");
        await using var reader = await image.CreateSessionAsync();
        // Assert
        (await image.GetAsync(reader, new byte[] { 1 }))!.Value.Value.ToArray().ShouldBe(new byte[] { commit ? (byte)3 : (byte)0 });
        (await image.GetAsync(reader, new byte[] { 2 })).HasValue.ShouldBe(commit);
    }
}

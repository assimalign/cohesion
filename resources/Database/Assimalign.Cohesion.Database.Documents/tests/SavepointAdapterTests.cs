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

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointAdapterTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Savepoint adapters: open streams refuse controls and release registration on every completion path")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MarkerControl_OpenStream_ShouldRefuseUntilDisposeOrFailure(bool cancel, bool completeFailure)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("stream-markers", new());
        var database = await engine.CreateDatabaseAsync("streams");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        using var canceled = new CancellationTokenSource();
        await using var stream = database.DataStorage.OpenWrite(database.Coordinator, transaction.Context,
            _ => completeFailure ? ValueTask.FromException(new IOException("Injected completion failure")) : ValueTask.CompletedTask,
            static () => ValueTask.CompletedTask, canceled.Token);
        await stream.WriteAsync(new byte[10_000]);
        // Act / Assert: every control is refused before it changes a marker or fence.
        var create = await Record.ExceptionAsync(() => UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context).AsTask());
        var release = await Record.ExceptionAsync(() => UndoMarkerDriver.ReleaseAsync(database.Coordinator, transaction.Context, marker).AsTask());
        var undo = await Record.ExceptionAsync(() => UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker).AsTask());
        UndoMarkerDriver.Code(create!).ShouldBe("CK04010");
        UndoMarkerDriver.Code(release!).ShouldBe("CK04010");
        UndoMarkerDriver.Code(undo!).ShouldBe("CK04010");
        if (cancel)
        {
            canceled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => stream.WriteAsync(new byte[1], canceled.Token).AsTask());
        }
        if (completeFailure) { await Should.ThrowAsync<IOException>(() => stream.DisposeAsync().AsTask()); }
        else { await stream.DisposeAsync(); }
        await stream.DisposeAsync(); // Idempotence must not decrement a second time.
        var next = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await UndoMarkerDriver.ReleaseAsync(database.Coordinator, transaction.Context, next);
        await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Savepoint adapters: same-writer metadata, chunks and indexes follow partial undo and recovery")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_ModelWrites_ShouldMatchOracleAndCrashImage(bool commit)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = DocumentDatabaseEngine.Create("savepoints", new() { StorageStrategy = strategy, CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE UNIQUE INDEX by_n ON items (n)");
        await collection.PutAsync(session, "one", """{"n":0}"""u8.ToArray());
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        var observing = await observer.GetCollectionAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "one", """{"n":1}"""u8.ToArray());
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await collection.PutAsync(session, "one", """{"n":2}"""u8.ToArray());
        await collection.PutAsync(session, "two", """{"n":9}"""u8.ToArray());
        // Act
        await UndoReaderProbe.RunAsync(async () => { Encoding.UTF8.GetString((await observing.GetAsync(observer, "one"))!.Value.Content.Span).ShouldBe("""{"n":0}"""); }, async () =>
        {
            await collection.PutAsync(session, "one", """{"n":2}"""u8.ToArray());
            await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        });
        Encoding.UTF8.GetString((await collection.GetAsync(session, "one"))!.Value.Content.Span).ShouldBe("""{"n":1}""");
        (await collection.GetAsync(session, "two")).ShouldBeNull();
        await collection.PutAsync(session, "one", """{"n":3}"""u8.ToArray());
        await collection.PutAsync(session, "two", """{"n":9}"""u8.ToArray());
        if (commit) { await transaction.CommitAsync(); }
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: false);
        strategy.CopyDatabase("source", "image");
        var image = await engine.OpenDatabaseAsync("image");
        await using var reader = await image.CreateSessionAsync();
        var recovered = await reader.GetCollectionAsync("items");
        // Assert
        Encoding.UTF8.GetString((await recovered.GetAsync(reader, "one"))!.Value.Content.Span).ShouldBe(commit ? """{"n":3}""" : """{"n":0}""");
        (await recovered.GetAsync(reader, "two")).HasValue.ShouldBe(commit);
    }
}

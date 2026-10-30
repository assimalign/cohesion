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
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointAdapterTests
{
    [Theory(DisplayName = "Cohesion Test [Blob] - Full rollback: retained own versions preserve the live committed catalog and predecessor")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_MarkedRepeatedSave_ShouldPreserveLivePredecessor(bool rollbackToFirst)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("rollback-live", new());
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        var container = await session.CreateContainerAsync("items");
        await Write(container, "one", 0);
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        var observing = await observer.GetContainerAsync("items");
        await using (var transaction = await session.BeginTransactionAsync())
        {
            var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
            await Write(container, "one", 1);
            await Write(container, "one", 2);
            if (rollbackToFirst)
            {
                await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
                await Write(container, "one", 3);
                await Write(container, "one", 4);
            }
            // Act
            await transaction.RollbackAsync();
        }

        // Assert against the live catalog and a snapshot pinned before the writer.
        (await Read(container, "one")).ShouldBe(new byte[20_000]);
        (await Read(observing, "one")).ShouldBe(new byte[20_000]);
        await using var replacement = await session.BeginTransactionAsync();
        await Write(container, "one", 5);
        using (var records = database.DataStorage.GetUnitIterator(0))
        {
            int predecessors = 0;
            while (records.MoveNext())
            {
                var unit = records.Current;
                if (BlobCatalogCodec.Decode(unit.Data.Span, unit.PageId, unit.SlotIndex).Blob is not { } blob || blob.Name != "one") { continue; }
                var stamps = RecordVersionStamp.ReadStamps(unit.Data.Span);
                if (stamps.Writer != replacement.Context.Sequence)
                { stamps.Deleter.ShouldBe(replacement.Context.Sequence); predecessors++; }
            }
            predecessors.ShouldBe(1);
        }
        await replacement.CommitAsync();
        (await Read(container, "one")).ShouldBe(Enumerable.Repeat((byte)5, 20_000).ToArray());
    }

    [Theory(DisplayName = "Cohesion Test [Blob] - Savepoint adapters: open streams refuse controls and release registration on every completion path")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MarkerControl_OpenStream_ShouldRefuseUntilDisposeOrFailure(bool cancel, bool completeFailure)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("stream-markers", new());
        var database = await engine.CreateDatabaseAsync("streams");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        using var canceled = new CancellationTokenSource();
        await using var stream = database.DataStorage.OpenWrite(database.Coordinator, transaction.Context,
            _ => completeFailure ? ValueTask.FromException(new IOException("Injected completion failure")) : ValueTask.CompletedTask,
            static _ => ValueTask.CompletedTask, canceled.Token);
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

    [Theory(DisplayName = "Cohesion Test [Blob] - Savepoint adapters: same-writer metadata, multi-chunk content and crash images follow undo")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_ModelWrites_ShouldMatchOracleAndCrashImage(bool commit)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = BlobDatabaseEngine.Create("savepoints", new() { StorageStrategy = strategy, CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        var container = await session.CreateContainerAsync("items");
        await Write(container, "one", 0);
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        var observing = await observer.GetContainerAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        await using var committedObserver = await database.CreateSessionAsync();
        var committedContainer = await committedObserver.GetContainerAsync("items");
        await Write(container, "one", 1);
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await Write(container, "one", 2);
        await Write(container, "two", 9);
        await session.CreateContainerAsync("transient");
        // Act
        await UndoReaderProbe.RunAsync(async () =>
        {
            (await Read(observing, "one")).ShouldBe(new byte[20_000]);
            await using var fresh = await committedObserver.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            (await Read(committedContainer, "one")).ShouldBe(new byte[20_000]);
            await fresh.CommitAsync();
        }, async () =>
        {
            await Write(container, "one", 2);
            await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        });
        (await Read(container, "one")).ShouldBe(Enumerable.Repeat((byte)1, 20_000).ToArray());
        (await container.GetPropertiesAsync("two")).ShouldBeNull();
        database.Catalog.FindContainer("transient", transaction.Context.Snapshot).ShouldBeNull();
        await Write(container, "one", 3);
        await Write(container, "two", 9);
        await session.CreateContainerAsync("transient");
        if (commit) { await transaction.CommitAsync(); }
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: false);
        strategy.CopyDatabase("source", "image");
        var image = await engine.OpenDatabaseAsync("image");
        await using var reader = await image.CreateSessionAsync();
        var recovered = await reader.GetContainerAsync("items");
        // Assert
        (await Read(recovered, "one")).ShouldBe(Enumerable.Repeat(commit ? (byte)3 : (byte)0, 20_000).ToArray());
        (await recovered.GetPropertiesAsync("two")).HasValue.ShouldBe(commit);
        var names = new List<string>();
        await foreach (var item in reader.GetContainersAsync()) { names.Add(item.Name); }
        names.Contains("transient").ShouldBe(commit);
    }

    private static async Task Write(BlobContainer container, string name, byte value)
    {
        await using var stream = await container.OpenWriteAsync(name);
        await stream.WriteAsync(Enumerable.Repeat(value, 20_000).ToArray());
    }

    private static async Task<byte[]> Read(BlobContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }
}

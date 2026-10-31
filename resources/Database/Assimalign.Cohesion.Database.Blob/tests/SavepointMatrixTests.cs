using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointMatrixTests
{
    public static IEnumerable<object[]> UndoFailures => SavepointUndoFault.Cases();

    [Theory(DisplayName = "Cohesion Test [Blob] - Every undo stage retains the real adapter's prefix and permits a later exact commit (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task RollbackToSavepointAsync_EachUndoStage_ShouldRecoverExactCommit(int stage, int recovery)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = BlobDatabaseEngine.Create("undo-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", token);
        await using var session = await database.CreateSessionAsync(token);
        var container = await session.CreateContainerAsync("items");
        await Write(0);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
        var coordinator = database.Coordinator;
        var context = transaction.Context;
        var outer = await coordinator.CreateSavepointAsync(context, token);
        await Write(1);
        var target = await coordinator.CreateSavepointAsync(context, token);
        for (int i = 0; i < 100; i++) { await Write((byte)(i + 2)); }
        using var fault = new SavepointUndoFault(coordinator, context, stage);
        if (stage >= 7)
        {
            for (int i = 0; i < 100; i++) { await Write((byte)(i + 2)); }
        }

        // Act
        try
        {
            var failure = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(context, target, token).AsTask());
            failure.ShouldNotBeNull();
            fault.IsCauseOf(failure).ShouldBeTrue();
            TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();
            fault.Calls.ShouldBe(fault.ExpectedCalls);
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await container.OpenReadAsync("one", cancellationToken: token));
            refusal.Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        finally { fault.Disarm(); }

        // Assert
        if (recovery == 2) { await transaction.RollbackAsync(token); }
        else
        {
            await coordinator.RollbackToSavepointAsync(context, recovery == 1 ? outer : target, token);
            await coordinator.RollbackToSavepointAsync(context, recovery == 1 ? outer : target, token);
        }
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeFalse();
        if (stage is 2 or 3 or 9 or 10) { fault.Value.ShouldBe(7); }
        if (stage is < 4 or >= 7) { fault.Calls.ShouldBe(2); }
        byte expected = recovery == 0 ? (byte)1 : (byte)0;
        if (recovery != 2) { await transaction.CommitAsync(token); }
        await using var observer = await database.CreateSessionAsync(token);
        var observing = await observer.GetContainerAsync("items");
        await using var stream = await observing.OpenReadAsync("one", cancellationToken: token);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, token);
        bytes.ToArray().ShouldBe(Enumerable.Repeat(expected, 20_000).ToArray());
        await stream.DisposeAsync();
        await using var next = await session.BeginTransactionAsync(cancellationToken: token);
        await Write(9);
        await next.CommitAsync(token);
        await using var committed = await container.OpenReadAsync("one", token);
        using var committedBytes = new MemoryStream();
        await committed.CopyToAsync(committedBytes, token);
        committedBytes.ToArray().ShouldBe(Enumerable.Repeat((byte)9, 20_000).ToArray());

        async Task Write(byte value)
        {
            await using var output = await container.OpenWriteAsync("one", cancellationToken: token);
            await output.WriteAsync(Enumerable.Repeat(value, 20_000).ToArray(), token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - A crash between physical undo brackets scrubs the uncommitted adapter prefix (#1726)")]
    public async Task OpenDatabaseAsync_MidUndoCrash_ShouldRecoverCommittedPredecessor()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = BlobDatabaseEngine.Create("crash-matrix", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        var container = await session.CreateContainerAsync("items");
        await Write(0);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
        await Write(1);
        var target = await database.Coordinator.CreateSavepointAsync(transaction.Context, token);
        for (int i = 0; i < 100; i++) { await Write((byte)(i + 2)); }
        int batches = 0, captured = 0;
        Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
        {
            if (++batches != 2) { return; }
            foreach (var storage in new[] { database.DataStorage })
            {
                storage.WriteBackDirtyPages(int.MaxValue);
                storage.WriteAheadJournal.Flush(forceDurable: false);
            }
            strategy.CopyDatabase("source", "image");
            captured++;
            throw new IOException("Crash image captured inside second undo bracket");
        });

        // Act
        try { await Should.ThrowAsync<IOException>(() => database.Coordinator.RollbackToSavepointAsync(transaction.Context, target, token).AsTask()); }
        finally { Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }

        // Assert: source cleanup cannot repair the already copied image.
        captured.ShouldBe(1);
        batches.ShouldBe(2);
        var image = await engine.OpenDatabaseAsync("image", token);
        await using var observer = await image.CreateSessionAsync(token);
        var recovered = await observer.GetContainerAsync("items");
        await using var input = await recovered.OpenReadAsync("one", token);
        using var bytes = new MemoryStream();
        await input.CopyToAsync(bytes, token);
        bytes.ToArray().ShouldBe(new byte[20_000]);

        async Task Write(byte value)
        {
            await using var output = await container.OpenWriteAsync("one", cancellationToken: token);
            await output.WriteAsync(Enumerable.Repeat(value, 20_000).ToArray(), token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Saturated write streams refuse CK04011, preserve CK04010 and drain without wrapping (#1726)")]
    public async Task OpenWrite_SaturatedRegistration_ShouldPreserveControlRefusalAndDrain()
    {
        // Arrange: register counts without allocating 65,535 content buffers.
        await using var engine = BlobDatabaseEngine.Create("stream-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        var context = transaction.Context;
        var marker = await database.Coordinator.CreateSavepointAsync(context, CancellationToken.None);
        int registered = 0;
        try
        {
            for (; registered < 65_535; registered++) { SavepointStreamCount.Begin(context); }

            // Act
            var failure = Record.Exception(() => database.DataStorage.OpenWrite(database.Coordinator, context,
                static _ => ValueTask.CompletedTask, static _ => ValueTask.CompletedTask, CancellationToken.None));

            // Assert
            failure.ShouldNotBeNull();
            TransactionCoordinator.GetUndoFailureCode(failure).ShouldBe("CK04011");
            var refused = await Record.ExceptionAsync(() => database.Coordinator.RollbackToSavepointAsync(context, marker, CancellationToken.None).AsTask());
            TransactionCoordinator.GetUndoFailureCode(refused!).ShouldBe("CK04010");
        }
        finally { for (int i = 0; i < registered; i++) { SavepointStreamCount.End(context); } }
        await using (var stream = database.DataStorage.OpenWrite(database.Coordinator, context,
            static _ => ValueTask.CompletedTask, static _ => ValueTask.CompletedTask, CancellationToken.None))
        { await stream.WriteAsync(new byte[] { 1 }, CancellationToken.None); }
        await database.Coordinator.RollbackToSavepointAsync(context, marker, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }
}

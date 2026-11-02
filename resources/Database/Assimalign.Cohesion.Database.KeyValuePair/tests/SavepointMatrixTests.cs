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

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointMatrixTests
{
    public static IEnumerable<object[]> UndoFailures => SavepointUndoFault.Cases();

    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Every undo stage retains the real adapter's prefix and permits a later exact commit (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task RollbackToSavepointAsync_EachUndoStage_ShouldRecoverExactCommit(int stage, int recovery)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = KeyValueDatabaseEngine.Create("undo-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", token);
        await using var session = await database.CreateSessionAsync(token);

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
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await database.GetAsync(session, new byte[] { 1 }, cancellationToken: token));
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
        (await database.GetAsync(observer, new byte[] { 1 }, cancellationToken: token))!.Value.Value.ToArray().ShouldBe(new byte[] { expected });
        await using var next = await session.BeginTransactionAsync(cancellationToken: token);
        await Write(9);
        await next.CommitAsync(token);
        (await database.GetAsync(observer, new byte[] { 1 }, cancellationToken: token))!.Value.Value.ToArray().ShouldBe(new byte[] { 9 });

        async Task Write(byte value)
        {
            await database.PutAsync(session, new byte[] { 1 }, new byte[] { value }, cancellationToken: token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - A crash between physical undo brackets scrubs the uncommitted adapter prefix (#1726)")]
    public async Task OpenDatabaseAsync_MidUndoCrash_ShouldRecoverCommittedPredecessor()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = KeyValueDatabaseEngine.Create("crash-matrix", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);

        await Write(0);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
        await Write(1);
        var target = await database.Coordinator.CreateSavepointAsync(transaction.Context, token);
        for (int i = 0; i < 100; i++) { await Write((byte)(i + 2)); }
        int batches = 0, captured = 0;
        Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
        {
            if (++batches != 2) { return; }
            foreach (var storage in new[] { database.DataStorage, database.CatalogStorage })
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
        (await image.GetAsync(observer, new byte[] { 1 }, cancellationToken: token))!.Value.Value.ToArray().ShouldBe(new byte[] { 0 });

        async Task Write(byte value)
        {
            await database.PutAsync(session, new byte[] { 1 }, new byte[] { value }, cancellationToken: token);
        }
    }
}

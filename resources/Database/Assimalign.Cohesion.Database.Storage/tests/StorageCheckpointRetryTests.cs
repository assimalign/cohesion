using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageCheckpointRetryTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Failed admission completes the bracket captured by a busy refusal (#1283)")]
    public void BeginTransaction_TriggerThrowsAfterBusyRefusal_ShouldCompleteCapturedBracket()
    {
        // Arrange
        using var storage = TornStorage.Create();
        storage.CheckpointJournalSize = 1;
        StorageTransaction? captured = null;
        StorageTransactionException? refusal = null;
        var failure = new InvalidOperationException("injected trigger failure");
        storage.OnCheckpointNeeded = () =>
        {
            captured = ActiveBrackets(storage).Keys.Single();
            refusal = Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
            captured.IsActive.ShouldBeTrue();
            throw failure;
        };
        // Act
        Should.Throw<InvalidOperationException>(() => storage.BeginTransaction()).ShouldBeSameAs(failure);
        // Assert: registry cleanup must also complete the captured physical identity.
        captured.ShouldNotBeNull().IsActive.ShouldBeFalse();
        storage.ActiveTransactions.ShouldBe(0);
        ActiveBrackets(storage).ShouldBeEmpty();
        refusal!.Data.Count.ShouldBe(0);
        storage.OnCheckpointNeeded = null;
        storage.Checkpoint();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - A held busy exception cannot retain disposed storage (#1283)")]
    public void Checkpoint_HeldBusyException_ShouldReleaseDisposedStorage()
    {
        // Arrange
        var (storage, refusal) = CaptureDisposedStorage();
        // Act
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        // Assert
        storage.IsAlive.ShouldBeFalse();
        GC.KeepAlive(refusal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Storage, StorageTransactionException Refusal) CaptureDisposedStorage()
    {
        using var storage = TornStorage.Create();
        using var bracket = storage.BeginTransaction();
        StorageTransactionException refusal;
        try { storage.Checkpoint(); throw new InvalidOperationException("Expected busy checkpoint."); }
        catch (StorageTransactionException exception) { refusal = exception; }
        bracket.Rollback();
        return (new WeakReference(storage), refusal);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeBrackets")]
    private static extern ref Dictionary<StorageTransaction, (string Class, long Owner)> ActiveBrackets(Storage storage);

    [Fact(DisplayName = "Cohesion Test [Storage] - Active transaction state includes replacement blockers (#1283)")]
    public void HasActiveTransactions_ReplacementBracket_ShouldKeepCheckpointBlocked()
    {
        // Arrange
        using var storage = TornStorage.Create();
        storage.HasActiveTransactions.ShouldBeFalse();
        using var outside = storage.BeginTransaction("Outside");
        // Act / Assert: completing an intervening bracket cannot unblock the outside one.
        Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
        using (var intervening = storage.BeginTransaction("Statement"))
        {
            storage.HasActiveTransactions.ShouldBeTrue();
            intervening.Rollback();
        }
        storage.HasActiveTransactions.ShouldBeTrue();
        using var replacement = storage.BeginTransaction("Replacement");
        outside.Rollback();
        storage.HasActiveTransactions.ShouldBeTrue();
        Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
        replacement.Rollback();
        storage.HasActiveTransactions.ShouldBeFalse();
        storage.Checkpoint();
    }
}

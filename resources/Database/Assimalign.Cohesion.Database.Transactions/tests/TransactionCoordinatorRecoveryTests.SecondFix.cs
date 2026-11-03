using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public partial class TransactionCoordinatorRecoveryTests
{
    [Fact(DisplayName = "Cohesion Test [Transactions] - Failed admission cannot strand a captured busy retry (#1283)")]
    public async Task Release_FailedAdmissionAfterBusyRefusal_ShouldRetryOnNextRelease()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        int attempts = 0;
        coordinator.BeforeCheckpoint = _ => attempts++;
        storage.CheckpointJournalSize = 1;
        var failure = new InvalidOperationException("injected trigger failure");
        storage.OnCheckpointNeeded = () =>
        {
            Should.Throw<StorageTransactionException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None));
            throw failure;
        };
        // Act
        Should.Throw<InvalidOperationException>(() => storage.BeginTransaction()).ShouldBeSameAs(failure);
        storage.OnCheckpointNeeded = null;
        for (int i = 0; i < 1000; i++)
        {
            using (gate.Enter("Statement"))
            { using var bracket = storage.BeginTransaction(); bracket.Commit(awaitDurability: false); }
        }
        // Assert: each new size crossing can checkpoint, with no stale captured blocker.
        attempts.ShouldBe(1001);
        storage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Inert lease copies may be disposed on another thread (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_InertLeaseOnAnotherThread_ShouldLeaveAdmissionUsable(bool coordinated)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        object binding = new();
        if (coordinated) { gate.ConfigureCheckpoint(binding, storage.Checkpoint, () => true); }
        TransactionCheckpointInterlock.Lease lease = gate.Enter("Catalog");
        if (coordinated) { gate.UnconfigureCheckpoint(binding); }
        else { lease.Dispose(); }
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Act: LongRunning ensures disposal runs on a different dedicated thread.
        await Task.Factory.StartNew(() => lease.Dispose(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(guard.Token);
        // Assert
        gate.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Refused live lease disposal reports at Error level (#1283)")]
    public void CheckpointLeaseThreadMismatch_ErrorListener_ShouldReceiveRefusal()
    {
        // Arrange: exercise the event independently of Debug.Assert's fail-fast contract.
        using var storage = CoordinatorStorage.Create();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Error);
        // Act
        TransactionEventSource.Log.CheckpointLeaseThreadMismatch(storage, 11, 22);
        // Assert
        var refusal = recorder.Events.Single(e => e.EventId == 22);
        refusal.Level.ShouldBe(EventLevel.Error);
        refusal.Payload![1].ShouldBe(11L);
        refusal.Payload[2].ShouldBe(22L);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }
}

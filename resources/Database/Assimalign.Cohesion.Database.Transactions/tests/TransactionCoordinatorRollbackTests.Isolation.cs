using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public partial class TransactionCoordinatorRollbackTests
{
    [Theory(DisplayName = "Cohesion Test [Transactions] - Isolation replacement: immutable contexts preserve identity snapshot and one shared lifecycle")]
    [InlineData(true)][InlineData(false)]
    public async Task ReplaceIsolationLevel_RepeatedReplacement_ShouldPreserveLifecycle(bool commit)
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var pin = await coordinator.BeginSnapshotPinAsync(CancellationToken.None);
        var original = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var snapshot = original.Snapshot;
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await coordinator.CommitAsync(writer, CancellationToken.None);

        // Act
        var readCommitted = coordinator.ReplaceIsolationLevel(original, IsolationLevel.ReadCommitted);
        var replacement = coordinator.ReplaceIsolationLevel(readCommitted, IsolationLevel.Snapshot);
        var statement = replacement.PinStatementSnapshot();

        // Assert
        original.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        readCommitted.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        readCommitted.Snapshot.IsVisible(writer.Sequence).ShouldBeTrue();
        replacement.Snapshot.ShouldBeSameAs(snapshot);
        statement.Snapshot.ShouldBeSameAs(snapshot);
        replacement.Id.ShouldBe(original.Id);
        replacement.Sequence.ShouldBe(original.Sequence);
        coordinator.GetOpenContexts().ShouldContain(replacement);
        coordinator.GetOpenContexts().ShouldNotContain(original);
        if (commit) { await coordinator.CommitAsync(replacement, CancellationToken.None); }
        else { await coordinator.RollbackAsync(replacement, CancellationToken.None); }
        foreach (var handle in new[] { original, readCommitted, replacement, statement })
        { handle.State.ShouldBe(commit ? TransactionState.Committed : TransactionState.RolledBack); }
        await coordinator.RollbackAsync(pin, CancellationToken.None);
        coordinator.GetOpenContexts().ShouldBeEmpty();
        coordinator.PruneBound.ShouldBeGreaterThan(replacement.Sequence);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Isolation replacement: stale view pin applying and ended contexts refuse without changing tracking")]
    public async Task ReplaceIsolationLevel_InvalidContext_ShouldKeepTrackingAndReleaseAdmission()
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var original = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var pin = await coordinator.BeginSnapshotPinAsync(CancellationToken.None);
        var replacement = coordinator.ReplaceIsolationLevel(original, IsolationLevel.ReadCommitted);

        // Act / Assert
        foreach (var invalid in new[] { original, replacement.PinStatementSnapshot(), pin })
        {
            Should.Throw<TransactionAbortedException>(() => coordinator.ReplaceIsolationLevel(invalid, IsolationLevel.Snapshot));
            coordinator.GetOpenContexts().Count().ShouldBe(2);
        }
        var applying = coordinator.Manager.EnterApply(replacement);
        try
        { Should.Throw<TransactionAbortedException>(() => coordinator.ReplaceIsolationLevel(replacement, IsolationLevel.Snapshot)); }
        finally { applying.ExitApply(); }
        var next = coordinator.ReplaceIsolationLevel(replacement, IsolationLevel.Snapshot);
        await coordinator.RollbackAsync(next, CancellationToken.None);
        Should.Throw<TransactionAbortedException>(() => coordinator.ReplaceIsolationLevel(next, IsolationLevel.ReadCommitted));
        await coordinator.RollbackAsync(pin, CancellationToken.None);
        coordinator.GetOpenContexts().ShouldBeEmpty();
    }
}

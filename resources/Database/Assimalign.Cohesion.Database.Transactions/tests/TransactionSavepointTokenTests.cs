using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Undo substrate")]
public sealed class TransactionSavepointTokenTests
{
    [Theory(DisplayName = "Cohesion Test [Transactions] - Live savepoint tokens refuse even same-level isolation replacement (#1726)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task ReplaceIsolationLevel_LiveTokens_ShouldRefuseWithoutInvalidatingThem(IsolationLevel level)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var outer = await coordinator.CreateSavepointAsync(context);
        var inner = await coordinator.CreateSavepointAsync(context);

        // Act / Assert
        Should.Throw<ArgumentException>(() => coordinator.ReplaceIsolationLevel(context, level));
        await coordinator.ReleaseSavepointAsync(context, inner);
        Should.Throw<ArgumentException>(() => coordinator.ReplaceIsolationLevel(context, level));
        await coordinator.RollbackToSavepointAsync(context, outer);
        await coordinator.ReleaseSavepointAsync(context, outer);
        var replacement = coordinator.ReplaceIsolationLevel(context, level);
        replacement.Id.ShouldBe(context.Id);
        replacement.IsolationLevel.ShouldBe(level);
        await coordinator.RollbackAsync(replacement);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Savepoint tokens: foreign coordinators and contexts cannot use a live scope (#1726)")]
    public async Task ReleaseSavepointAsync_ForeignOwner_ShouldRefuseWithoutInvalidatingScope()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        using var otherStorage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        await using var other = new TransactionCoordinator(otherStorage, otherStorage.Log, otherStorage.Records);
        var owner = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var peer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var foreign = await other.BeginAsync(IsolationLevel.Snapshot);
        var scope = await coordinator.CreateSavepointAsync(owner);

        // Act / Assert
        foreach (var target in new[] { (coordinator, peer), (other, foreign) })
        {
            var error = await Record.ExceptionAsync(() => target.Item1.ReleaseSavepointAsync(target.Item2, scope).AsTask());
            TransactionCoordinator.GetUndoFailureCode(error!).ShouldBe("CK04008");
        }
        await coordinator.RollbackToSavepointAsync(owner, scope);
        await coordinator.ReleaseSavepointAsync(owner, scope);
        var released = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(owner, scope).AsTask());
        TransactionCoordinator.GetUndoFailureCode(released!).ShouldBe("CK04008");
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Savepoint tokens: canceled controls and ended contexts preserve structured refusals (#1726)")]
    public async Task CreateSavepointAsync_CancellationAndRootEnd_ShouldKeepOwnedLifetime()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var owner = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await coordinator.CreateSavepointAsync(owner, canceled.Token));
        var scope = await coordinator.CreateSavepointAsync(owner);
        await Should.ThrowAsync<OperationCanceledException>(async () => await coordinator.ReleaseSavepointAsync(owner, scope, canceled.Token));
        await coordinator.RollbackToSavepointAsync(owner, scope);
        await coordinator.CommitAsync(owner);
        var ended = await Record.ExceptionAsync(() => coordinator.ReleaseSavepointAsync(owner, scope).AsTask());
        TransactionCoordinator.GetUndoFailureCode(ended!).ShouldBe("CK04008");
        TransactionCoordinator.GetUndoFailureCode(new InvalidOperationException()).ShouldBeNull();
    }
}

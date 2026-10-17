using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the statement view of a transaction context (<see cref="TransactionContext.PinStatementSnapshot"/>,
/// plan §6.1, #1258): the view keeps one snapshot while its transaction's read-committed snapshot
/// moves on, shares the transaction's identity and state, cannot end the transaction, and forwards
/// the end claim and the apply admission to the transaction's own context.
/// </summary>
public class TransactionStatementViewTests
{
    private static TransactionManager CreateManager()
        => TransactionManager.Create(LockManager.Create(), VersionStore.CreateInMemory());

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: Should keep its snapshot while the read-committed transaction's snapshot moves on")]
    public async Task PinStatementSnapshot_ReadCommittedAfterLaterCommit_ShouldKeepPinnedSnapshot()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();
        var pinned = view.Snapshot;

        var writer = await manager.BeginAsync();
        await manager.CommitAsync(writer);

        // Act
        var viewSnapshot = view.Snapshot;
        var contextSnapshot = context.Snapshot;

        // Assert: the view still hides the later commit; the transaction's refreshed snapshot sees it.
        viewSnapshot.ShouldBeSameAs(pinned);
        viewSnapshot.IsVisible(writer.Sequence).ShouldBeFalse();
        contextSnapshot.IsVisible(writer.Sequence).ShouldBeTrue();

        await manager.CommitAsync(context);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: Should share the transaction's identity, sequence and isolation level")]
    public async Task PinStatementSnapshot_OfActiveContext_ShouldShareIdentity()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);

        // Act
        var view = context.PinStatementSnapshot();

        // Assert
        view.ShouldNotBeSameAs(context);
        view.Id.ShouldBe(context.Id);
        view.Sequence.ShouldBe(context.Sequence);
        view.IsolationLevel.ShouldBe(context.IsolationLevel);
        view.State.ShouldBe(TransactionState.Active);

        await manager.CommitAsync(context);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: Pinning a view should pin the same snapshot again")]
    public async Task PinStatementSnapshot_OfView_ShouldPinSameSnapshot()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();

        var writer = await manager.BeginAsync();
        await manager.CommitAsync(writer);

        // Act
        var repinned = view.PinStatementSnapshot();

        // Assert
        repinned.Snapshot.ShouldBeSameAs(view.Snapshot);
        repinned.Sequence.ShouldBe(context.Sequence);

        await manager.CommitAsync(context);
        repinned.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: The view's state should follow a commit of the transaction")]
    public async Task PinStatementSnapshot_AfterCommit_ShouldReportCommitted()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();

        // Act
        await manager.CommitAsync(context);

        // Assert
        view.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: The view's state should follow a rollback of the transaction")]
    public async Task PinStatementSnapshot_AfterRollback_ShouldReportRolledBack()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();

        // Act
        await manager.RollbackAsync(context);

        // Assert
        context.State.ShouldBe(TransactionState.RolledBack);
        view.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Ownership: A manager should refuse to commit or roll back a statement view")]
    public async Task CommitAndRollback_OfStatementView_ShouldThrowTransactionAborted()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();

        // Act + Assert
        var commit = await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.CommitAsync(view));
        var rollback = await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.RollbackAsync(view));

        commit.Message.ShouldContain("not created by this manager", Case.Sensitive);
        rollback.Message.ShouldContain("not created by this manager", Case.Sensitive);
        context.State.ShouldBe(TransactionState.Active);

        // The refused ends claimed nothing: the transaction itself still commits.
        await manager.CommitAsync(context);
        context.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - PinStatementSnapshot: A view should forward the apply admission and the end claim to its transaction")]
    public async Task PinStatementSnapshot_ApplyAndEnd_ShouldForwardToTransaction()
    {
        // Arrange
        var manager = CreateManager();
        await using var _ = manager;

        var context = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        var view = context.PinStatementSnapshot();

        // Act: an apply admitted through the view is one the transaction's end must wait for.
        view.TryEnterApply().ShouldBeTrue();
        var drained = context.WaitForAppliesAsync();
        var waitedWhileApplying = drained.IsCompleted;
        view.ExitApply();

        // Assert
        waitedWhileApplying.ShouldBeFalse();
        await drained;

        await manager.CommitAsync(context);

        // The transaction's end is claimed: the view can neither claim it again nor admit an apply.
        view.TryClaimEnd().ShouldBeFalse();
        view.TryEnterApply().ShouldBeFalse();
    }
}

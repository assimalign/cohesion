using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Journal enrollment")]
public sealed class TransactionCommitCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Commit: cancellation before the end leaves readers and writers usable (#1246)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionUsable(bool writer)
    {
        // Arrange
        var log = new TransactionCommitLog();
        var locks = LockManager.Create();
        var versions = VersionStore.CreateInMemory();
        await using var manager = TransactionManager.Create(log, locks, versions);
        var context = await manager.BeginAsync();
        if (writer) { await versions.AppendVersionAsync(1, 1, new byte[] { 42 }, context.Sequence); }
        var row = LockResource.Entry(1, 1);
        await locks.AcquireAsync(context.Sequence, row, LockMode.Exclusive);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await manager.CommitAsync(context, canceled.Token));

        // Assert: neither end admission nor the log was touched; another statement and commit work.
        error.CancellationToken.ShouldBe(canceled.Token);
        context.State.ShouldBe(TransactionState.Active);
        manager.OldestActive.ShouldBe(context.Sequence);
        log.CommitCalls.ShouldBe(0);
        log.AbortCalls.ShouldBe(0);
        locks.TryAcquire(new TransactionSequence(ulong.MaxValue), row, LockMode.Exclusive).ShouldBeFalse();
        manager.EnterApply(context).ExitApply();
        await manager.CommitAsync(context);
        context.State.ShouldBe(TransactionState.Committed);
        var observer = await manager.BeginAsync();
        (await versions.GetVisibleVersionAsync(1, 1, observer.Snapshot)).HasValue.ShouldBe(writer);
        await manager.RollbackAsync(observer);
        locks.TryAcquire(new TransactionSequence(ulong.MaxValue), row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(new TransactionSequence(ulong.MaxValue));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Commit: cancellation after the claim reaches neither drain nor append (#1246)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_TokenCanceledAfterClaim_ShouldComplete(bool duringApply)
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        var log = new TransactionCommitLog { DuringCommit = canceled.Cancel };
        await using var manager = TransactionManager.Create(log, LockManager.Create(), VersionStore.CreateInMemory());
        var context = await manager.BeginAsync();
        if (duringApply) { manager.EnterApply(context); }

        // Act
        var commit = manager.CommitAsync(context, canceled.Token).AsTask();
        if (duringApply)
        {
            canceled.Cancel();
            context.ExitApply();
        }
        await commit.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        canceled.IsCancellationRequested.ShouldBeTrue();
        log.CommitCalls.ShouldBe(1);
        log.CommitToken.CanBeCanceled.ShouldBeFalse();
        log.AbortCalls.ShouldBe(0);
        context.State.ShouldBe(TransactionState.Committed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Commit: cancellation cannot replace append or durability failures (#1246)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_TokenCanceledWithLogFailure_ShouldPreserveFailureOutcome(bool unconfirmed)
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        var failure = new IOException("Injected commit failure.");
        var log = new TransactionCommitLog
        {
            DuringCommit = canceled.Cancel,
            Failure = unconfirmed ? new TransactionCommitUnconfirmedException("Injected unconfirmed commit.", failure) : failure,
        };
        var locks = LockManager.Create();
        await using var manager = TransactionManager.Create(log, locks, VersionStore.CreateInMemory());
        var context = await manager.BeginAsync();
        var row = LockResource.Entry(1, 1);
        await locks.AcquireAsync(context.Sequence, row, LockMode.Exclusive);

        // Act
        var error = await Should.ThrowAsync<Exception>(async () => await manager.CommitAsync(context, canceled.Token));

        // Assert
        if (unconfirmed)
        {
            error.ShouldBeSameAs(log.Failure);
            context.State.ShouldBe(TransactionState.Committed);
            log.AbortCalls.ShouldBe(0);
        }
        else
        {
            error.ShouldBeOfType<TransactionAbortedException>().InnerException.ShouldBeSameAs(failure);
            context.State.ShouldBe(TransactionState.Faulted);
            log.AbortCalls.ShouldBe(1);
        }
        log.CommitToken.CanBeCanceled.ShouldBeFalse();
        log.CommitCalls.ShouldBe(1);
        locks.TryAcquire(new TransactionSequence(ulong.MaxValue), row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(new TransactionSequence(ulong.MaxValue));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Commit: repeated cancellation, competing rollback and disposal release every end and lock (#1246)")]
    public async Task CommitAsync_CancellationAndDisposalDuringAppend_ShouldDrainEveryClaim()
    {
        // Arrange
        const int rounds = 64;
        int commits = 0, cancellations = 0, refusedRollbacks = 0, waitingDisposals = 0;
        var row = LockResource.Entry(1, 1);

        // Act: rendezvous at append in every round; timeouts are only hang ceilings.
        for (int round = 0; round < rounds; round++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var canceled = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new TransactionCommitLog { Entered = entered, Release = release };
            var locks = LockManager.Create();
            await using var manager = TransactionManager.Create(log, locks, VersionStore.CreateInMemory());
            var context = await manager.BeginAsync();
            await locks.AcquireAsync(context.Sequence, row, LockMode.Exclusive);
            using var early = new CancellationTokenSource();
            early.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await manager.CommitAsync(context, early.Token));
            cancellations++;
            var commit = DedicatedTestTask.Run(async () => await manager.CommitAsync(context, canceled.Token));
            Task? disposal = null;
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.RollbackAsync(context));
                refusedRollbacks++;
                disposal = manager.DisposeAsync().AsTask();
                disposal.IsCompleted.ShouldBeFalse();
                waitingDisposals++;
                await DedicatedTestTask.Run(canceled.Cancel, timeout.Token);
                canceled.IsCancellationRequested.ShouldBeTrue();
                commit.IsCompleted.ShouldBeFalse();
                disposal.IsCompleted.ShouldBeFalse();
            }
            finally { release.TrySetResult(); }
            await commit.WaitAsync(timeout.Token);
            if (disposal is not null) { await disposal.WaitAsync(timeout.Token); }
            context.State.ShouldBe(TransactionState.Committed);
            log.CommitCalls.ShouldBe(1);
            log.CommitToken.CanBeCanceled.ShouldBeFalse();
            log.AbortCalls.ShouldBe(0);
            cancellations.ShouldBe(round + 1);
            refusedRollbacks.ShouldBe(round + 1);
            waitingDisposals.ShouldBe(round + 1);
            locks.TryAcquire(new TransactionSequence(ulong.MaxValue), row, LockMode.Exclusive).ShouldBeTrue();
            locks.ReleaseAll(new TransactionSequence(ulong.MaxValue));
            commits++;
        }

        // Assert
        commits.ShouldBe(rounds);
        cancellations.ShouldBe(commits);
        refusedRollbacks.ShouldBe(commits);
        waitingDisposals.ShouldBe(commits);
    }
}

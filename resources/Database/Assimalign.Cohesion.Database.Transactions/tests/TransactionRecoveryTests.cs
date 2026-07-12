using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Atomicity tests binding the transaction manager to the storage journal (#850):
/// commit is durable in the journal before acknowledgment, a crash mid-commit leaves
/// no partial effects after replay, and recovery classifies sequences correctly.
/// </summary>
public class TransactionRecoveryTests
{
    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Journal binding: commit record is durable before acknowledgment")]
    public async Task Commit_JournalBoundLog_ShouldBeDurableBeforeReturn()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);
        var manager = TransactionManager.Create(
            TransactionLog.CreateJournalBound(journal), LockManager.Create(), VersionStore.CreateInMemory());
        await using var _ = manager;

        var transaction = await manager.BeginAsync();

        // Act
        await manager.CommitAsync(transaction);

        // Assert: the journal is durable through the commit record.
        journal.DurableLsn.ShouldBe(journal.LastLsn);
        var plan = TransactionRecovery.Analyze(journal);
        plan.Committed.ShouldContain(transaction.Sequence);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Recovery: crash mid-commit leaves no partial effects")]
    public async Task Recovery_CrashBeforeCommitRecord_ShouldPurgeUncommittedVersions()
    {
        // Arrange: one committed transaction, one in-flight when the process dies.
        using var stream = new MemoryStream();

        TransactionSequence committedSequence;
        TransactionSequence crashedSequence;

        {
            using var journal = new StreamJournal(stream, leaveOpen: true);
            var versions = VersionStore.CreateInMemory();
            var manager = TransactionManager.Create(
                TransactionLog.CreateJournalBound(journal), LockManager.Create(), versions);

            var committed = await manager.BeginAsync();
            await versions.AppendVersionAsync(1, 1, Payload("survives"), committed.Sequence);
            await manager.CommitAsync(committed);
            committedSequence = committed.Sequence;

            var crashed = await manager.BeginAsync();
            await versions.AppendVersionAsync(1, 2, Payload("vanishes"), crashed.Sequence);
            crashedSequence = crashed.Sequence;

            // Crash: no commit, no rollback, manager never disposed cleanly.
        }

        // Act: restart — analyze the journal and rebuild the version store the way
        // an engine would (re-appending committed work from its own storage replay,
        // purging anything the journal does not prove committed).
        using var reopenedJournal = new StreamJournal(stream, leaveOpen: true);
        var plan = TransactionRecovery.Analyze(reopenedJournal);

        // Assert
        plan.Committed.ShouldContain(committedSequence);
        plan.Committed.ShouldNotContain(crashedSequence);
        plan.Aborted.ShouldContain(crashedSequence);
        plan.MaxSequence.ShouldBe(crashedSequence);

        // The recovered store: committed work re-applied, aborted work purged.
        var recovered = VersionStore.CreateInMemory();
        await recovered.AppendVersionAsync(1, 1, Payload("survives"), committedSequence);
        await recovered.AppendVersionAsync(1, 2, Payload("vanishes"), crashedSequence);

        foreach (var aborted in plan.Aborted)
        {
            await recovered.PurgeWriterAsync(aborted);
        }

        var everything = new TransactionSnapshot(
            new TransactionSequence(plan.MaxSequence.Value + 1),
            new TransactionSequence(plan.MaxSequence.Value + 1),
            new TransactionSequence(plan.MaxSequence.Value + 2),
            Array.Empty<TransactionSequence>());

        (await recovered.GetVisibleVersionAsync(1, 1, everything)).ShouldNotBeNull();
        (await recovered.GetVisibleVersionAsync(1, 2, everything)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Recovery: explicit rollback classifies as aborted")]
    public async Task Recovery_RolledBackTransaction_ShouldClassifyAsAborted()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);
        var manager = TransactionManager.Create(
            TransactionLog.CreateJournalBound(journal), LockManager.Create(), VersionStore.CreateInMemory());
        await using var _ = manager;

        var transaction = await manager.BeginAsync();
        await manager.RollbackAsync(transaction);

        // Act
        var plan = TransactionRecovery.Analyze(journal);

        // Assert
        plan.Aborted.ShouldContain(transaction.Sequence);
        plan.Committed.ShouldBeEmpty();
    }
}

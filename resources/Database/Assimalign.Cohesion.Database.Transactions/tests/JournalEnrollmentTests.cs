using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Journal enrollment")]
public sealed class JournalEnrollmentTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Standalone journal: a reader ends without appending or flushing and releases its locks (#1280)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task End_ReadOnlyTransaction_ShouldLeaveJournalUntouchedAndReleaseLocks(int end)
    {
        // Arrange
        using var media = new CrashSimulationStream();
        using var journal = StorageJournal.Create(media, leaveOpen: true);
        var locks = new LockManager();
        await using var manager = TransactionManager.Create(
            TransactionLog.CreateJournalBound(journal), locks, VersionStore.CreateInMemory());
        long length = journal.Length;
        long lsn = journal.LastLsn;
        int flushes = media.FlushCount;
        var reader = await manager.BeginAsync(cancellationToken: CancellationToken.None);
        var resource = LockResource.Entry(1, 1);
        await locks.AcquireAsync(reader.Sequence, resource, LockMode.Exclusive, CancellationToken.None);

        // Act
        if (end == 0)
        {
            await manager.CommitAsync(reader, CancellationToken.None);
        }
        else if (end == 1)
        {
            await manager.RollbackAsync(reader, CancellationToken.None);
        }
        else if (end == 2)
        {
            await manager.DisposeAsync();
        }
        else
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await manager.RollbackAsync(reader, canceled.Token));
            reader.State.ShouldBe(TransactionState.Active);
            await manager.RollbackAsync(reader, CancellationToken.None);
        }

        // Assert
        journal.Length.ShouldBe(length);
        journal.LastLsn.ShouldBe(lsn);
        media.FlushCount.ShouldBe(flushes);
        locks.TryAcquire(new TransactionSequence(reader.Sequence.Value + 1), resource, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(new TransactionSequence(reader.Sequence.Value + 1));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Standalone journal: a failed enrollment releases apply admission so rollback and disposal complete (#1280)")]
    public async Task EnterApply_BeginRecordRefused_ShouldReleaseAdmission()
    {
        // Arrange
        using var media = new CrashSimulationStream();
        using var journal = StorageJournal.Create(media, leaveOpen: true);
        await using var manager = TransactionManager.Create(
            TransactionLog.CreateJournalBound(journal), new LockManager(), VersionStore.CreateInMemory());
        var reader = await manager.BeginAsync(cancellationToken: CancellationToken.None);
        journal.Dispose();

        // Act
        Should.Throw<ObjectDisposedException>(() => manager.EnterApply(reader));
        await manager.RollbackAsync(reader, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        reader.State.ShouldBe(TransactionState.RolledBack);
        manager.OldestActive.Value.ShouldBeGreaterThan(reader.Sequence.Value);
    }
}

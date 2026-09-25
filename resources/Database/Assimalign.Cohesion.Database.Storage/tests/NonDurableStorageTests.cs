using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class NonDurableStorageTests
{
    [Fact]
    public void None_PreservesCommitAndRollbackThroughWriteBackCheckpointAndShutdown()
    {
        var data = new ObservedHandle();
        var journal = new ObservedHandle();
        var storage = HarnessStorage.Create(data, journal);
        var wal = storage.Wal;
        storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
        storage.SupportsDurableFlush.ShouldBeFalse();

        (PageId PageId, int SlotIndex) location;
        using (var transaction = storage.BeginTransaction())
        {
            location = storage.Insert(transaction, [1, 2, 3]);
            transaction.Commit();
            transaction.IsActive.ShouldBeFalse();
        }

        wal.ReadAll().Count(record => record.Type == JournalRecordType.CommitTransaction).ShouldBe(1);
        storage.Read(location).ShouldBe(new byte[] { 1, 2, 3 });
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, location, [4, 5, 6]);
            storage.WriteBackDirtyPages(16).ShouldBeGreaterThan(0);
            transaction.Rollback();
        }

        storage.Read(location).ShouldBe(new byte[] { 1, 2, 3 });
        storage.FlushPendingCommits().ShouldBeFalse();
        storage.FlushChanges();
        storage.Checkpoint();
        wal.ReadAll().Single().Type.ShouldBe(JournalRecordType.Checkpoint);
        wal.DurableLsn.ShouldBe(0L);
        long checkpointLsn = wal.LastLsn;
        storage.Dispose();

        wal.LastLsn.ShouldBeGreaterThan(checkpointLsn);
        wal.DurableLsn.ShouldBe(0L);
        data.DurableFlushRequests.ShouldBe(0);
        journal.DurableFlushRequests.ShouldBe(0);
        data.OrdinaryFlushRequests.ShouldBeGreaterThan(0);
        journal.OrdinaryFlushRequests.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void None_RecoveryReplaysCommittedRowsAndUndoesStolenUncommittedChanges(bool stealUncommitted)
    {
        var data = new ObservedHandle();
        var journal = new ObservedHandle();
        using var original = HarnessStorage.Create(data, journal);
        (PageId PageId, int SlotIndex) location;
        using (var committed = original.BeginTransaction())
        {
            location = original.Insert(committed, [7, 8, 9]);
            committed.Commit();
        }

        using var uncommitted = original.BeginTransaction();
        if (stealUncommitted)
        {
            original.Update(uncommitted, location, [9, 8, 7]);
            original.WriteBackDirtyPages(16).ShouldBeGreaterThan(0);
        }

        // Capture the current live bytes, not a claim that memory survived a crash.
        // The same redo/undo algorithm must work without requesting durable I/O.
        var reopenedData = new ObservedHandle(data.Bytes);
        var reopenedJournal = new ObservedHandle(journal.Bytes);
        using var reopened = HarnessStorage.Open(reopenedData, reopenedJournal);
        reopened.Read(location).ShouldBe(new byte[] { 7, 8, 9 });
        reopened.Wal.DurableLsn.ShouldBe(0L);
        reopened.Wal.ReadAll().Single().Type.ShouldBe(JournalRecordType.Checkpoint);
        reopenedData.DurableFlushRequests.ShouldBe(0);
        reopenedJournal.DurableFlushRequests.ShouldBe(0);
    }

    [Fact]
    public async Task None_ActiveTransactionAsyncShutdownDoesNotRequestDurability()
    {
        var data = new ObservedHandle();
        var journal = new ObservedHandle();
        var storage = HarnessStorage.Create(data, journal);
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, [10, 11]);
        var wal = storage.Wal;

        // Leave the transaction active to exercise the non-checkpoint shutdown path.
        await storage.DisposeAsync();

        wal.DurableLsn.ShouldBe(0L);
        data.DurableFlushRequests.ShouldBe(0);
        journal.DurableFlushRequests.ShouldBe(0);
        data.OrdinaryFlushRequests.ShouldBeGreaterThan(0);
        journal.OrdinaryFlushRequests.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ReopenedNonDurableJournalDoesNotClaimExistingBytesAreDurable()
    {
        using var memory = new MemoryStream();
        using (var journal = new StreamJournal(memory, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendCommit(1);
            journal.Flush();
        }

        using var reopened = new StreamJournal(memory, leaveOpen: true);
        reopened.LastLsn.ShouldBe(2L);
        reopened.DurableLsn.ShouldBe(0L);
        Should.Throw<NotSupportedException>(() => reopened.EnsureDurable(reopened.LastLsn));
        reopened.DurableLsn.ShouldBe(0L);
    }

    [Theory]
    [InlineData(false, false, StorageCommitDurability.None)]
    [InlineData(true, false, StorageCommitDurability.None)]
    [InlineData(false, true, StorageCommitDurability.None)]
    [InlineData(true, true, StorageCommitDurability.Synchronous)]
    public void DefaultRequiresBothDataAndJournalDurableBacking(bool durableData, bool durableJournal, StorageCommitDurability expected)
    {
        // Durable handles here explicitly simulate the capability. The real physical
        // flush contract is observed separately by FileSystemDurabilityTests.
        using var storage = HarnessStorage.Create(new ObservedHandle(durable: durableData), new ObservedHandle(durable: durableJournal));
        storage.CommitDurability.ShouldBe(expected);
        storage.SupportsDurableFlush.ShouldBe(durableData && durableJournal);
        ((byte)StorageCommitDurability.Synchronous).ShouldBe((byte)0);
        ((byte)StorageCommitDurability.Grouped).ShouldBe((byte)1);
        ((byte)StorageCommitDurability.None).ShouldBe((byte)2);
    }

    [Theory]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public void ExplicitDurabilityRequiresCapableBackingAndNamesStoreAndSetting(StorageCommitDurability requested)
    {
        using var storage = HarnessStorage.Create(new ObservedHandle(), new ObservedHandle());

        var error = Should.Throw<NotSupportedException>(() => storage.ConfigureCommitDurability(requested, "unsupported-store"));

        error.Message.ShouldContain("unsupported-store");
        error.Message.ShouldContain(requested.ToString());
        error.Message.ShouldContain("SupportsDurableFlush = false");
        storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
        storage.Wal.LastLsn.ShouldBe(0L);
    }

    private sealed class HarnessStorage : Storage
    {
        private HarnessStorage(ObservedHandle data, ObservedHandle journal)
            : base(new StorageStream(data), new StorageStream(journal), StorageStream.FromInMemory())
        {
            ConfigureCommitDurability(null, "non-durable-regression");
        }

        public override StorageModel Model => StorageModel.Custom;
        public IStorageJournal Wal => WriteAheadLog;

        public static HarnessStorage Create(ObservedHandle data, ObservedHandle journal)
        {
            var storage = new HarnessStorage(data, journal);
            storage.InitializeNew((Name)"non-durable-regression");
            return storage;
        }

        public static HarnessStorage Open(ObservedHandle data, ObservedHandle journal)
        {
            var storage = new HarnessStorage(data, journal);
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, byte[] bytes)
            => InsertRecord(transaction, bytes);

        public byte[] Read((PageId PageId, int SlotIndex) location)
            => ReadRecord(location.PageId, location.SlotIndex).ToArray();

        public void Update(IStorageTransaction transaction, (PageId PageId, int SlotIndex) location, byte[] bytes)
            => UpdateRecord(transaction, location.PageId, location.SlotIndex, bytes);

        public void FlushChanges() => Flush();
    }

    private sealed class ObservedHandle : IFileSystemFileHandle
    {
        private readonly MemoryStream _memory = new();
        private readonly IFileSystemFileHandle _inner;

        public ObservedHandle(byte[]? bytes = null, bool durable = false)
        {
            if (bytes is not null)
            {
                _memory.Write(bytes);
            }
            _inner = durable ? new SimulatedDurableFileHandle(_memory) : new StorageStream(_memory);
        }

        public byte[] Bytes => _memory.ToArray();
        public int DurableFlushRequests { get; private set; }
        public int OrdinaryFlushRequests { get; private set; }
        public bool SupportsDurableFlush => _inner.SupportsDurableFlush;
        public long Length => _inner.Length;
        public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, offset, cancellationToken);
        public void Write(ReadOnlySpan<byte> buffer, long offset) => _inner.Write(buffer, offset);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, offset, cancellationToken);
        public void SetLength(long length) => _inner.SetLength(length);
        public void Flush(bool durable)
        {
            if (durable)
            {
                DurableFlushRequests++;
            }
            else
            {
                OrdinaryFlushRequests++;
            }
            _inner.Flush(durable);
        }
        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush(durable);
            return default;
        }
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

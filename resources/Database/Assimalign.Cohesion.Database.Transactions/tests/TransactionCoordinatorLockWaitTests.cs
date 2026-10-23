using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// The coordinator's lock manager and its statement apply gate (#1390). Every writer, undo and
/// prune of a database waits for the gate, and the gate is no resource of the lock table, so a
/// lock wait under it would stall the database and could close a cycle the deadlock detector never
/// sees. PostgreSQL keeps the same rule for its buffer content locks, which have no deadlock
/// detection: <c>heap_delete</c> unlocks the buffer before it waits for a tuple lock.
/// </summary>
public class TransactionCoordinatorLockWaitTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly LockResource _held = LockResource.Entry(3, 1);
    private static readonly LockResource _preAcquired = LockResource.Entry(3, 2);

    /// <summary>
    /// A request that must wait inside a statement apply is refused at once, the statement's bracket
    /// rolls back, and the gate is free for the next statement. A lock the statement acquired
    /// before its apply is granted again inside it, as the executors' index maintenance relies on,
    /// and the same request outside an apply waits as any other.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator locks: A lock request that would wait inside a statement apply is refused and the statement writes nothing (#1390)")]
    public async Task ApplyStatementAsync_LockRequestThatMustWait_ShouldBeRefusedAndWriteNothing()
    {
        // Arrange: the holder holds one row; the writer acquired another before its statement.
        using var storage = LockWaitStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await locks.AcquireAsync(holder.Sequence, _held, LockMode.Exclusive);
        await locks.AcquireAsync(writer.Sequence, _preAcquired, LockMode.Exclusive);
        bool regrantedAtOnce = false;

        // Act: inside its apply the writer writes a record, re-requests its own lock, then requests
        // the holder's.
        var refused = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await coordinator.ApplyStatementAsync<int>(writer, async bracket =>
            {
                storage.Insert(bracket, Record(writer));
                var regrant = locks.AcquireAsync(writer.Sequence, _preAcquired, LockMode.Exclusive);
                regrantedAtOnce = regrant.IsCompletedSuccessfully;
                await regrant;
                await locks.AcquireAsync(writer.Sequence, _held, LockMode.Exclusive);
                return 0;
            }).AsTask().WaitAsync(_timeout));
        bool recordSurvived = storage.TryReadRecord(storage.LastInserted.PageId, storage.LastInserted.SlotIndex, out _);

        // The gate is free: another statement of the writer applies.
        int applied = await coordinator.ApplyStatementAsync(writer, _ => 1).AsTask().WaitAsync(_timeout);

        // Outside an apply the same request waits for the holder, as any other.
        var outside = locks.AcquireAsync(writer.Sequence, _held, LockMode.Exclusive).AsTask();
        bool waitedOutside = !outside.IsCompleted;
        await coordinator.CommitAsync(holder);
        await outside.WaitAsync(_timeout);
        await coordinator.CommitAsync(writer);

        // Assert
        regrantedAtOnce.ShouldBeTrue();
        refused.Message.ShouldStartWith($"Transaction {writer.Sequence}'s request for {LockMode.Exclusive} on {_held} would wait inside a statement apply", Case.Sensitive);
        recordSurvived.ShouldBeFalse();
        applied.ShouldBe(1);
        waitedOutside.ShouldBeTrue();
    }

    private static byte[] Record(TransactionContext writer)
    {
        byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
        RecordVersionStamp.WriteWriter(record, writer.Sequence);
        return record;
    }

    /// <summary>An in-memory storage with the coordinator's record space over its records.</summary>
    private sealed class LockWaitStorage : Storage.Storage
    {
        private RecordSpace? _records;

        private LockWaitStorage()
            : base(StorageModel.KeyValue, new StorageStream(new SimulatedDurableFileHandle(new MemoryStream())),
                new StorageStream(new SimulatedDurableFileHandle(new FaultingMemoryStream())), new StorageStream(new MemoryStream()))
        {
            CommitDurability = StorageCommitDurability.Synchronous;
            InitializeNew((Name)"lock-wait-test");
        }

        internal StorageJournal Log => WriteAheadLog;

        internal (PageId PageId, int SlotIndex) LastInserted { get; private set; }

        internal TransactionRecordSpace Records => _records ??= new RecordSpace(this);

        internal static LockWaitStorage Create() => new();

        internal void Insert(StorageTransaction bracket, ReadOnlySpan<byte> data) => LastInserted = InsertRecord(bracket, data);

        private sealed class RecordSpace : TransactionRecordSpace
        {
            private readonly LockWaitStorage _storage;

            internal RecordSpace(LockWaitStorage storage)
            {
                _storage = storage;
            }

            protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadRecord(pageId, slotIndex);

            protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
                => _storage.UpdateRecord(transaction, pageId, slotIndex, record);

            protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
                => _storage.DeleteRecord(transaction, pageId, slotIndex);

            protected override ulong PackLocationCore(PageId pageId, int slotIndex) => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

            protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location)
                => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));
        }
    }
}

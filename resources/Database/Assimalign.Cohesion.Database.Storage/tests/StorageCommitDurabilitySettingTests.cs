using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The <see cref="Storage.CommitDurability"/> and <see cref="Storage.GroupCommitWindow"/> setters
/// (owner decision 26 of 2026-10-06). An undefined mode or an out-of-range window is refused. A
/// change is allowed while transactions are active and applies from the next commit, as
/// PostgreSQL's <c>synchronous_commit</c> does; a commit reads one value throughout; a change waits
/// for a running checkpoint, which reads one value throughout; and an initialized storage never
/// leaves <see cref="StorageCommitDurability.None"/> for a durable mode.
/// </summary>
public sealed class StorageCommitDurabilitySettingTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Durability setting: an undefined mode is refused and the setting is kept")]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void CommitDurability_UndefinedValue_ShouldThrowAndKeepTheSetting(int value)
    {
        // Arrange
        using var storage = SettingStorage.Create();
        storage.CommitDurability = StorageCommitDurability.Grouped;

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => storage.CommitDurability = (StorageCommitDurability)value).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => storage.ConfigureCommitDurability((StorageCommitDurability)value, "undefined")).ParamName.ShouldBe("durability");
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Grouped);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a negative or over-long group-commit window is refused and the window is kept")]
    public void GroupCommitWindow_OutOfRange_ShouldThrowAndKeepTheWindow()
    {
        // Arrange
        using var storage = SettingStorage.Create();
        storage.GroupCommitWindow = TimeSpan.FromMilliseconds(7);

        // Act / Assert: a monitor wait takes no longer timeout, so a longer window failed the
        // waiting commit after its record was journaled.
        Should.Throw<ArgumentOutOfRangeException>(() => storage.GroupCommitWindow = TimeSpan.FromTicks(-1)).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => storage.GroupCommitWindow = Storage.MaximumGroupCommitWindow + TimeSpan.FromTicks(1)).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => storage.GroupCommitWindow = TimeSpan.MaxValue);
        storage.GroupCommitWindow.ShouldBe(TimeSpan.FromMilliseconds(7));

        storage.GroupCommitWindow = TimeSpan.Zero;
        storage.GroupCommitWindow.ShouldBe(TimeSpan.Zero);
        storage.GroupCommitWindow = Storage.MaximumGroupCommitWindow;
        storage.GroupCommitWindow.ShouldBe(TimeSpan.FromMilliseconds(int.MaxValue));
    }

    /// <summary>
    /// PostgreSQL: "the behavior for any one transaction is determined by the setting in effect
    /// when it commits" (<c>doc/src/sgml/config.sgml:3458-3460</c>). A transaction begun under one
    /// mode commits under the mode set before its commit, and the change does not wait for it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a change while a transaction is active is allowed and applies at its commit")]
    public void CommitDurability_ChangedWhileATransactionIsActive_ShouldApplyAtItsCommit()
    {
        // Arrange: a transaction begun under Grouped, whose commit would register with the gate.
        using var storage = SettingStorage.Create();
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromSeconds(5);
        int pending = 0;
        storage.OnCommitPending = () => Interlocked.Increment(ref pending);

        using var grouped = storage.BeginTransaction();
        storage.Insert(grouped, [1, 2, 3]);

        // Act: Grouped to Synchronous while the transaction is active, then its commit.
        storage.CommitDurability = StorageCommitDurability.Synchronous;
        grouped.Commit();

        // Assert: it flushed inline, never registering with the group-commit gate.
        pending.ShouldBe(0);
        storage.Wal.DurableLsn.ShouldBeGreaterThanOrEqualTo(grouped.CommitRecordLsn);

        // Arrange: a transaction begun under Synchronous.
        using var synchronous = storage.BeginTransaction();
        storage.Insert(synchronous, [4, 5, 6]);
        long durable = storage.Wal.DurableLsn;
        int durableFlushes = storage.JournalHandle.DurableFlushes;

        // Act: Synchronous to None while the transaction is active, then its commit.
        storage.CommitDurability = StorageCommitDurability.None;
        synchronous.Commit();

        // Assert: its record left the process (#1252) without a durable flush.
        storage.Wal.WrittenLsn.ShouldBeGreaterThanOrEqualTo(synchronous.CommitRecordLsn);
        storage.Wal.DurableLsn.ShouldBe(durable);
        storage.JournalHandle.DurableFlushes.ShouldBe(durableFlushes);
    }

    /// <summary>
    /// A commit reads the setting once, when its storage bracket starts to commit, and uses it for
    /// its durability check and its wait: a change made while the commit journals its page records
    /// applies from the next commit.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a commit applies the setting it started with, whatever changes during it")]
    public void CommitDurability_ChangedDuringACommit_ShouldNotChangeThatCommit()
    {
        // Arrange: a small append buffer, so the commit's page record is written to the journal's
        // handle while the commit runs, and a hook on that write that turns durability off.
        using var storage = SettingStorage.Create();
        storage.Wal.MaximumBufferBytes = 4096;
        using var transaction = storage.BeginTransaction();
        storage.Insert(transaction, Enumerable.Repeat((byte)0xAB, 6000).ToArray());
        storage.JournalHandle.OnNextWrite = () => storage.CommitDurability = StorageCommitDurability.None;

        // Act
        transaction.Commit();

        // Assert: the change ran inside the commit, and the commit still waited durably.
        storage.JournalHandle.OnNextWrite.ShouldBeNull();
        storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
        storage.Wal.DurableLsn.ShouldBeGreaterThanOrEqualTo(transaction.CommitRecordLsn);
    }

    /// <summary>
    /// The setter takes the lock a checkpoint holds throughout, so a change waits for a running
    /// checkpoint, and the checkpoint flushes its journal truncation under the setting it started
    /// with. Before decision 26 the change landed mid-checkpoint: the header write had flushed the
    /// data file under the old setting, and the journal truncation flushed under the new one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a change waits for a running checkpoint, which reads one setting throughout")]
    public async Task CommitDurability_ChangedDuringACheckpoint_ShouldWaitForIt()
    {
        // Arrange: a committed change, and a checkpoint held inside its durable data flush.
        using var storage = SettingStorage.Create();
        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, [7]);
            transaction.Commit();
        }

        using var flushEntered = new ManualResetEventSlim();
        using var releaseFlush = new ManualResetEventSlim();
        storage.DataHandle.OnNextDurableFlush = () =>
        {
            flushEntered.Set();
            releaseFlush.Wait(TimeSpan.FromSeconds(30));
        };

        var checkpoint = Task.Run(() => storage.Checkpoint());
        flushEntered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();

        // Act: the change starts while the checkpoint holds its lock.
        var change = Task.Run(() => storage.CommitDurability = StorageCommitDurability.None);
        var first = await Task.WhenAny(change, Task.Delay(TimeSpan.FromMilliseconds(500)));
        bool changedDuringCheckpoint = first == change;
        releaseFlush.Set();
        await checkpoint;
        await change;

        // Assert: the change waited, and the checkpoint record was flushed durably.
        changedDuringCheckpoint.ShouldBeFalse();
        storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);
    }

    /// <summary>
    /// PostgreSQL turns <c>fsync</c> back on only after "all modified buffers in the kernel" reach
    /// durable storage (<c>doc/src/sgml/config.sgml:3360-3365</c>). Nothing an initialized storage
    /// wrote under <c>None</c> is durable or ordered on the media, so it refuses a durable mode
    /// until it is reopened with one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: an initialized storage never leaves None for a durable mode")]
    public void CommitDurability_LeavingNoneAfterInitialization_ShouldThrowAndKeepNone()
    {
        // Arrange: None chosen before initialization, on handles that could flush durably.
        using var storage = SettingStorage.Create(StorageCommitDurability.None);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => storage.CommitDurability = StorageCommitDurability.Synchronous).Message.ShouldContain("Reopen it");
        Should.Throw<InvalidOperationException>(() => storage.CommitDurability = StorageCommitDurability.Grouped);
        Should.Throw<InvalidOperationException>(() => storage.ConfigureCommitDurability(StorageCommitDurability.Synchronous, "none-store"));
        storage.CommitDurability = StorageCommitDurability.None;
        storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: before initialization any change, and after it any change but leaving None, is allowed")]
    public void CommitDurability_AllowedChanges_ShouldApply()
    {
        // Arrange: not initialized yet, as an engine's ConfigureCommitDurability finds it.
        using var storage = SettingStorage.Create(StorageCommitDurability.None, initialize: false);

        // Act / Assert
        storage.ConfigureCommitDurability(StorageCommitDurability.Synchronous, "configured-once");
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Synchronous);
        storage.Initialize();

        foreach (var mode in new[] { StorageCommitDurability.Grouped, StorageCommitDurability.Synchronous, StorageCommitDurability.Grouped, StorageCommitDurability.None })
        {
            storage.CommitDurability = mode;
            storage.CommitDurability.ShouldBe(mode);
        }
    }

    private sealed class SettingStorage : Storage
    {
        private SettingStorage(HookedHandle data, HookedHandle journal)
            : base(StorageModel.Custom, new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()))
        {
            DataHandle = data;
            JournalHandle = journal;
        }

        public HookedHandle DataHandle { get; }

        public HookedHandle JournalHandle { get; }

        public StorageJournal Wal => WriteAheadLog;

        public static SettingStorage Create(StorageCommitDurability? durability = null, bool initialize = true)
        {
            var storage = new SettingStorage(new HookedHandle(), new HookedHandle());
            if (durability is { } configured)
            {
                storage.ConfigureCommitDurability(configured, "durability-setting");
            }

            if (initialize)
            {
                storage.Initialize();
            }

            return storage;
        }

        public void Initialize() => InitializeNew((Name)"durability-setting");

        public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, byte[] data) => InsertRecord(transaction, data);
    }

    /// <summary>
    /// A durable in-memory handle that counts durable flushes and runs a one-shot action before
    /// its next write or its next durable flush.
    /// </summary>
    private sealed class HookedHandle : IFileSystemFileHandle
    {
        private readonly SimulatedDurableFileHandle _inner = new();
        private int _durableFlushes;

        public Action? OnNextWrite { get; set; }

        public Action? OnNextDurableFlush { get; set; }

        public int DurableFlushes => Volatile.Read(ref _durableFlushes);

        public long Length => _inner.Length;

        public bool SupportsDurableFlush => true;

        public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, offset, cancellationToken);

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            if (OnNextWrite is { } hook)
            {
                OnNextWrite = null;
                hook();
            }

            _inner.Write(buffer, offset);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span, offset);
            return default;
        }

        public void SetLength(long length) => _inner.SetLength(length);

        public void Flush(bool durable)
        {
            if (durable)
            {
                if (OnNextDurableFlush is { } hook)
                {
                    OnNextDurableFlush = null;
                    hook();
                }

                Interlocked.Increment(ref _durableFlushes);
            }

            _inner.Flush(durable);
        }

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            Flush(durable);
            return default;
        }

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The <see cref="Storage.CommitDurability"/> and <see cref="Storage.GroupCommitWindow"/> setters
/// (owner decision 26 of 2026-10-06). An undefined mode or an out-of-range window is refused. A
/// change between the two durable modes is allowed while transactions are active and applies from
/// the next commit, as PostgreSQL's <c>synchronous_commit</c> does; a commit reads one value
/// throughout; a change waits for a running checkpoint, which reads one value throughout; and an
/// initialized storage never enters or leaves <see cref="StorageCommitDurability.None"/>, which is
/// PostgreSQL's <c>fsync = off</c>.
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
    /// durable mode commits under the durable mode set before its commit, and the change does not
    /// wait for it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a change between the durable modes while a transaction is active is allowed and applies at its commit")]
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

        // Arrange: a transaction begun under Synchronous, and a zero window, so a grouped commit
        // flushes itself as soon as it registers.
        storage.GroupCommitWindow = TimeSpan.Zero;
        using var synchronous = storage.BeginTransaction();
        storage.Insert(synchronous, [4, 5, 6]);

        // Act: Synchronous to Grouped while the transaction is active, then its commit.
        storage.CommitDurability = StorageCommitDurability.Grouped;
        synchronous.Commit();

        // Assert: it registered with the gate, and its record is durable.
        pending.ShouldBe(1);
        storage.Wal.DurableLsn.ShouldBeGreaterThanOrEqualTo(synchronous.CommitRecordLsn);
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
        // handle while the commit runs, and a hook on that write that switches to Grouped. A commit
        // that read the setting again would register with the gate and wait out its window.
        using var storage = SettingStorage.Create();
        storage.GroupCommitWindow = TimeSpan.FromSeconds(5);
        int pending = 0;
        storage.OnCommitPending = () => Interlocked.Increment(ref pending);
        storage.Wal.MaximumBufferBytes = 4096;
        using var transaction = storage.BeginTransaction();
        storage.Insert(transaction, Enumerable.Repeat((byte)0xAB, 6000).ToArray());
        storage.JournalHandle.OnNextWrite = () => storage.CommitDurability = StorageCommitDurability.Grouped;

        // Act
        transaction.Commit();

        // Assert: the change ran inside the commit, and the commit still flushed inline.
        storage.JournalHandle.OnNextWrite.ShouldBeNull();
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Grouped);
        pending.ShouldBe(0);
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
        var change = Task.Run(() => storage.CommitDurability = StorageCommitDurability.Grouped);
        var first = await Task.WhenAny(change, Task.Delay(TimeSpan.FromMilliseconds(500)));
        bool changedDuringCheckpoint = first == change;
        releaseFlush.Set();
        await checkpoint;
        await change;

        // Assert: the change waited, and the checkpoint record was flushed durably.
        changedDuringCheckpoint.ShouldBeFalse();
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Grouped);
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);
    }

    /// <summary>
    /// <c>None</c> is PostgreSQL's <c>fsync = off</c>, which only the configuration file changes
    /// (<c>doc/src/sgml/config.sgml:3375-3376</c>) and which is turned back on only after all
    /// modified buffers reach durable storage (<c>config.sgml:3360-3365</c>). A checkpoint under it
    /// truncates the journal without flushing the data file durably, so an initialized storage
    /// refuses a change into it or out of it until it is reopened with the new mode.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Durability setting: an initialized storage never enters or leaves None")]
    [InlineData(StorageCommitDurability.None, StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.None, StorageCommitDurability.Grouped)]
    [InlineData(StorageCommitDurability.Synchronous, StorageCommitDurability.None)]
    [InlineData(StorageCommitDurability.Grouped, StorageCommitDurability.None)]
    public void CommitDurability_EnteringOrLeavingNoneAfterInitialization_ShouldThrowAndKeepTheSetting(
        StorageCommitDurability initial, StorageCommitDurability requested)
    {
        // Arrange: the mode chosen before initialization, on handles that can flush durably.
        using var storage = SettingStorage.Create(initial);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => storage.CommitDurability = requested).Message.ShouldContain("Reopen it");
        Should.Throw<InvalidOperationException>(() => storage.ConfigureCommitDurability(requested, "fixed-none"));
        storage.CommitDurability.ShouldBe(initial);

        // The current value may be set again, as the Sql and KeyValuePair engines do.
        storage.ConfigureCommitDurability(initial, "fixed-none");
        storage.CommitDurability.ShouldBe(initial);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: before initialization any change, and after it any change between the durable modes, is allowed")]
    public void CommitDurability_AllowedChanges_ShouldApply()
    {
        // Arrange: not initialized yet, as a model storage's Create finds it.
        using var storage = SettingStorage.Create(StorageCommitDurability.None, initialize: false);

        // Act / Assert
        storage.ConfigureCommitDurability(StorageCommitDurability.Synchronous, "configured-once");
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Synchronous);
        storage.Initialize();

        foreach (var mode in new[] { StorageCommitDurability.Grouped, StorageCommitDurability.Synchronous, StorageCommitDurability.Grouped })
        {
            storage.CommitDurability = mode;
            storage.CommitDurability.ShouldBe(mode);
        }

        // An unset choice resolves from the handles, as an engine's second configuration does.
        storage.ConfigureCommitDurability(null, "configured-again");
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Synchronous);
    }

    /// <summary>
    /// The race the refusal closes (kernel review of owner decision 26). A logical commit waits for
    /// its record in the group-commit gate, as <c>TransactionCoordinator</c> does after its statement
    /// brackets committed without waiting. Had the mode switched to <c>None</c>, a checkpoint would
    /// have truncated the journal without flushing the data file durably, and the waiter's own
    /// fsync would then have made that truncation durable while the pages were not: after a power
    /// loss both the logical commit and an earlier commit acknowledged under <c>Synchronous</c> were
    /// gone. Now the switch is refused, the checkpoint stays durable, and both commits survive.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Durability setting: a durable wait across a checkpoint keeps its commit, since the switch to None it would race is refused")]
    public async Task CommitDurability_SwitchToNoneWhileALogicalCommitWaits_ShouldBeRefusedAndKeepBothCommits()
    {
        // Arrange: a durable base on flush-gated media, and a commit acknowledged under Synchronous.
        using var storage = CrashStorage.CreateNew();
        storage.Checkpoint();
        using (var acknowledged = storage.BeginTransaction())
        {
            storage.Insert(acknowledged, "acknowledged");
            acknowledged.Commit();
        }

        // A statement bracket committed without its wait, then the logical commit's wait under
        // Grouped, on a thread of its own, with a window too long for the waiter to help itself.
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromSeconds(20);
        using var registered = new ManualResetEventSlim();
        storage.OnCommitPending = registered.Set;
        long lsn;
        using (var bracket = storage.BeginTransaction())
        {
            storage.Insert(bracket, "logical");
            bracket.Commit(awaitDurability: false);
            lsn = bracket.CommitRecordLsn;
        }

        var waiter = Task.Factory.StartNew(
            () => storage.EnsureCommitDurable(lsn), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        registered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("the logical commit did not register on the gate within 10 s");
        waiter.IsCompleted.ShouldBeFalse();

        // Act: the switch to None, then a checkpoint, while the commit waits.
        Should.Throw<InvalidOperationException>(() => storage.CommitDurability = StorageCommitDurability.None);
        storage.Checkpoint();
        await waiter.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the checkpoint stayed durable and released the waiter, and a power loss now keeps
        // both commits.
        storage.CommitDurability.ShouldBe(StorageCommitDurability.Grouped);
        using var reopened = CrashStorage.Open(storage.CaptureDurable());
        reopened.ScanText().ShouldBe(new[] { "acknowledged", "logical" }, ignoreOrder: true);
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
    /// A storage over flush-gated media on which only a durable flush survives a power loss, as an
    /// operating system's cache behaves: the flushes <c>None</c> issues survive none.
    /// </summary>
    private sealed class CrashStorage : Storage
    {
        private readonly CrashSimulationStream _data;
        private readonly CrashSimulationStream _journal;

        private CrashStorage(CrashSimulationStream data, CrashSimulationStream journal)
            : base(StorageModel.Custom, new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
        }

        public static CrashStorage CreateNew()
        {
            var storage = new CrashStorage(
                new CrashSimulationStream(writeThrough: false, name: "data") { DurableFlushesOnly = true },
                new CrashSimulationStream(writeThrough: false, name: "journal") { DurableFlushesOnly = true });
            storage.ConfigureCommitDurability(StorageCommitDurability.Synchronous, "crash-storage");
            storage.InitializeNew((Name)"crash-storage");
            return storage;
        }

        public static CrashStorage Open((byte[] Data, byte[] Journal) images)
        {
            var storage = new CrashStorage(
                new CrashSimulationStream(images.Data, writeThrough: true, name: "data"),
                new CrashSimulationStream(images.Journal, writeThrough: true, name: "journal"));
            storage.ConfigureCommitDurability(StorageCommitDurability.Synchronous, "crash-storage");
            storage.OpenExisting(checkpointOnOpen: false);
            return storage;
        }

        /// <summary>Gets what a power loss right now would leave on the media.</summary>
        public (byte[] Data, byte[] Journal) CaptureDurable() => (_data.CaptureDurable(), _journal.CaptureDurable());

        public void Insert(StorageTransaction transaction, string text) => InsertRecord(transaction, Encoding.UTF8.GetBytes(text));

        public List<string> ScanText()
        {
            var results = new List<string>();
            using var iterator = GetUnitIterator(0);
            while (iterator.MoveNext())
            {
                results.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
            }

            return results;
        }
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

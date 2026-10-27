using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class JournalConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Journal tail: in-flight drains resume at the last verified frame")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureDurable_TornTailAndConcurrentAppend_ShouldWriteAtVerifiedOffset(bool write)
    {
        // Arrange
        byte[] original;
        using (var seed = new MemoryStream())
        {
            using var journal = StorageJournal.Create(seed, leaveOpen: true);
            journal.AppendCommit(1);
            journal.Flush();
            seed.Write(new byte[] { 90, 91, 92, 93, 94, 95, 96, 97, 98 });
            original = seed.ToArray();
        }
        var medium = new CrashSimulationStream(original);
        var faults = new DeviceFaults();
        using var reopened = StorageJournal.Create(faults.WrapJournal(medium));
        long first = reopened.AppendCommit(2);
        Stall(faults, write);
        Task force = DedicatedTestTask.Run(() => reopened.EnsureDurable(first));
        try
        {
            WaitForStall(faults, write);

            // Act
            long later = reopened.AppendCommit(3);
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
            reopened.EnsureDurable(later);

            // Assert: no record sits behind the old torn tail, and offsets remain contiguous.
            using var crash = StorageJournal.Create(new MemoryStream(medium.CaptureDurable()));
            crash.ReadAll().Select(record => record.Lsn).ShouldBe([1L, first, later]);
            medium.Length.ShouldBe(3 * 38);
        }
        finally
        {
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal force: appenders proceed during device I/O and waiters share a durable prefix")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EnsureDurable_StalledDevice_ShouldReleaseAppendLockAndCoalesceWaiters(bool write, bool flush)
    {
        // Arrange
        var faults = new DeviceFaults();
        var medium = new CrashSimulationStream { DurableFlushesOnly = true };
        using var journal = StorageJournal.Create(faults.WrapJournal(medium));
        long first = journal.AppendCommit(1);
        Stall(faults, write);
        Task force = DedicatedTestTask.Run(() => { if (flush) { journal.Flush(true); } else { journal.EnsureDurable(first); } });
        Task[] waiters = [];

        try
        {
            WaitForStall(faults, write);

            // Act: the second commit is buffered while the first prefix is on the device.
            long second = await DedicatedTestTask.Run(() => journal.AppendCommit(2)).WaitAsync(TimeSpan.FromSeconds(10));
            waiters = Enumerable.Range(0, 8).Select(_ => DedicatedTestTask.Run(() => journal.EnsureDurable(first))).ToArray();

            // Assert: neither the in-flight prefix nor the later commit was acknowledged yet.
            second.ShouldBe(first + 1);
            journal.DurableLsn.ShouldBe(0);
            faults.Clear();
            await Task.WhenAll(waiters.Append(force)).WaitAsync(TimeSpan.FromSeconds(10));
            medium.FlushCount.ShouldBe(1);
            journal.DurableLsn.ShouldBe(first);
            using (var crash = StorageJournal.Create(new MemoryStream(medium.CaptureDurable())))
            {
                crash.ReadAll().Select(record => record.Lsn).ShouldBe([first]);
            }

            journal.EnsureDurable(second);
            using var reopened = StorageJournal.Create(new MemoryStream(medium.CaptureDurable()));
            reopened.ReadAll().Select(record => record.Lsn).ShouldBe([first, second]);
        }
        finally
        {
            faults.Clear();
            await Task.WhenAll(waiters.Append(force)).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal failure: latch before any later prefix can be acknowledged")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureDurable_FailedDeviceWithLaterAppends_ShouldFenceEveryLaterLsn(bool write)
    {
        // Arrange
        var faults = new DeviceFaults();
        var medium = new CrashSimulationStream { DurableFlushesOnly = true };
        using var journal = StorageJournal.Create(faults.WrapJournal(medium));
        long durable = journal.AppendCommit(1);
        journal.EnsureDurable(durable);
        byte[] payload = [1, 2, 3, 4];
        long first = journal.AppendPageRecord(2, (PageId)1L, JournalRecordType.FullPageImage, payload, out var location);
        Stall(faults, write);
        Task force = DedicatedTestTask.Run(() => Should.Throw<StorageOfflineException>(() => journal.EnsureDurable(first)));
        try
        {
            WaitForStall(faults, write);

            // Act: one frame in the detached prefix, another in the replacement append buffer.
            long later = journal.AppendPageRecord(3, (PageId)2L, JournalRecordType.FullPageImage, payload, out var buffered);
            var read = new byte[64];
            journal.ReadPageRecord(buffered, read).ShouldBe(payload.Length);
            read.AsSpan(0, payload.Length).ToArray().ShouldBe(payload);
            if (write)
            {
                await DedicatedTestTask.Run(() => journal.ReadPageRecord(location, read)).WaitAsync(TimeSpan.FromSeconds(10));
                read.AsSpan(0, payload.Length).ToArray().ShouldBe(payload);
                faults.FailJournalWrites = true;
            }
            else
            {
                faults.FailJournalFlushes = true;
            }
            Task laterForce = DedicatedTestTask.Run(() => Should.Throw<StorageOfflineException>(() => journal.EnsureDurable(later)));
            Release(faults, write);
            await Task.WhenAll(force, laterForce).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert: first error persists after the device heals; old durability stays usable.
            faults.Clear();
            journal.DurableLsn.ShouldBe(durable);
            faults.ActiveJournalFlushes.ShouldBe(0);
            faults.StalledJournalWrites.ShouldBe(0);
            faults.StalledJournalFlushes.ShouldBe(0);
            journal.EnsureDurable(durable);
            Should.Throw<StorageOfflineException>(() => journal.Flush(true));
            Should.Throw<StorageOfflineException>(() => journal.AppendCommit(4));
            journal.ReadPageRecord(location, read).ShouldBe(payload.Length);
            read.AsSpan(0, payload.Length).ToArray().ShouldBe(payload);
            journal.ReadPageRecord(buffered, read).ShouldBe(payload.Length);
            using var reopened = StorageJournal.Create(new MemoryStream(medium.CaptureDurable()));
            reopened.ReadAll().Select(record => record.Lsn).ShouldBe([durable]);
        }
        finally
        {
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal lifecycle: offline, checkpoint and disposal wait for an in-flight fsync")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Flush_RacingLifecycle_ShouldCompletePrefixBeforeExclusiveOperation(int operation)
    {
        // Arrange
        var faults = new DeviceFaults();
        var medium = new CrashSimulationStream();
        using var journal = StorageJournal.Create(faults.WrapJournal(medium), leaveOpen: true);
        long first = journal.AppendCommit(1);
        faults.StallJournalFlushes();
        Task force = DedicatedTestTask.Run(() => journal.EnsureDurable(first));
        Exception? error = null;
        Thread? exclusive = null;
        try
        {
            WaitForStall(faults, write: false);
            long later = journal.AppendCommit(2);
            exclusive = new Thread(() =>
            {
                try
                {
                    if (operation == 0)
                    {
                        journal.TakeOffline(StorageOfflineException.Create(StorageOfflineCause.DataFlush, "test device", new IOException("test")));
                    }
                    else if (operation == 1)
                    {
                        journal.Checkpoint([]);
                    }
                    else
                    {
                        journal.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            }) { IsBackground = true };

            // Act: observe the exclusive thread blocked on the medium, then release the fsync.
            exclusive.Start();
            SpinWait.SpinUntil(() => (exclusive.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            journal.IsOffline.ShouldBeFalse();
            medium.Length.ShouldBe(38);
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
            exclusive.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();

            // Assert
            error.ShouldBeNull();
            if (operation == 0)
            {
                journal.IsOffline.ShouldBeTrue();
                journal.DurableLsn.ShouldBe(first);
                journal.EnsureDurable(first);
                Should.Throw<StorageOfflineException>(() => journal.EnsureDurable(later));
            }
            else if (operation == 1)
            {
                var checkpoint = journal.ReadAll().ShouldHaveSingleItem();
                checkpoint.Lsn.ShouldBe(later + 1);
                checkpoint.Type.ShouldBe(JournalRecordType.Checkpoint);
            }
            else
            {
                using var reopened = StorageJournal.Create(new MemoryStream(medium.CaptureLive()));
                reopened.ReadAll().Select(record => record.Lsn).ShouldBe([first, later]);
                Should.Throw<ObjectDisposedException>(() => journal.AppendCommit(3));
            }
        }
        finally
        {
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
            if (exclusive is not null)
            {
                exclusive.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Written prefix: confirmation bypasses an unrelated stalled force")]
    public async Task EnsureWritten_AlreadyFlushedPrefix_ShouldBypassMediumLock()
    {
        // Arrange
        var faults = new DeviceFaults();
        using var journal = StorageJournal.Create(faults.WrapJournal(new CrashSimulationStream()));
        long first = journal.AppendCommit(1);
        journal.EnsureWritten(first);
        faults.StallJournalFlushes();
        Task force = DedicatedTestTask.Run(() => journal.Flush(true));
        try
        {
            WaitForStall(faults, write: false);

            // Act: no write followed the previous OS flush, so only the append lock is needed.
            await DedicatedTestTask.Run(() => journal.EnsureWritten(first)).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            faults.StalledJournalFlushes.ShouldBe(1);
        }
        finally
        {
            faults.Clear();
            await force.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal force stress: acknowledged prefixes survive concurrent appends and group flushes")]
    public async Task EnsureDurable_ConcurrentAppendersAndGroupGate_ShouldKeepEveryAcknowledgedCommit()
    {
        // Arrange: each round holds one identified force on the device until all four
        // appenders have completed their appends, or the bounded cleanup ceiling expires.
        const int rounds = 32;
        const int appenderCount = 4;
        var medium = new CrashSimulationStream { DurableFlushesOnly = true };
        var faults = new DeviceFaults();
        using var journal = StorageJournal.Create(faults.WrapJournal(medium), leaveOpen: true);
        var gate = new StorageGroupCommitGate();
        var acknowledged = new ConcurrentBag<long>();
        int overlapping = 0;
        int forces = 0;

        // Act
        for (int round = 0; round < rounds; round++)
        {
            using var pending = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            using var observed = new ManualResetEventSlim();
            using var appended = new CountdownEvent(appenderCount);
            int roundOverlap = 0;
            gate.CommitPending = pending.Set;
            faults.JournalFlushStalled = () =>
            {
                entered.Set();
                // The count is taken inside this force's device hook. Its gate remains
                // closed until observed, so neither this force nor a later one can finish.
                if (appended.Wait(TimeSpan.FromSeconds(10)))
                {
                    faults.StalledJournalFlushes.ShouldBe(1);
                    roundOverlap = appenderCount;
                    Interlocked.Add(ref overlapping, roundOverlap);
                }
                observed.Set();
            };
            long first = journal.AppendCommit(round * (appenderCount + 1) + 1);
            Task committer = DedicatedTestTask.Run(() =>
            {
                gate.AwaitDurable(first, TimeSpan.FromSeconds(30), journal);
                acknowledged.Add(first);
            });
            Task flusher = Task.CompletedTask;
            Task[] appenders = [];
            faults.StallJournalFlushes();
            try
            {
                pending.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
                flusher = DedicatedTestTask.Run(() =>
                {
                    if (gate.FlushPending(journal))
                    {
                        Interlocked.Increment(ref forces);
                    }
                });
                entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
                appenders = Enumerable.Range(1, appenderCount).Select(sequence => DedicatedTestTask.Run(() =>
                {
                    long lsn = journal.AppendCommit(sequence);
                    appended.Signal();
                    gate.AwaitDurable(lsn, TimeSpan.Zero, journal);
                    acknowledged.Add(lsn);
                })).ToArray();
                observed.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
            }
            finally
            {
                faults.ReleaseJournalFlushes();
                await Task.WhenAll(appenders.Append(flusher).Append(committer)).WaitAsync(TimeSpan.FromSeconds(40));
                faults.JournalFlushStalled = null;
            }
            roundOverlap.ShouldBe(appenderCount, $"round {round}: append completion must precede release of its force");
        }

        // Assert
        overlapping.ShouldBeGreaterThan(100);
        forces.ShouldBeGreaterThan(10);
        faults.ActiveJournalFlushes.ShouldBe(0);
        acknowledged.Count.ShouldBeGreaterThan(100);
        using var reopened = StorageJournal.Create(new MemoryStream(medium.CaptureDurable()));
        var survived = reopened.ReadAll().Select(record => record.Lsn).ToHashSet();
        acknowledged.ShouldAllBe(lsn => survived.Contains(lsn));
    }

    private static void Stall(DeviceFaults faults, bool write)
    {
        if (write) { faults.StallJournalWrites(); } else { faults.StallJournalFlushes(); }
    }

    private static void Release(DeviceFaults faults, bool write)
    {
        if (write) { faults.ReleaseJournalWrites(); } else { faults.ReleaseJournalFlushes(); }
    }

    private static void WaitForStall(DeviceFaults faults, bool write)
    {
        SpinWait.SpinUntil(() => (write ? faults.StalledJournalWrites : faults.StalledJournalFlushes) == 1,
            TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }
}

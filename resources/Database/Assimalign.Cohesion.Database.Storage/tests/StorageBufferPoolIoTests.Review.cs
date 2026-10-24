using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageBufferPoolIoTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Full flush: a read-pinned image is confirmed across a writer boundary")]
    public void FlushAll_ReadPinnedCopyOverlapsWriter_ShouldRetryTheWholeImage()
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(1);
        using var pin = pool.Pin((PageId)0L, stream);
        pin.Page.AsBodySpan().Fill(0x11);
        pin.MarkDirty();
        int copies = 0;
        pool.FlushCopyHalfway = () =>
        {
            if (++copies != 1) { return; }
            int epoch = pin.Entry.BeginWrite(new object());
            try { pin.Page.AsBodySpan().Fill(0x22); pin.MarkDirty(); }
            finally { pin.Entry.EndWrite(epoch); }
        };

        // Act
        pool.FlushAll(stream);
        pool.FlushCopyHalfway = null;

        // Assert
        byte[] stored = new byte[Page.Size];
        stream.ReadPage((PageId)0L, stored);
        copies.ShouldBeGreaterThanOrEqualTo(2);
        stored.AsSpan(Page.HeaderSize).ToArray().ShouldAllBe(value => value == 0x22);
        pin.IsDirty.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Pool rendezvous: repeat every interrupt and waiter ownership window")]
    public async Task Io_RepeatedReviewRendezvous_ShouldExerciseEveryWindow()
    {
        // Arrange
        var elapsed = Stopwatch.StartNew();
        var counts = new int[8];

        // Act: explicit rendezvous exercise the windows; time only sets a minimum.
        do
        {
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2));
            await Maintenance_InterruptedDrain_ShouldReopenAdmission(false); counts[0]++;
            await Maintenance_InterruptedDrain_ShouldReopenAdmission(true); counts[1]++;
            Io_InterruptedCleanupMonitorEntry_ShouldReleaseItsReservation("load"); counts[2]++;
            Io_InterruptedCleanupMonitorEntry_ShouldReleaseItsReservation("evict"); counts[3]++;
            Io_InterruptedCleanupMonitorEntry_ShouldReleaseItsReservation("resize"); counts[4]++;
            await Pin_UnrelatedFlightFailure_ShouldRetryItsOwnOperation("overwrite"); counts[5]++;
            await Pin_UnrelatedFlightFailure_ShouldRetryItsOwnOperation("interrupt"); counts[6]++;
            await Pin_UnrelatedFlightFailure_ShouldRetryItsOwnOperation("victim"); counts[7]++;
        }
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || counts[0] < 5);

        // Assert
        counts.ShouldAllBe(count => count >= 5);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Write-back: cached pins cannot enter during the image copy")]
    public void FlushSome_CopyRendezvous_ShouldReserveAgainstCachedWriters()
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(1);
        StorageBufferPool.BufferEntry entry;
        using (var pin = pool.Pin((PageId)0L, stream))
        {
            entry = pin.Entry;
            var page = pin.Page; page.Type = PageType.Data;
            page.AsBodySpan().Fill(0x11);
            pin.MarkDirty();
        }
        bool pinnedDuringCopy = false;
        pool.FlushCopyHalfway = () =>
        {
            // Probe the same atomic pin primitive in Debug, whose diagnostic check
            // otherwise enters the mapping monitor before returning a cached pin.
            if (!entry.TryPin()) { return; }
            entry.ReleasePin();
#if DEBUG
            using var writer = pool.Pin((PageId)0L, stream);
            writer.Page.AsBodySpan().Fill(0x22);
            writer.MarkDirty();
            pinnedDuringCopy = true;
#else
            DedicatedTestTask.Run(() =>
            {
                using var writer = pool.Pin((PageId)0L, stream);
                writer.Page.AsBodySpan().Fill(0x22);
                writer.MarkDirty();
                pinnedDuringCopy = true;
            }).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
#endif
        };

        // Act
        pool.FlushSome(stream, 1).ShouldBe(1);
        pool.FlushCopyHalfway = null;

        // Assert
        byte[] stored = new byte[Page.Size];
        stream.ReadPage((PageId)0L, stored);
        stored[Page.HeaderSize].ShouldBe(stored[^1], "the stored image must not combine halves from different writes");
        stored.AsSpan(Page.HeaderSize).ToArray().ShouldAllBe(value => value == 0x11);
        pinnedDuringCopy.ShouldBeFalse();
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Maintenance: interrupted drain reopens cold I/O admission")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_InterruptedDrain_ShouldReopenAdmission(bool dispose)
    {
        // Arrange
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(2);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        file.BeforeRead = _ => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException(); } };
        var read = DedicatedTestTask.Run(() => { using var pin = pool.Pin((PageId)0L, stream); });
        Exception? failure = null;
        var maintenance = new Thread(() =>
        {
            try { if (dispose) { pool.Dispose(); } else { pool.Resize(1, stream); } }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        bool closed;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            maintenance.Start();
            SpinWait.SpinUntil(() => MaintenanceActive(pool), TimeSpan.FromSeconds(30)).ShouldBeTrue();
            // Act
            maintenance.Interrupt();
            maintenance.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            closed = MaintenanceActive(pool);
        }
        finally
        {
            file.BeforeRead = null;
            resume.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(30));
            // Repair only the base's leaked gate so failing evidence cannot hang teardown.
            lock (MappingMonitor(pool)) { MaintenanceFlag(pool) = false; Monitor.PulseAll(MappingMonitor(pool)); }
        }

        // Assert
        failure.ShouldBeOfType<ThreadInterruptedException>();
        closed.ShouldBeFalse();
        using var next = await DedicatedTestTask.Run(() => pool.Pin((PageId)1L, stream)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Cleanup: interruption cannot leak a reservation or active I/O")]
    [InlineData("load")]
    [InlineData("evict")]
    [InlineData("resize")]
    public void Io_InterruptedCleanupMonitorEntry_ShouldReleaseItsReservation(string kind)
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(1);
        using (var pin = pool.Pin((PageId)0L, stream)) { pin.MarkDirty(); }
        using var cleanup = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var returning = new ManualResetEventSlim();
        pool.IoCleanupStarting = () =>
        {
            cleanup.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException(); }
            returning.Set();
        };
        if (kind != "resize") { pool.WriteGuard = () => throw new IOException("injected"); }
        Exception? failure = null;
        var actor = new Thread(() =>
        {
            try
            {
                if (kind == "load") { using var pin = pool.Pin((PageId)1L, stream); }
                else if (kind == "evict") { pool.Evict((PageId)0L, stream); }
                else { pool.Resize(2, stream); }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        actor.Start();
        int active;
        bool reserved, maintenance;
        try
        {
            cleanup.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            lock (MappingMonitor(pool))
            {
                resume.Set();
                returning.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                SpinWait.SpinUntil(() => (actor.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
                // Act: the cleanup lock is contended, not the I/O callback.
                actor.Interrupt();
            }
            actor.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            active = ActiveIo(pool);
            maintenance = MaintenanceActive(pool);
            reserved = !pool.TryGet((PageId)0L, out var pin);
            pin?.Dispose();
        }
        finally
        {
            resume.Set();
            pool.IoCleanupStarting = null;
            pool.WriteGuard = null;
            actor.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            lock (MappingMonitor(pool)) { ActiveIo(pool) = 0; MaintenanceFlag(pool) = false; Monitor.PulseAll(MappingMonitor(pool)); }
        }

        // Assert
        failure.ShouldBeOfType<ThreadInterruptedException>();
        active.ShouldBe(0);
        maintenance.ShouldBeFalse();
        reserved.ShouldBeFalse();
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Single flight: waiters retry failures belonging to another operation")]
    [InlineData("overwrite")]
    [InlineData("interrupt")]
    [InlineData("victim")]
    public async Task Pin_UnrelatedFlightFailure_ShouldRetryItsOwnOperation(string kind)
    {
        // Arrange
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(1);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Exception failure = kind == "interrupt" ? new ThreadInterruptedException() : kind == "overwrite"
            ? new StorageCorruptionException((PageId)1L, "injected") : new IOException("victim WAL");
        int calls = 0;
        void FailOnce()
        {
            if (Interlocked.Increment(ref calls) != 1) { return; }
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException(); }
            throw failure;
        }
        if (kind == "victim")
        {
            using (var pin = pool.Pin((PageId)0L, stream)) { var page = pin.Page; page.Lsn = 1; pin.MarkDirty(); }
            pool.WriteAheadGate = _ => FailOnce();
        }
        else { file.BeforeRead = _ => FailOnce(); }
        Exception? loaderFailure = null;
        var loader = DedicatedTestTask.Run(() =>
        {
            try { using var pin = pool.Pin((PageId)1L, stream); }
            catch (Exception exception) { loaderFailure = exception; }
        });
        Task? waiter = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            waiter = DedicatedTestTask.Run(() =>
            {
                using var pin = kind == "overwrite" ? pool.PinForOverwrite((PageId)1L, stream) : pool.Pin((PageId)1L, stream);
                pin.Id.ShouldBe((PageId)1L);
            });
            SpinWait.SpinUntil(() => pool.WriteBackWaits >= 1, TimeSpan.FromSeconds(30)).ShouldBeTrue();
        }
        finally { resume.Set(); await loader.WaitAsync(TimeSpan.FromSeconds(30)); }

        // Act / Assert
        await waiter.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(30));
        loaderFailure.ShouldBeSameAs(failure);
        pool.CheckInvariants();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeIo")]
    private static extern ref int ActiveIo(StorageBufferPool pool);
}

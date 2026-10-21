using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageCheckpointFlushTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Paced flush: an idle pass allocates no snapshot or write image (#1269)")]
    public void FlushSome_CleanPool_ShouldAllocateNothing()
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(128L * Page.Size);
        using var pool = new StorageBufferPool(128);
        for (long id = 0; id < 128; id++)
        {
            Initialize(pool, stream, id, (byte)id);
        }
        pool.FlushAll(stream);
        pool.FlushSome(stream, 16);
        long minimum = long.MaxValue;

        // Act: minimum of three warmed rounds, with no timing threshold.
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int pass = 0; pass < 100; pass++)
            {
                pool.FlushSome(stream, 16);
            }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert
        minimum.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Flush: cached pins progress while write-ahead, page writes and stream flushes are blocked (#1269)")]
    [InlineData("all", "wal")]
    [InlineData("all", "write")]
    [InlineData("all", "flush")]
    [InlineData("some", "write")]
    [InlineData("page", "write")]
    public async Task Flush_BlockedIo_ShouldAllowCachedPins(string operation, string stage)
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(4L * Page.Size);
        using var pool = new StorageBufferPool(4);
        Initialize(pool, stream, 0, 1);
        Initialize(pool, stream, 1, 2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Block()
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
        }

        if (stage == "wal")
        {
            pool.WriteAheadGate = _ => Block();
        }
        if (stage == "write")
        {
            file.BeforeWrite = Block;
        }
        if (stage == "flush")
        {
            file.BeforeFlush = Block;
        }

        var flushing = Task.Factory.StartNew(() =>
        {
            if (operation == "all")
            {
                pool.FlushAll(stream);
            }
            else if (operation == "some")
            {
                pool.FlushSome(stream, 1).ShouldBe(1);
            }
            else
            {
                pool.FlushPage((PageId)0L, stream);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Act: a completed pin while the I/O latch stays closed proves monitor independence.
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            await Task.Run(() =>
            {
                using var same = pool.Pin((PageId)0L, stream);
                pool.TryGet((PageId)1L, out var hot).ShouldBeTrue();
                using (hot)
                {
                    hot!.Page.AsBodySpan()[0].ShouldBe((byte)2);
                }
                same.Page.AsBodySpan()[0] = 3;
                same.MarkDirty();
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            file.BeforeWrite = null;
            file.BeforeFlush = null;
            pool.WriteAheadGate = null;
            release.Set();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert: marking dirty while an older image is in flight still owes another write.
        pool.FlushAll(stream);
        pool.Evict((PageId)0L, stream);
        using var reloaded = pool.Pin((PageId)0L, stream);
        reloaded.Page.AsBodySpan()[0].ShouldBe((byte)3);
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Flush: every failed write-back stage releases its frame and flush gate (#1269)")]
    [InlineData("guard")]
    [InlineData("audit")]
    [InlineData("wal")]
    [InlineData("write")]
    [InlineData("flush")]
    public void Flush_FailedStage_ShouldReleaseItsFrame(string stage)
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(Page.Size);
        using var pool = new StorageBufferPool(1);
        Initialize(pool, stream, 0, 7);
        static void Fail() => throw new IOException("Injected flush failure");
        if (stage == "guard")
        {
            pool.WriteGuard = Fail;
        }
        if (stage == "audit")
        {
            pool.WriteBackAudit = (_, _) => Fail();
        }
        if (stage == "wal")
        {
            pool.WriteAheadGate = _ => Fail();
        }
        if (stage == "write")
        {
            file.BeforeWrite = Fail;
        }
        if (stage == "flush")
        {
            file.BeforeFlush = Fail;
        }

        // Act
        Should.Throw<IOException>(() => pool.FlushAll(stream));
        pool.WriteGuard = null;
        pool.WriteBackAudit = null;
        pool.WriteAheadGate = null;
        file.BeforeWrite = null;
        file.BeforeFlush = null;

        // Assert: eviction cannot hang on a leaked I/O lease, nor lose dirty bytes.
        pool.Evict((PageId)0L, stream);
        using var reloaded = pool.Pin((PageId)0L, stream);
        reloaded.Page.AsBodySpan()[0].ShouldBe((byte)7);
        reloaded.Entry.WriteInProgress.ShouldBeFalse();
        pool.FlushAll(stream);
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: eviction of snapshot entries cannot write recycled buffers under old ids (#1269)")]
    public async Task FlushAll_SnapshotEntryRecycled_ShouldKeepPageIdentity()
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(3L * Page.Size);
        using var pool = new StorageBufferPool(2);
        Initialize(pool, stream, 0, 10);
        Initialize(pool, stream, 1, 20);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        file.BeforeWrite = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
            }
        };
        var flushing = Task.Run(() => pool.FlushAll(stream));

        // Act: remove a later snapshot entry while the first one's write is parked.
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            await Task.Run(() =>
            {
                pool.Evict((PageId)1L, stream);
                Initialize(pool, stream, 2, 30);
                using var first = pool.Pin((PageId)0L, stream);
                first.Entry.WriteInProgress.ShouldBeTrue();
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10));
            file.BeforeWrite = null;
        }

        // Assert
        pool.FlushAll(stream);
        using var verify = new StorageBufferPool(3);
        for (long id = 0; id < 3; id++)
        {
            using var page = verify.Pin((PageId)id, stream);
            page.Page.Id.ShouldBe(id);
            page.Page.AsBodySpan()[0].ShouldBe((byte)((id + 1) * 10));
        }
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: pins and eviction progress during parked I/O while resize waits (#1269)")]
    public async Task FlushAll_ConcurrentEviction_ShouldExercisePinsDuringIo()
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(12L * Page.Size);
        using var pool = new StorageBufferPool(6);
        for (long id = 0; id < 12; id++)
        {
            Initialize(pool, stream, id, (byte)id);
        }
        pool.FlushAll(stream);
        using (var page = pool.Pin((PageId)5L, stream))
        {
            page.MarkDirty();
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int writes = 0;
        file.BeforeWrite = () =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
            }
        };
        int hitsDuringIo = 0;
        int mutations = 0;
        var flushing = Task.Factory.StartNew(() => pool.FlushAll(stream), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? resizing = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            var resizeStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            resizing = Task.Factory.StartNew(() =>
            {
                resizeStarted.SetResult(Thread.CurrentThread);
                pool.Resize(7, stream);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Act: the worker has entered Resize and is blocked on the flush gate.
            await WaitForGateAsync(resizing, resizeStarted.Task);
            pool.Capacity.ShouldBe(6);
            await Task.Run(() =>
            {
                for (int i = 0; i < 32; i++)
                {
                    using var page = pool.Pin((PageId)5L, stream);
                    page.Entry.WriteInProgress.ShouldBeTrue();
                    page.Page.Id.ShouldBe(5);
                    hitsDuringIo++;
                }
                for (int i = 0; i < 100; i++)
                {
                    long id = i % 12;
                    using var page = pool.Pin((PageId)id, stream);
                    page.Page.AsBodySpan()[0] = (byte)id;
                    page.MarkDirty();
                    mutations++;
                }
                using var leased = pool.Pin((PageId)5L, stream);
                leased.Entry.WriteInProgress.ShouldBeTrue();
            }).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert: every pin and mutation completed while the first write stayed parked.
            release.IsSet.ShouldBeFalse();
            hitsDuringIo.ShouldBe(32);
            mutations.ShouldBe(100);
            writes.ShouldBeGreaterThan(1); // Dirty foreground evictions also ran.
            resizing.IsCompleted.ShouldBeFalse();
            pool.CheckInvariants();
        }
        finally
        {
            file.BeforeWrite = null;
            release.Set();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10));
            if (resizing is not null)
            {
                await resizing.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        pool.Capacity.ShouldBe(7);
        pool.FlushSome(stream, 2).ShouldBeGreaterThan(0);
        pool.FlushAll(stream);
        for (long id = 0; id < 12; id++)
        {
            using var page = pool.Pin((PageId)id, stream);
            page.Page.Id.ShouldBe(id);
            page.Page.AsBodySpan()[0].ShouldBe((byte)id);
        }
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: misses waiting for the same flushing frame reuse one loaded page (#1269)")]
    public async Task Pin_ConcurrentMissesDuringFlush_ShouldReuseLoadedFrame()
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(1);
        Initialize(pool, stream, 0, 9);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        file.BeforeWrite = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
        };
        var flushing = Task.Run(() => pool.FlushAll(stream));
        Task<StoragePageHandle>? first = null;
        Task<StoragePageHandle>? second = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            first = Task.Factory.StartNew(() => pool.Pin((PageId)1L, stream), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            second = Task.Factory.StartNew(() => pool.Pin((PageId)1L, stream), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Act: prove both misses reached the I/O lease, while hits still progress.
            SpinWait.SpinUntil(() => pool.WriteBackWaits >= 2, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            using var hit = pool.Pin((PageId)0L, stream);
            hit.Page.AsBodySpan()[0].ShouldBe((byte)9);
        }
        finally
        {
            file.BeforeWrite = null;
            release.Set();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10));
            if (first is not null && second is not null)
            {
                using var one = await first.WaitAsync(TimeSpan.FromSeconds(10));
                using var two = await second.WaitAsync(TimeSpan.FromSeconds(10));
                // Assert: neither waiter replaced the other's pinned entry.
                one.Entry.ShouldBeSameAs(two.Entry);
                one.PinCount.ShouldBe(2);
            }
        }
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: disposal drains the in-flight write and leaves outstanding handles valid (#1269)")]
    public async Task Dispose_DuringFlush_ShouldDrainIoBeforeClearingFrames()
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(Page.Size);
        using var pool = new StorageBufferPool(1);
        Initialize(pool, stream, 0, 11);
        using var handle = pool.Pin((PageId)0L, stream);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        file.BeforeWrite = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
        };
        var flushing = Task.Run(() => pool.FlushAll(stream));
        Task? closing = null;
        try
        {
            // Act
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            var disposeStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = Task.Factory.StartNew(() =>
            {
                disposeStarted.SetResult(Thread.CurrentThread);
                pool.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await WaitForGateAsync(closing, disposeStarted.Task);

            // Assert before releasing I/O, in Release as well as Debug: disposal keeps
            // the leased frame resident until the parked write has completed.
            pool.Count.ShouldBe(1);
            pool.TryGet((PageId)0L, out var resident).ShouldBeTrue();
            using (resident)
            {
                resident!.Entry.ShouldBeSameAs(handle.Entry);
                resident.Entry.WriteInProgress.ShouldBeTrue();
            }
            release.IsSet.ShouldBeFalse();
            closing.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.Set();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10));
            if (closing is not null)
            {
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // Assert: disposal completed after the write and cleared the resident frames.
        pool.Count.ShouldBe(0);
        handle.Page.AsBodySpan()[0].ShouldBe((byte)11);
        handle.Entry.WriteInProgress.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => pool.Pin((PageId)0L, stream));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - AllocatePage: pool exhaustion returns the reserved page to the allocator (#1269)")]
    public void AllocatePage_AllFramesPinned_ShouldReleaseReservation()
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(8);
        var map = new StorageFreeSpaceMap();
        var manager = new StoragePageManager(stream, pool, map);
        var pinned = new List<StoragePageHandle>();
        for (int i = 0; i < 8; i++)
        {
            pinned.Add(manager.AllocatePage(PageType.Data));
        }
        long allocated = manager.PageCount - manager.FreePageCount;
        allocated.ShouldBe(9);

        // Act
        Should.Throw<StorageIOException>(() => manager.AllocatePage(PageType.Data));

        // Assert
        (manager.PageCount - manager.FreePageCount).ShouldBe(allocated);
        manager.FreePageCount.ShouldBe(1);
        foreach (var page in pinned)
        {
            page.Dispose();
        }
        using var retry = manager.AllocatePage(PageType.Data);
        ((long)retry.Id).ShouldBe(9);
        manager.FreePageCount.ShouldBe(0);
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - AllocatePage: failure after pinning releases both frame and reservation (#1269)")]
    [InlineData("extend")]
    [InlineData("audit")]
    public void AllocatePage_InitializationFails_ShouldReleasePinAndReservation(string stage)
    {
        // Arrange
        using var file = new ObservedFileHandle();
        using var stream = new StorageStream(file);
        using var pool = new StorageBufferPool(1);
        var manager = new StoragePageManager(stream, pool, new StorageFreeSpaceMap());
        static void Fail() => throw new IOException("Injected allocation failure");
        if (stage == "extend")
        {
            file.BeforeSetLength = Fail;
        }
        else
        {
            manager.WroteOutsideJournal = _ => Fail();
        }

        // Act
        Should.Throw<IOException>(() => manager.AllocatePage(PageType.Data));
        file.BeforeSetLength = null;
        manager.WroteOutsideJournal = null;

        // Assert
        manager.FreePageCount.ShouldBe(1);
        pool.TryGet((PageId)1L, out var failed).ShouldBeTrue();
        using (failed)
        {
            failed!.PinCount.ShouldBe(1);
            failed.IsDirty.ShouldBeFalse();
            failed.Page.Type.ShouldBe(PageType.Free);
        }
        using var retry = manager.AllocatePage(PageType.Data);
        ((long)retry.Id).ShouldBe(1);
        retry.PinCount.ShouldBe(1);
        manager.FreePageCount.ShouldBe(0);
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - AllocatePage: a refusing audit preserves a resident free page's bytes and dirty version (#1269)")]
    public void AllocatePage_AuditFailsForResidentFreePage_ShouldPreserveBuffer()
    {
        // Arrange
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(1);
        var manager = new StoragePageManager(stream, pool, new StorageFreeSpaceMap());
        using (var page = pool.Pin((PageId)1L, stream))
        {
            var content = page.Page;
            content.Id = 1;
            content.Type = PageType.Reclaimed;
            page.Page.AsBodySpan()[0] = 17;
            page.MarkDirty();
        }
        manager.WroteOutsideJournal = _ => throw new IOException("Injected allocation audit failure");

        // Act
        Should.Throw<IOException>(() => manager.AllocatePage(PageType.Data));

        // Assert
        manager.FreePageCount.ShouldBe(1);
        pool.TryGet((PageId)1L, out var preserved).ShouldBeTrue();
        using (preserved)
        {
            preserved!.PinCount.ShouldBe(1);
            preserved.IsDirty.ShouldBeTrue();
            preserved.Page.Type.ShouldBe(PageType.Reclaimed);
            preserved.Page.AsBodySpan()[0].ShouldBe((byte)17);
        }
        pool.CheckInvariants();
    }

    private static async Task WaitForGateAsync(Task operation, Task<Thread> started)
    {
        var worker = await started.WaitAsync(TimeSpan.FromSeconds(10));
        // After the execution handshake the worker only calls the pool operation.
        // Observe its monitor wait, or its completion if the gate was removed.
        SpinWait.SpinUntil(() => operation.IsCompleted ||
            (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    private static void Initialize(StorageBufferPool pool, StorageStream stream, long id, byte value)
    {
        using var page = pool.Pin((PageId)id, stream);
        var content = page.Page;
        content.Id = id;
        content.Type = PageType.Data;
        content.Lsn = 1;
        page.Page.AsBodySpan()[0] = value;
        page.MarkDirty();
    }

    private sealed class ObservedFileHandle : IFileSystemFileHandle
    {
        private readonly SimulatedDurableFileHandle _inner = new();
        public Action? BeforeWrite;
        public Action? BeforeFlush;
        public Action? BeforeSetLength;
        public int ActiveWrites;
        public long Length => _inner.Length;
        public bool SupportsDurableFlush => true;
        public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, offset, cancellationToken);
        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            Interlocked.Increment(ref ActiveWrites);
            try
            {
                BeforeWrite?.Invoke();
                Thread.Yield();
                _inner.Write(buffer, offset);
            }
            finally { Interlocked.Decrement(ref ActiveWrites); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span, offset);
            return default;
        }
        public void SetLength(long length)
        {
            BeforeSetLength?.Invoke();
            _inner.SetLength(length);
        }
        public void Flush(bool durable)
        {
            BeforeFlush?.Invoke();
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

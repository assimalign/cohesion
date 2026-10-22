using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageBufferPoolIoTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: unrelated cached pins progress before blocked foreground I/O resumes (#1541)")]
    [InlineData("Read")]
    [InlineData("Length")]
    [InlineData("Write")]
    [InlineData("Grow")]
    [InlineData("StoredRead")]
    [InlineData("Wal")]
    [InlineData("Evict")]
    [InlineData("Resize")]
    public async Task Pin_UnrelatedForegroundIoIsBlocked_ShouldKeepTheMappingMonitorAndCachedPageAvailable(string operation)
    {
        // Arrange: page 0 stays pinned; page 1 is the only eviction candidate.
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(3L * Page.Size);
        using var pool = new StorageBufferPool(2);
        using (var victim = pool.Pin((PageId)1L, stream))
        {
            if (operation is "Write" or "Wal" or "Evict" or "Resize")
            {
                var page = victim.Page;
                page.Id = 1;
                page.Lsn = 1;
                page.Type = PageType.Data;
                victim.MarkDirty();
            }
        }
        using var hot = pool.Pin((PageId)0L, stream);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string blockedKind = operation switch
        {
            "Grow" => "SetLength",
            "Length" => "Length",
            "Write" or "Evict" or "Resize" => "Write",
            _ => "Read"
        };
        void Block()
        {
            entered.TrySetResult();
            // Thirty seconds is only a hang guard; the explicit resume controls the test.
            if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("I/O test hang guard."); }
        }
        if (operation == "Wal") { pool.WriteAheadGate = _ => Block(); }
        else { file.BeforeOperation = kind => { if (kind == blockedKind) { Block(); } }; }
        var cold = DedicatedTestTask.Run(() =>
        {
            switch (operation)
            {
                case "Grow": pool.EnsureLength(stream, 4L * Page.Size); break;
                case "StoredRead": pool.TryReadStored((PageId)2L, stream, new byte[Page.Size]).ShouldBeTrue(); break;
                case "Evict": pool.Evict((PageId)1L, stream); break;
                case "Resize": pool.Resize(1, stream); break;
                default: using (pool.Pin((PageId)2L, stream)) { } break;
            }
        });
        Task? cached = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // A blocked callback owns the old monitor deterministically, so baseline
            // failures need no short deadline or throughput ratio to identify the defect.
            object monitor = MappingMonitor(pool);
            bool available = Monitor.TryEnter(monitor);
            if (available) { Monitor.Exit(monitor); }
            available.ShouldBeTrue();
            if (operation == "Resize") { pool.Capacity.ShouldBe(2); pool.Count.ShouldBe(2); }

            // Act / Assert: real pins, lookup and unpins finish while the I/O remains blocked.
            cached = DedicatedTestTask.Run(() =>
            {
                using var pin = pool.Pin((PageId)0L, stream);
                pool.TryGet((PageId)0L, out var found).ShouldBeTrue();
                found.ShouldNotBeNull().Dispose();
            });
            await cached.WaitAsync(TimeSpan.FromSeconds(30));
            resume.IsSet.ShouldBeFalse();
        }
        finally
        {
            file.BeforeOperation = null;
            pool.WriteAheadGate = null;
            resume.Set();
            await Task.WhenAll(cached is null ? [cold] : [cold, cached]).WaitAsync(TimeSpan.FromSeconds(30));
        }
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: coalesced readers share one verified frame or the original fault (#1541)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pin_SameColdPage_ShouldCoalesceCompletionAndPreserveFailureIdentity(bool fail)
    {
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(3L * Page.Size);
        using var pool = new StorageBufferPool(3);
        using var hot = pool.Pin((PageId)0L, stream);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Injected cold read failure.");
        int reads = 0;
        file.BeforeRead = offset =>
        {
            if (offset != Page.Size) { return; }
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("I/O test hang guard."); }
            if (fail) { throw failure; }
        };
        var one = DedicatedTestTask.Run(Read);
        Task<StoragePageHandle?>? two = null, three = null;
        StoragePageHandle? first = null, second = null, third = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            two = DedicatedTestTask.Run(Read);
            three = DedicatedTestTask.Run(Read);
            SpinWait.SpinUntil(() => pool.WriteBackWaits >= 2, TimeSpan.FromSeconds(30)).ShouldBeTrue();
            pool.Count.ShouldBe(1); // the unfinished frame has not been published
            pool.TryGet((PageId)1L, out _).ShouldBeFalse();
            // A separate cold page can also finish before the first read resumes.
            using var independent = await DedicatedTestTask.Run(() => pool.Pin((PageId)2L, stream)).WaitAsync(TimeSpan.FromSeconds(30));
            independent.Id.ShouldBe((PageId)2L);
            resume.IsSet.ShouldBeFalse();
            pool.CheckInvariants();
        }
        finally
        {
            resume.Set();
            first = await one.WaitAsync(TimeSpan.FromSeconds(30));
            if (two is not null) { second = await two.WaitAsync(TimeSpan.FromSeconds(30)); }
            if (three is not null) { third = await three.WaitAsync(TimeSpan.FromSeconds(30)); }
            file.BeforeRead = null;
        }
        try
        {
            reads.ShouldBe(1);
            if (!fail)
            {
                first.ShouldNotBeNull(); second.ShouldNotBeNull(); third.ShouldNotBeNull();
                first.Entry.ShouldBeSameAs(second.Entry);
                first.Entry.ShouldBeSameAs(third.Entry);
                first.PinCount.ShouldBe(3);
            }
            else
            {
                pool.TryGet((PageId)1L, out _).ShouldBeFalse();
                using var retry = pool.Pin((PageId)1L, stream);
                retry.PinCount.ShouldBe(1);
            }
            pool.CheckInvariants();
        }
        finally { first?.Dispose(); second?.Dispose(); third?.Dispose(); }

        StoragePageHandle? Read()
        {
            if (!fail) { return pool.Pin((PageId)1L, stream); }
            Should.Throw<IOException>(() => pool.Pin((PageId)1L, stream)).ShouldBeSameAs(failure);
            return null;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: maintenance drains cold reads without publishing a smaller capacity early (#1541)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_DuringColdRead_ShouldDrainTheReservation(bool dispose)
    {
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(2);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        file.BeforeRead = _ =>
        {
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("I/O test hang guard."); }
        };
        var reading = DedicatedTestTask.Run(() => pool.Pin((PageId)1L, stream));
        Task? maintenance = null;
        StoragePageHandle? handle = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            maintenance = DedicatedTestTask.Run(() => { if (dispose) { pool.Dispose(); } else { pool.Resize(1, stream); } });
            SpinWait.SpinUntil(() => MaintenanceActive(pool), TimeSpan.FromSeconds(30)).ShouldBeTrue();
            pool.Count.ShouldBe(0);
            pool.Capacity.ShouldBe(2);
            maintenance.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            file.BeforeRead = null;
            resume.Set();
            handle = await reading.WaitAsync(TimeSpan.FromSeconds(30));
            if (maintenance is not null) { await maintenance.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        using (handle)
        {
            handle.PinCount.ShouldBe(1);
            handle.Page.AsBodySpan()[0].ShouldBe((byte)0);
            pool.Count.ShouldBe(dispose ? 0 : 1);
            if (dispose) { Should.Throw<ObjectDisposedException>(() => pool.Pin((PageId)0L, stream)); }
            else { pool.Capacity.ShouldBe(1); pool.CheckInvariants(); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: a write-back audit can read the prior physical image during resize (#1541)")]
    public void Resize_AuditReadsStoredImage_ShouldReenterItsOwnIoGate()
    {
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(2);
        for (long id = 0; id < 2; id++)
        {
            using var handle = pool.Pin((PageId)id, stream);
            var page = handle.Page; page.Id = id; page.Type = PageType.Data; page.Lsn = 1;
            handle.MarkDirty();
        }
        int audits = 0;
        pool.WriteBackAudit = (id, _) =>
        {
            pool.TryReadStored((PageId)id, stream, new byte[Page.Size]).ShouldBeTrue();
            audits++;
        };
        pool.Resize(1, stream);
        audits.ShouldBe(1);
        pool.Capacity.ShouldBe(1);
        pool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: failed foreground eviction preserves the dirty page and its original exception (#1541)")]
    [InlineData("guard")]
    [InlineData("audit")]
    [InlineData("wal")]
    [InlineData("write")]
    public void Pin_VictimWriteFails_ShouldKeepTheOriginalDirtyMapping(string stage)
    {
        var file = new BlockingHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(1);
        using (var handle = pool.Pin((PageId)0L, stream))
        {
            var page = handle.Page; page.Id = 0; page.Type = PageType.Data; page.Lsn = 1;
            page.AsBodySpan()[0] = 71;
            handle.MarkDirty();
        }
        var failure = new IOException("Injected foreground write failure.");
        void Fail() => throw failure;
        if (stage == "guard") { pool.WriteGuard = Fail; }
        if (stage == "audit") { pool.WriteBackAudit = (_, _) => Fail(); }
        if (stage == "wal") { pool.WriteAheadGate = _ => Fail(); }
        if (stage == "write") { file.BeforeOperation = kind => { if (kind == "Write") { Fail(); } }; }
        Should.Throw<IOException>(() => pool.Pin((PageId)1L, stream)).ShouldBeSameAs(failure);
        pool.WriteGuard = null; pool.WriteBackAudit = null; pool.WriteAheadGate = null; file.BeforeOperation = null;
        pool.Count.ShouldBe(1);
        pool.TryGet((PageId)1L, out _).ShouldBeFalse();
        using (var preserved = pool.Pin((PageId)0L, stream))
        {
            preserved.IsDirty.ShouldBeTrue();
            preserved.Page.AsBodySpan()[0].ShouldBe((byte)71);
            preserved.PinCount.ShouldBe(1);
            preserved.Entry.WriteInProgress.ShouldBeFalse();
        }
        using (var retry = pool.Pin((PageId)1L, stream)) { }
        using var reloaded = pool.Pin((PageId)0L, stream);
        reloaded.Page.AsBodySpan()[0].ShouldBe((byte)71);
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: a stale lookup cannot pin a recycled frame under its old identity (#1541)")]
    public void PinEntry_StaleLookupAfterReuse_ShouldUndoTheSpeculativePin()
    {
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(1);
        StorageBufferPool.BufferEntry stale;
        using (var first = pool.Pin((PageId)0L, stream)) { stale = first.Entry; }
        using var replacement = pool.Pin((PageId)1L, stream);
        replacement.Entry.ShouldBeSameAs(stale);
        PinEntry(pool, 0, stale).ShouldBeFalse();
        replacement.PinCount.ShouldBe(1);
        pool.TryGet((PageId)0L, out _).ShouldBeFalse();
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool I/O: frame reservation and cached pins exclude each other (#1541)")]
    public void FrameReservation_WithLivePin_ShouldFailUntilThatPinIsReleased()
    {
        using var stream = StorageStream.FromInMemory();
        using var pool = new StorageBufferPool(1);
        var handle = pool.Pin((PageId)0L, stream);
        var entry = handle.Entry;
        try
        {
            entry.TryReserve().ShouldBeFalse();
            handle.PinCount.ShouldBe(1);
            handle.Dispose();
            entry.TryReserve().ShouldBeTrue();
            entry.TryPin().ShouldBeFalse();
            entry.PinCount.ShouldBe(0);
            entry.AllowPins();
            entry.TryPin().ShouldBeTrue();
            entry.PinCount.ShouldBe(1);
            entry.ReleasePin().ShouldBeTrue();
        }
        finally { entry.AllowPins(); handle.Dispose(); }
        pool.CheckInvariants();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryPinEntry")]
    private static extern bool PinEntry(StorageBufferPool pool, long id, StorageBufferPool.BufferEntry entry);

    private static bool MaintenanceActive(StorageBufferPool pool)
    {
        lock (MappingMonitor(pool)) { return MaintenanceFlag(pool); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_maintenance")]
    private static extern ref bool MaintenanceFlag(StorageBufferPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_syncRoot")]
    private static extern ref object MappingMonitor(StorageBufferPool pool);

    private sealed class BlockingHandle : IFileSystemFileHandle
    {
        private readonly SimulatedDurableFileHandle _inner = new();
        internal Action<string>? BeforeOperation;
        internal Action<long>? BeforeRead;
        public bool SupportsDurableFlush => true;
        public long Length { get { BeforeOperation?.Invoke("Length"); return _inner.Length; } }
        public int Read(Span<byte> buffer, long offset)
        {
            BeforeOperation?.Invoke("Read");
            BeforeRead?.Invoke(offset);
            return _inner.Read(buffer, offset);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Read(buffer.Span, offset));
        }
        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            BeforeOperation?.Invoke("Write");
            _inner.Write(buffer, offset);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span, offset);
            return default;
        }
        public void SetLength(long length) { BeforeOperation?.Invoke("SetLength"); _inner.SetLength(length); }
        public void Flush(bool durable) { BeforeOperation?.Invoke("Flush"); _inner.Flush(durable); }
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

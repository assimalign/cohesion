using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class SqlHeapPool
{
    internal sealed class HashPoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode(), _chars = ArrayPool<char>.Shared.GetHashCode(),
            _objects = ArrayPool<object>.Shared.GetHashCode(), _ints = ArrayPool<int>.Shared.GetHashCode(), _values = ArrayPool<SqlValue>.Shared.GetHashCode();
        internal long Trimmed, Rented, Allocated, Dropped, Unknown;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Verbose); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId is not (1 or 2 or 4 or 6) || args.Payload is null || args.Payload.Count < 3) { return; }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = pool == _bytes ? 1 : pool == _chars ? 2 : pool == _objects ? IntPtr.Size : pool == _ints ? 4 : pool == _values ? Unsafe.SizeOf<SqlValue>() : 0;
            if (width == 0) { Interlocked.Increment(ref Unknown); return; }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 1) { Interlocked.Add(ref Rented, size); }
            if (args.EventId == 2) { Interlocked.Add(ref Allocated, size); }
            if (args.EventId == 4) { Interlocked.Add(ref Trimmed, size); }
            if (args.EventId == 6) { Interlocked.Add(ref Dropped, size); }
        }
    }

    internal static async Task QuiesceWindowPoolAsync(HashPoolEvents events)
    {
        // Shared buckets stay held by the lease. Other threads' free TLS images can
        // otherwise disappear during a later gen-2 collection. An untouched pooled
        // probe must be collected before sampling; its weak reference decides readiness.
        long trimmed = events.Trimmed;
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<WeakReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Keep this TLS owner alive until trimming, independently of pool-worker retirement.
            try { ready.SetResult(ReturnWindowPoolProbe()); release.Wait(); }
            catch (Exception failure) { ready.TrySetException(failure); }
        }) { IsBackground = true, Name = "window-pool-probe" };
        thread.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var probe = await ready.Task.WaitAsync(guard.Token);
            while (true)
            {
                LiveHeap.Read();
                if (!probe.IsAlive) { break; }
                await Task.Delay(100, guard.Token);
            }
            (events.Trimmed - trimmed).ShouldBeGreaterThanOrEqualTo(1048600);
        }
        finally { release.Set(); thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReturnWindowPoolProbe()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1048576);
        var weak = new WeakReference(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
        return weak;
    }

    private static List<byte[]> PinWindowPool(HashPoolEvents events)
    {
        // Hold pre-existing pooled page images across both baselines and every sample.
        // A new allocation proves that each relevant shared bucket has been drained.
        var held = new List<byte[]>();
        foreach (int size in new[] { 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 })
        {
            long allocated = events.Allocated;
            int rents = 0;
            do { held.Add(ArrayPool<byte>.Shared.Rent(size)); rents++; }
            while (events.Allocated == allocated && rents < 1024);
            events.Allocated.ShouldBeGreaterThan(allocated);
        }
        return held;
    }

    internal sealed class WindowPoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];
        internal void Drain(HashPoolEvents events) => _buffers.AddRange(PinWindowPool(events));
        public void Dispose()
        { foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); } _buffers.Clear(); }
    }
}

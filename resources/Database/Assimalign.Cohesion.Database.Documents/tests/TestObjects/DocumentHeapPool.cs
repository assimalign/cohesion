using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapPool
{
    internal sealed class PoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode();
        internal long Allocated, Trimmed;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Verbose); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId is not (2 or 4) || args.Payload is null || args.Payload.Count < 3
                || (int)args.Payload[2]! != _bytes) { return; }
            long size = (24L + (int)args.Payload[1]! + 7) & ~7L;
            if (args.EventId == 2) { Interlocked.Add(ref Allocated, size); }
            else { Interlocked.Add(ref Trimmed, size); }
        }
    }

    internal sealed class PoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];

        internal void Drain(PoolEvents events)
        {
            // JSON metadata for the 20 MiB document can occupy a 32 MiB bucket.
            for (int size = 8192; size <= 32 * 1024 * 1024; size *= 2)
            {
                long allocated = events.Allocated;
                int rents = 0;
                do { _buffers.Add(ArrayPool<byte>.Shared.Rent(size)); rents++; }
                while (events.Allocated == allocated && rents < 1024);
                events.Allocated.ShouldBeGreaterThan(allocated);
            }
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); }
            _buffers.Clear();
        }
    }

    internal static async Task QuiesceAsync(PoolEvents events, CancellationToken cancellationToken = default)
    {
        long trimmed = events.Trimmed;
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<WeakReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ready.SetResult(ReturnProbe()); release.Wait(); }
            catch (Exception failure) { ready.TrySetException(failure); }
        }) { IsBackground = true, Name = "document-pool-probe" };
        thread.Start();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(90));
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
    private static WeakReference ReturnProbe()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1048576);
        var weak = new WeakReference(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
        return weak;
    }
}

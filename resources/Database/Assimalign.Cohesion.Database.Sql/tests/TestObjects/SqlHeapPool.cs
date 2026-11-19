using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class SqlHeapPool
{
    internal sealed class HashPoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode(), _chars = ArrayPool<char>.Shared.GetHashCode(),
            _objects = ArrayPool<object>.Shared.GetHashCode(), _ints = ArrayPool<int>.Shared.GetHashCode(), _values = ArrayPool<SqlValue>.Shared.GetHashCode();
        internal long TrimEvents, UnsizedTrims;
        internal long Trimmed, Rented, Allocated, Dropped, Unknown;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Informational); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            // Count every pool and element type, before any payload sizing/filtering.
            if (args.EventId == 4) { Interlocked.Increment(ref TrimEvents); }
            if (args.EventId is not (1 or 2 or 4 or 6)) { return; }
            if (args.Payload is null || args.Payload.Count < 3)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = pool == _bytes ? Unsafe.SizeOf<byte>() : pool == _chars ? Unsafe.SizeOf<char>() : pool == _objects ? IntPtr.Size : pool == _ints ? Unsafe.SizeOf<int>() : pool == _values ? Unsafe.SizeOf<SqlValue>() : 0;
            if (width == 0)
            {
                Interlocked.Increment(ref Unknown);
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 1) { Interlocked.Add(ref Rented, size); }
            if (args.EventId == 2) { Interlocked.Add(ref Allocated, size); }
            if (args.EventId == 4) { Interlocked.Add(ref Trimmed, size); }
            if (args.EventId == 6) { Interlocked.Add(ref Dropped, size); }
        }
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

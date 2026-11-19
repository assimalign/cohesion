using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapPool
{
    internal sealed class PoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode(), _chars = ArrayPool<char>.Shared.GetHashCode(),
            _ints = ArrayPool<int>.Shared.GetHashCode(), _objects = ArrayPool<object>.Shared.GetHashCode();
        internal long TrimEvents, Trimmed, UnsizedTrims;
        internal long Allocated;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Informational); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            // Count every pool and element type, before any payload sizing/filtering.
            if (args.EventId == 4) { Interlocked.Increment(ref TrimEvents); }
            if (args.EventId is not (2 or 4)) { return; }
            if (args.Payload is null || args.Payload.Count < 3)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = pool == _bytes ? Unsafe.SizeOf<byte>() : pool == _chars ? Unsafe.SizeOf<char>() :
                pool == _ints ? Unsafe.SizeOf<int>() : pool == _objects ? IntPtr.Size : 0;
            if (width == 0)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 2)
            {
                if (pool == _bytes) { Interlocked.Add(ref Allocated, size); }
            }
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
}

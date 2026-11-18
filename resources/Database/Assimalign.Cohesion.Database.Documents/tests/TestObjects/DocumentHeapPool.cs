using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;

using Shouldly;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapPool
{
    internal sealed class PoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode();
        internal long TrimEvents;
        internal long Allocated;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Verbose); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            // Count every pool and element type, before any payload sizing/filtering.
            if (args.EventId == 4) { Interlocked.Increment(ref TrimEvents); }
            if (args.EventId != 2 || args.Payload is null || args.Payload.Count < 3
                || (int)args.Payload[2]! != _bytes) { return; }
            long size = (24L + (int)args.Payload[1]! + 7) & ~7L;
            Interlocked.Add(ref Allocated, size);
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

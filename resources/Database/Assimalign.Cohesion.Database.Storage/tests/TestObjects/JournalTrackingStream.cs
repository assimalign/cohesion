using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>Exact cumulative work at a test journal's write and truncation points.</summary>
internal readonly record struct JournalCounters(long Written, int Truncations, long Largest);

/// <summary>A test journal whose complete checkpoint cycles remain observable.</summary>
internal class JournalTrackingStream : MemoryStream
{
    private readonly object _counterGate = new();
    private JournalCounters _counters;

    internal JournalCounters Counters { get { lock (_counterGate) { return _counters; } } }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_counterGate)
        {
            long previous = Length;
            base.Write(buffer, offset, count);
            _counters = _counters with
            {
                Written = _counters.Written + Math.Max(0, Length - previous),
                Largest = Math.Max(_counters.Largest, Length),
            };
        }
    }

    // MemoryStream's derived-type span path calls the array overload; funnel explicitly so
    // all overloads count once, including the fault-injecting subclasses' successful writes.
    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);
    public override void WriteByte(byte value) => Write([value], 0, 1);

    public override void SetLength(long value)
    {
        lock (_counterGate)
        {
            long previous = Length;
            base.SetLength(value);
            _counters = _counters with
            {
                Truncations = _counters.Truncations + (value < previous ? 1 : 0),
                Largest = Math.Max(_counters.Largest, value),
            };
        }
    }
}

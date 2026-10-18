using System;
using System.Buffers;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

// JsonDocument borrows this memory. Arm after parsing to cancel from element reads,
// on the executing thread, rather than racing a timer against statement startup.
internal sealed class CountingJsonMemory : MemoryManager<byte>
{
    private readonly byte[] _bytes;
    private readonly CancellationTokenSource _cancellation;
    private int _cancelAt;

    internal CountingJsonMemory(string json, CancellationTokenSource cancellation)
    {
        _bytes = Encoding.UTF8.GetBytes(json);
        _cancellation = cancellation;
    }

    internal int Reads { get; private set; }

    internal void Arm(int cancelAt)
    {
        Reads = 0;
        _cancelAt = cancelAt;
    }

    public override Span<byte> GetSpan()
    {
        if (_cancelAt > 0 && ++Reads == _cancelAt) { _cancellation.Cancel(); }
        return _bytes;
    }

    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}

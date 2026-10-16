using System;
using System.Buffers;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class NonArrayMemoryManager : MemoryManager<byte>
{
    private readonly byte[] _bytes;
    internal NonArrayMemoryManager(byte[] bytes) => _bytes = bytes;
    public override Span<byte> GetSpan() => _bytes;
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}

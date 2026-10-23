using System;
using System.Buffers;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Utf8JsonWriter can request six bytes per UTF-16 character before it knows the
// actual escape count. Allow that bounded scratch space, but never retain more
// than one frame of committed JSON, even when a projection repeats large values.
internal sealed class DocumentJsonBuffer : IBufferWriter<byte>
{
    private const int maximumLength = (int)ProtocolFrameHeader.MaxPayloadLength;
    private const int maximumCapacity = maximumLength * 6 + 4096;
    private byte[] _buffer = [];
    private int _written;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    internal void Clear() => _written = 0;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > maximumLength - _written) { throw DatabaseException.CreateDocumentsFailure("CD05016", ErrorMessages.ResultTooLarge); }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint > maximumCapacity - _written) { throw DatabaseException.CreateDocumentsFailure("CD05016", ErrorMessages.ResultTooLarge); }
        int required = _written + sizeHint;
        if (required > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Min(maximumCapacity, Math.Max(required, Math.Max(256, _buffer.Length * 2))));
        }
    }
}

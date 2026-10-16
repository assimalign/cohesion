using System;
using System.Buffers;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

// Every append checks before growth; scratch storage is returned even on malformed input.
internal sealed class PdfBuffer : IDisposable
{
    private byte[] _bytes;
    private readonly int _limit;
    private readonly long _position;
    internal int Count { get; private set; }
    internal PdfBuffer(int limit, long position)
    {
        _limit = limit; _position = position;
        _bytes = ArrayPool<byte>.Shared.Rent(Math.Min(256, limit));
    }
    internal void Add(byte value)
    {
        Ensure(1); _bytes[Count++] = value;
    }
    internal void Add(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length); bytes.CopyTo(_bytes.AsSpan(Count)); Count += bytes.Length;
    }
    private void Ensure(int count)
    {
        if (count > _limit - Count)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, _position);
        }
        if (count <= _bytes.Length - Count) { return; }
        var capacity = (int)Math.Min(_limit, Math.Max((long)Count + count, (long)_bytes.Length * 2));
        var next = ArrayPool<byte>.Shared.Rent(capacity);
        _bytes.AsSpan(0, Count).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_bytes);
        _bytes = next;
    }
    internal byte[] ToArray() => _bytes.AsSpan(0, Count).ToArray();
    public void Dispose() => ArrayPool<byte>.Shared.Return(_bytes);
}

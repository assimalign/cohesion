using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

internal sealed class BmffBufferStream : MemoryStream
{
    private readonly int _limit;
    internal BmffBufferStream(int limit) { _limit = limit; }

    private void Check(long length)
    {
        if (length > _limit)
        {
            throw new ContentFormatException(ErrorMessages.BufferRequired, ContentFormatErrorCode.LimitExceeded, Position);
        }
        if (length > Capacity)
        {
            Capacity = (int)Math.Min(_limit, Math.Max(length, Math.Max(256L, 2L * Capacity)));
        }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(Position + count);
        base.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(Position + buffer.Length);
        base.Write(buffer);
    }
    public override void WriteByte(byte value)
    {
        Check(Position + 1);
        base.WriteByte(value);
    }
    public override void SetLength(long value)
    {
        Check(value);
        base.SetLength(value);
    }
}

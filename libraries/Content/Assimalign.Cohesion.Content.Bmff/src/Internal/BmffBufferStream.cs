using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

[global::System.Diagnostics.DebuggerDisplay("BmffBufferStream {Length,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly BmffBufferStream _target;

        public DebugView(BmffBufferStream value)
        {
            _target = value;
        }

        public bool CanRead => _target.CanRead;
        public bool CanSeek => _target.CanSeek;
        public bool CanWrite => _target.CanWrite;
        public int Capacity => _target.Capacity;
        public long Length => _target.Length;
        public long Position => _target.Position;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}

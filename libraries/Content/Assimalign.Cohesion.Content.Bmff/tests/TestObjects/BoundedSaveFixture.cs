using System;
using System.Buffers.Binary;
using System.IO;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Original SparseMediaStream framing, with configurable zero payloads that saves may read.
internal static class BoundedSaveFixture
{
    internal static SparseSource CreateSource(long payloadLength)
    {
        using var original = new SparseMediaStream();
        var header = new byte[16];
        original.ReadExactly(header);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)payloadLength + 16);
        original.Position = 16 + SparseMediaStream.MediaSize;
        var tail = new byte[8];
        original.ReadExactly(tail);
        return new SparseSource(header, payloadLength, tail);
    }

    // Reads real bounded chunks of implicit zeros; never short-circuits CopyTo or saves.
    internal sealed class SparseSource : Stream
    {
        private readonly byte[] _prefix;
        private readonly byte[] _suffix;
        private readonly long _suffixOffset;
        internal SparseSource(byte[] prefix, long payloadLength, byte[] suffix)
        {
            _prefix = prefix;
            _suffix = suffix;
            _suffixOffset = prefix.Length + payloadLength;
        }
        internal long BytesRead { get; private set; }
        internal int MaximumRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _suffixOffset + _suffix.Length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            var count = (int)Math.Min(buffer.Length, Math.Max(0, Length - Position));
            var destination = buffer[..count];
            destination.Clear();
            CopyRegion(_prefix, 0, destination);
            CopyRegion(_suffix, _suffixOffset, destination);
            Position += count;
            BytesRead += count;
            return count;
        }
        private void CopyRegion(byte[] bytes, long start, Span<byte> destination)
        {
            var from = Math.Max(start, Position);
            var to = Math.Min(start + bytes.Length, Position + destination.Length);
            if (to > from) { bytes.AsSpan((int)(from - start), (int)(to - from)).CopyTo(destination[(int)(from - Position)..]); }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Forward-only output: counts every byte and retains none, including single-byte writes.
    internal sealed class CountingSink : Stream
    {
        internal long BytesWritten { get; private set; }
        internal int MaximumWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
        }
        public override void WriteByte(byte value) { BytesWritten++; MaximumWrite = Math.Max(MaximumWrite, 1); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

using System;
using System.IO;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

// Writer-generated live Matroska metadata followed by an original, unlaced virtual block.
internal static class BoundedSaveFixture
{
    internal static SparseReadStream CreateSource(long payloadLength)
    {
        using var prefix = new MemoryStream();
        prefix.Write(MkvFixtures.Create(cues: false, live: true));
        MkvFixtures.Id(0xA3).Write(prefix);
        VInt.EncodeSize((ulong)payloadLength + 4).Write(prefix);
        prefix.Write(new byte[] { 0x81, 0, 0, 0x80 });
        return new SparseReadStream(prefix.ToArray(), prefix.Length + payloadLength);
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

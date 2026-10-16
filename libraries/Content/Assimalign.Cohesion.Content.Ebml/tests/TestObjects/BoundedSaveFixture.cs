using System;
using System.IO;

using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

// Same original RFC 8794 framing and virtual payload as EbmlComplianceTests.
internal static class BoundedSaveFixture
{
    internal static SparsePayloadStream CreateSource(long payloadLength)
    {
        using var prefix = new MemoryStream();
        prefix.Write(Rfc8794Corpus.Element(0x1A45DFA3, Rfc8794Corpus.Element(0x4282, "test"u8.ToArray())));
        var binarySize = VInt.EncodeSize((ulong)payloadLength);
        VInt.FromEncoded(0xA0).Write(prefix);
        VInt.EncodeSize((ulong)(payloadLength + 1 + binarySize.Length)).Write(prefix);
        VInt.FromEncoded(0xA7).Write(prefix);
        binarySize.Write(prefix);
        return new SparsePayloadStream(prefix.ToArray(), payloadLength);
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

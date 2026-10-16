using System;
using System.IO;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

// Writer-authored HEIC metadata, with the existing sparse idat fixture made readable for saves.
internal static class BoundedSaveFixture
{
    internal static PayloadSource CreateSource(long payloadLength)
    {
        using var seed = new MemoryStream(HeifFixtures.Create("heic"));
        using var tree = BmffDocument.Load(seed, registry: HeifBoxRegistry.Default);
        var meta = HeifFixtures.Find<MetaBox>(tree.Boxes);
        var iloc = HeifFixtures.Find<ItemLocationBox>(meta.Children);
        iloc.LengthSize = 8;
        iloc.Entries[0].ConstructionMethod = 1;
        iloc.Entries[0].Extents[0] = new() { Offset = 0, Length = (ulong)payloadLength };
        using var prefix = new MemoryStream();
        prefix.Write(HeifBoxTests.Write(tree.Boxes[0]));
        var idatHeader = BmffBoxHeader.Create((BmffBoxType)0x69646174, payloadLength);
        BmffBoxHeader.Create(BmffBoxType.Meta, meta.GetPayloadSize() + idatHeader.HeaderSize + payloadLength).Write(prefix);
        prefix.Write(new byte[4]);
        foreach (var child in meta.Children) { prefix.Write(HeifBoxTests.Write(child)); }
        idatHeader.Write(prefix);
        return new PayloadSource(prefix.ToArray(), payloadLength);
    }

    // HeifProbeStream rejects image reads. Delegate framing to it and supply zero bytes only
    // inside its declared idat range; keep counters 64-bit when more than Int32 bytes are read.
    internal sealed class PayloadSource : Stream
    {
        private readonly HeifProbeStream _framing;
        private readonly int _prefixLength;
        internal PayloadSource(byte[] prefix, long payloadLength)
        {
            _framing = new HeifProbeStream(prefix, payloadLength);
            _prefixLength = prefix.Length;
        }
        internal long BytesRead { get; private set; }
        internal int MaximumRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _framing.Length;
        public override long Position { get => _framing.Position; set => _framing.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            int count;
            if (Position < _prefixLength) { count = _framing.Read(buffer); }
            else
            {
                count = (int)Math.Min(buffer.Length, Math.Max(0, Length - Position));
                buffer[..count].Clear();
                Position += count;
            }
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => _framing.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _framing.Dispose(); } base.Dispose(disposing); }
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

using System;
using System.IO;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Same writer-authored metadata and virtual mdat as Mp4BoundaryTests; no external media.
internal static class BoundedSaveFixture
{
    internal static Mp4SparseStream CreateSource(long payloadLength)
    {
        using var prefix = new MemoryStream();
        prefix.Write(Mp4Fixtures.Write(Mp4Fixtures.Brand("mp42")));
        BmffBoxHeader.Create(BmffBoxType.MediaData, payloadLength).Write(prefix);
        var payloadEnd = prefix.Length + payloadLength;
        var track = Mp4MediaFixtures.Track(1, true, 0, count: 1, wide: true);
        Mp4MediaFixtures.Find<ChunkOffset64BitBox>(track).Entries = [(ulong)(payloadEnd - 3)];
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0), track);
        return new Mp4SparseStream(prefix.ToArray(), Mp4Fixtures.Write(movie), payloadEnd);
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

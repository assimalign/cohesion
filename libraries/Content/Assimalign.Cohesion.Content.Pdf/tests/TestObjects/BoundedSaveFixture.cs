using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

// Reuse SparsePdfStream's original four-object prefix. Its classic table cannot address a
// new /Info object beyond 9,999,999,999 bytes, so append an authored 64-bit xref stream instead.
internal static class BoundedSaveFixture
{
    internal static SparseSource CreateSource(long payloadLength)
    {
        using var original = new SparsePdfStream();
        var framing = new byte[1024];
        original.ReadExactly(framing);
        var prefixLength = framing.AsSpan().IndexOf("stream\n"u8) + 7;
        var prefixText = Encoding.ASCII.GetString(framing, 0, prefixLength).Replace(
            SparsePdfStream.PayloadLength.ToString(CultureInfo.InvariantCulture),
            payloadLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var prefix = Encoding.ASCII.GetBytes(prefixText);
        using var suffix = new MemoryStream();
        PdfFixtureBuilder.Write(suffix, "\nendstream\nendobj\n");
        var xref = prefix.Length + payloadLength + suffix.Length;
        PdfFixtureBuilder.Write(suffix,
            "5 0 obj\n<< /Type /XRef /Size 6 /Root 1 0 R /W [1 8 4] /Length 78 /ID [<0011> <2233>] >>\nstream\n");
        Span<byte> row = stackalloc byte[13];
        row.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(row[9..], 65535);
        suffix.Write(row);
        for (var number = 1; number <= 5; number++)
        {
            row.Clear();
            row[0] = 1;
            var offset = number == 5 ? xref : prefixText.IndexOf(number + " 0 obj\n", StringComparison.Ordinal);
            BinaryPrimitives.WriteUInt64BigEndian(row[1..], (ulong)offset);
            suffix.Write(row);
        }
        PdfFixtureBuilder.Write(suffix, FormattableString.Invariant($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n"));
        return new SparseSource(prefix, payloadLength, suffix.ToArray());
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

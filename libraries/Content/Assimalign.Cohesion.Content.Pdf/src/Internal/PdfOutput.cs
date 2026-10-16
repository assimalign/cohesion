using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

// Count relative offsets without seeking the destination. Never own the destination.
internal sealed class PdfOutput : Stream
{
    private readonly Stream _destination;
    private readonly CancellationToken _cancellationToken;
    private readonly long _limit;
    internal long Count { get; private set; }
    internal PdfOutput(Stream destination, CancellationToken cancellationToken = default, long limit = long.MaxValue)
    {
        _destination = destination; _cancellationToken = cancellationToken; _limit = limit;
    }
    internal void Text(string text)
    {
        Span<byte> bytes = stackalloc byte[512];
        while (!text.AsSpan().IsEmpty)
        {
            var count = Math.Min(text.Length, bytes.Length);
            var written = Encoding.ASCII.GetBytes(text.AsSpan(0, count), bytes);
            Write(bytes[..written]); text = text[count..];
        }
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length > _limit - Count)
        {
            throw new ContentFormatException(ErrorMessages.WriterLimit, ContentFormatErrorCode.LimitExceeded, Count);
        }
        _destination.Write(buffer); Count += buffer.Length;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) { Span<byte> one = stackalloc byte[1]; one[0] = value; Write(one); }
    public override void Flush() => _destination.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(ErrorMessages.OutputForwardOnly);
    public override long Position { get => Count; set => throw new NotSupportedException(ErrorMessages.OutputForwardOnly); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.OutputForwardOnly);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(ErrorMessages.OutputForwardOnly);
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.OutputForwardOnly);
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

// Count relative offsets without seeking the destination. Never own the destination.
[global::System.Diagnostics.DebuggerDisplay("PdfOutput")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfOutput : Stream
{
    private readonly Stream _destination;
    private readonly CancellationToken _cancellationToken;
    private readonly long _limit;
    private readonly IncrementalHash? _hash;
    internal long Count { get; private set; }
    internal PdfOutput(Stream destination, CancellationToken cancellationToken = default, long limit = long.MaxValue, IncrementalHash? hash = null)
    {
        _destination = destination; _cancellationToken = cancellationToken; _limit = limit; _hash = hash;
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
        _destination.Write(buffer); _hash?.AppendData(buffer); Count += buffer.Length;
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

    private sealed class DebugView
    {
        private readonly PdfOutput _target;

        public DebugView(PdfOutput value)
        {
            _target = value;
        }

        public bool CanRead => _target.CanRead;
        public bool CanSeek => _target.CanSeek;
        public bool CanWrite => _target.CanWrite;
        public long Position => _target.Position;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}

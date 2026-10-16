using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

internal sealed class PdfCopyProbeStream : Stream
{
    private readonly MemoryStream _source;
    private readonly long _payloadStart;
    private readonly long _payloadEnd;
    private bool _measure;
    internal long PayloadBytesRead { get; private set; }
    internal int MaxRead { get; private set; }
    internal PdfCopyProbeStream(byte[] bytes, int payloadStart, int payloadLength)
    {
        _source = new MemoryStream(bytes); _payloadStart = payloadStart; _payloadEnd = payloadStart + payloadLength;
    }
    internal void Measure() { _measure = true; PayloadBytesRead = 0; MaxRead = 0; }
    public override int Read(Span<byte> buffer)
    {
        var start = Position;
        if (_measure)
        {
            MaxRead = Math.Max(MaxRead, buffer.Length);
            if (buffer.Length > 131072) { throw new InvalidOperationException("Attempted whole-payload materialization."); }
        }
        var read = _source.Read(buffer);
        if (_measure) { PayloadBytesRead += Math.Max(0, Math.Min(start + read, _payloadEnd) - Math.Max(start, _payloadStart)); }
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { _source.Dispose(); } base.Dispose(disposing); }
}

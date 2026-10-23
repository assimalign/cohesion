using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

// A counted, bounded view lets Content.Binary own exact-read loops while EBML supplies
// absolute positions even when the original source cannot seek.
[global::System.Diagnostics.DebuggerDisplay("EbmlInput")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EbmlInput : Stream
{
    private readonly Stream _source;
    private long _position;

    internal EbmlInput(Stream source)
    {
        _source = source;
        _position = source is ContentSliceStream slice ? slice.SourceOffset + slice.Position
            : source.CanSeek || source is EbmlInput ? source.Position : 0;
    }

    internal long? End { get; set; }
    internal bool RequireData { get; set; }
    internal BytesRead? Observe { get; set; }
    internal bool SuppressObservation { get; set; }
    internal void Advance(long count) => _position += count;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(Span<byte> buffer)
    {
        if (End is long end)
        {
            buffer = buffer[..(int)Math.Min(buffer.Length, Math.Max(0, end - _position))];
        }

        var read = _source.Read(buffer);
        if (RequireData && read == 0 && buffer.Length != 0)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, _position);
        }
        if (read > long.MaxValue - _position)
        {
            throw new EbmlDataFormatException(ErrorMessages.LengthLimit, ContentFormatErrorCode.LimitExceeded, _position);
        }

        _position += read;
        if (!SuppressObservation)
        {
            Observe?.Invoke(buffer[..read]);
        }

        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (End is long end)
        {
            buffer = buffer[..(int)Math.Min(buffer.Length, Math.Max(0, end - _position))];
        }

        var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (RequireData && read == 0 && buffer.Length != 0)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, _position);
        }
        if (read > long.MaxValue - _position)
        {
            throw new EbmlDataFormatException(ErrorMessages.LengthLimit, ContentFormatErrorCode.LimitExceeded, _position);
        }

        _position += read;
        if (!SuppressObservation)
        {
            Observe?.Invoke(buffer.Span[..read]);
        }

        return read;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private sealed class DebugView
    {
        private readonly EbmlInput _target;

        public DebugView(EbmlInput value)
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

internal delegate void BytesRead(ReadOnlySpan<byte> bytes);

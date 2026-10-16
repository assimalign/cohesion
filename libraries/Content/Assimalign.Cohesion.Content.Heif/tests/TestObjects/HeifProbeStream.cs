using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Heif.Tests;

internal sealed class HeifProbeStream : Stream
{
    private readonly Stream? _stream;
    private readonly byte[]? _prefix;
    private readonly long _length;
    private readonly bool _seekable;
    private long _position;
    internal int MaximumRead { get; private set; }
    internal int BytesRead { get; private set; }
    internal HeifProbeStream(Stream stream, bool seekable) { _stream = stream; _seekable = seekable; }
    internal HeifProbeStream(byte[] prefix, long payloadLength) { _prefix = prefix; _length = prefix.Length + payloadLength; _seekable = true; }
    public override bool CanRead => _stream?.CanRead ?? true;
    public override bool CanSeek => _seekable;
    public override bool CanWrite => _stream?.CanWrite ?? false;
    public override long Length => !_seekable ? throw new NotSupportedException() : _stream?.Length ?? _length;
    public override long Position
    {
        get => !_seekable ? throw new NotSupportedException() : _stream?.Position ?? _position;
        set { if (!_seekable)
            {
                throw new NotSupportedException();
            }

            if (_stream is not null)
            {
                _stream.Position = value;
            }
            else
            {
                _position = value;
            }
        }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        MaximumRead = Math.Max(MaximumRead, buffer.Length);
        if (_stream is not null) { var count = _stream.Read(buffer); BytesRead += count; return count; }
        if (_position >= _length)
        {
            return 0;
        }

        if (_position >= _prefix!.Length)
        {
            throw new InvalidOperationException("Lazy image data was read.");
        }

        var length = Math.Min(buffer.Length, _prefix.Length - (int)_position);
        _prefix.AsSpan((int)_position, length).CopyTo(buffer);
        _position += length;
        BytesRead += length;
        return length;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!_seekable)
        {
            throw new NotSupportedException();
        }

        if (_stream is not null)
        {
            return _stream.Seek(offset, origin);
        }

        return _position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : Length) + offset;
    }
    public override void Write(byte[] buffer, int offset, int count) => _stream!.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _stream!.Write(buffer);
    public override void Flush() => _stream?.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
}

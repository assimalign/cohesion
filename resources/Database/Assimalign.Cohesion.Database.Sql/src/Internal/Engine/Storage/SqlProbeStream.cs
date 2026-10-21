using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Database.Sql.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Storage's recovery reader requires writable stream semantics. Writes and truncation
// belong to this private overlay; the original sidecar is held through a read-only handle.
internal sealed class SqlProbeStream : Stream
{
    private const int blockSize = 4096;
    private readonly FileStream _source;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private long _position;
    private long _length;
    private long _sourceLength;
    private bool _disposed;

    internal SqlProbeStream(string path)
    {
        _source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _length = _sourceLength = _source.Length;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
    public override long Position
    {
        get => _position;
        set { ObjectDisposedException.ThrowIf(_disposed, this); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
        if (_blocks.Count == 0)
        {
            ReadSource(_position, buffer[..count]);
            _position += count;
            return count;
        }
        int read = 0;
        while (read < count)
        {
            long block = _position / blockSize;
            int offset = (int)(_position % blockSize);
            int take = Math.Min(count - read, blockSize - offset);
            if (_blocks.TryGetValue(block, out var bytes)) { bytes.AsSpan(offset, take).CopyTo(buffer[read..]); }
            else { ReadSource(_position, buffer.Slice(read, take)); }
            _position += take;
            read += take;
        }
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            long block = _position / blockSize;
            int offset = (int)(_position % blockSize);
            int take = Math.Min(buffer.Length, blockSize - offset);
            if (!_blocks.TryGetValue(block, out var bytes))
            {
                bytes = new byte[blockSize];
                ReadSource(block * blockSize, bytes);
                _blocks.Add(block, bytes);
            }
            buffer[..take].CopyTo(bytes.AsSpan(offset));
            buffer = buffer[take..];
            _position += take;
            _length = Math.Max(_length, _position);
        }
    }

    private void ReadSource(long position, Span<byte> destination)
    {
        int count = (int)Math.Min(destination.Length, Math.Max(0, _sourceLength - position));
        if (count > 0)
        {
            _source.Position = position;
            _source.ReadExactly(destination[..count]);
        }
        destination[count..].Clear();
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value < _length)
        {
            _sourceLength = Math.Min(_sourceLength, value);
            foreach (long block in new List<long>(_blocks.Keys))
            {
                if (block * blockSize >= value) { _blocks.Remove(block); }
                else if (value - block * blockSize < blockSize) { _blocks[block].AsSpan((int)(value % blockSize)).Clear(); }
            }
        }
        _length = value;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = position;
        return position;
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _blocks.Clear();
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace System.IO;

using Assimalign.Cohesion.Internal;


// length: 30 - offset 15, limit 25
[DebuggerDisplay("length: {_stream.Length} [Range {Offset} .. {Offset + Length}]")]
public class OffsetStream : Stream
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _isReadOnly;

    private bool _isDisposed;
    private long _length;
    private long _offset;
    private long _position = 0;


    /// <summary>
    /// 
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="offset">The offset from the current position of the stream.</param>
    /// <param name="length">The length of bytes to limit reading and writing.</param>
    /// <param name="isReadOnly"></param>
    /// <param name="leaveOpen">Specifies whther to leave the underlying <paramref name="stream"/> open on dispose.</param>
    public OffsetStream(Stream stream, long offset = 0, long length = 0, bool isReadOnly = false, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _offset = offset;
        _length = length;
        _isReadOnly = isReadOnly;
    }

    #region Properties

    /// <inheritdoc/>
    public override bool CanRead => _stream.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => _stream.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => _stream.CanWrite && !IsReadOnly;

    /// <inheritdoc/>
    public override bool CanTimeout => _stream.CanTimeout;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            InvalidOperationException.ThrowIf(!_stream.CanSeek, "Seeking is not allowed.");
            InvalidOperationException.ThrowIf(value < 0 || value > _length, $"The position {value} cannot exceed boundary of {_offset + _length}");
            
            _stream.Position = (_offset + value);
            _position = value;
        }
    }

    /// <summary>
    /// Represents the starting stream position
    /// </summary>
    public long Offset => _offset;

    /// <summary>
    /// The remaining bytes left to read within the offset.
    /// </summary>
    public long Remaining => _length - (_stream.Position - Offset);

    /// <summary>
    /// Specifies whether the stream is ReadOnly.
    /// </summary>
    public bool IsReadOnly { get; }

    #endregion

    /// <summary>
    /// Flushes the underlying stream.
    /// </summary>
    public override void Flush()
    {
        AssertReadOnly();

        _stream.Flush();
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        AssertReadOnly();

        return _stream.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckOrAdjustPosition();

        if (count < 1)
        {
            return 0;
        }

        InvalidOperationException.ThrowIf(count > Remaining, "The count exceeds the remaining readable bytes.");

        int bytesRead = _stream.Read(buffer, offset, count);

        _position =+ bytesRead;

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        CheckOrAdjustPosition();

        if (count < 1)
        {
            return 0;
        }

        InvalidOperationException.ThrowIf(count > Remaining, "The count exceeds the remaining readable bytes.");

        Memory<byte> memory = buffer.AsMemory(offset, count);

        int bytesRead = await _stream.ReadAsync(memory, cancellationToken);

        _position =+ bytesRead;

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckOrAdjustPosition();

        InvalidOperationException.ThrowIf(!_stream.CanSeek, "Stream is not seekable.");

        var boundary = origin switch
        {
            SeekOrigin.Begin => _offset + offset,
            SeekOrigin.Current => _stream.Position + offset,
            SeekOrigin.End => (_offset + _length) + offset,
            _ => throw new ArgumentException("Invalid Seek Origin")
        };

        InvalidOperationException.ThrowIf(boundary < _offset || boundary > (_offset + _length), "The offset exceeds the boundary of the stream.");

        _stream.Position = boundary;
        _position =+ (boundary - _offset);

        return boundary;
    }

    /// <summary>
    /// Adjust the offset of the stream.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="NotImplementedException"></exception>
    public void SetOffset(long value)
    {
        AssertReadOnly();

        throw new NotImplementedException();
    }

    /// <summary>
    /// Set length will readjust the limit
    /// </summary>
    /// <param name="value"></param>
    public override void SetLength(long value)
    {
        AssertReadOnly();

        // If greater than the underlying length of the stream we 
        // need to adjust the underlying stream length and the 
        if (value > Length)
        {
            _stream.SetLength(value);
        }

        _length = value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        AssertReadOnly();
        //
        //		if (count < 1)
        //			return;
        //
        //		if (_stream.CanSeek)
        //			_stream.Position = _offset + _position;
        //
        //		_stream.Write(buffer, offset, count);
        //		_position += count;
        //
        //		if (_position > _length)
        //			_length = _position;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        AssertReadOnly();

        if (count < 1)
        {
            return;
        }

        //		if (_stream.CanSeek)
        //			_stream.Position = _offset + _position;
        //
        //		await _stream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        //		_position += count;
        //
        //		if (_position > _length)
        //			_length = _position;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        AssertReadOnly();

        if (buffer.Length < 1)
        {
            return;
        }
        //
        //		if (_stream.CanSeek)
        //			_stream.Position = _offset + _position;
        //
        //		await _stream.WriteAsync(buffer, cancellationToken);
        //		_position += buffer.Length;
        //
        //		if (_position > _length)
        //			_length = _position;
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(OffsetStream));
        }

        if (disposing && !_leaveOpen && _stream is not null)
        {
            _stream?.Dispose();
        }

        _isDisposed = true;

        base.Dispose(disposing);
    }

    private void AssertReadOnly()
    {
        InvalidOperationException.ThrowIf(IsReadOnly, "The stream is ReadOnly.");
    }

    private void CheckOrAdjustPosition()
    {
        if (_stream.Position < _offset)
        {
            _stream.Position = _offset;
        }
    }
}

using System;
using System.IO;

namespace Assimalign.Cohesion.Files.Bmff;


/// <summary>
/// Represents a constrained stream.
/// </summary>
public sealed class BmffStream : Stream
{
    private readonly Stream _stream;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    public BmffStream(Stream stream, long offset, long limit)
    {
        this._stream = stream;
        this.Offset = offset;
        this.Limit = limit;
    }

    public BmffBoxType BoxType { get; set; }

    /// <summary>
    /// Represents the starting stream position
    /// </summary>
    public long Offset { get; }
    /// <summary>
    /// The allowed limit of bytes that can be read and written to the stream.
    /// </summary>
    public long Limit { get; }
    /// <summary>
    /// 
    /// </summary>
    public long Remaining =>  Limit - (Position - Offset);

    /// <inheritdoc />
    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => _stream.CanSeek;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => _stream.Length;
    public override long Position
    {
        get => _stream.Position;
        set
        {
            if (value > Remaining)
            {
                throw new ArgumentOutOfRangeException("");
            }

            _stream.Position = value;
        }
    }

    public override void Flush()
    {
        _stream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Let's check to see if the user is reading outside of the BMFF Box Constraints
        if (Position + offset + count > Offset + Limit)
        {
            throw new ArgumentOutOfRangeException();
        }

        return _stream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Position + offset + count > Offset + Limit)
        {
            throw new ArgumentOutOfRangeException();
        }

        _stream.Write(buffer, offset, count);
    }
}

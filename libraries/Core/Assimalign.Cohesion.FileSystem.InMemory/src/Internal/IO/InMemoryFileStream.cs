using System;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileStream : Stream
{
    private readonly InMemoryFileSystemFile _file;
    private readonly bool _canRead;
    private readonly bool _canWrite;
    private readonly bool _appendOnly;
    private readonly Action _onDispose;
    private int _isDisposed;
    private long _position;

    public InMemoryFileStream(
        InMemoryFileSystemFile file,
        bool canRead,
        bool canWrite,
        bool appendOnly,
        Action onDispose)
    {
        _file = file;
        _canWrite = canWrite;
        _canRead = canRead;
        _appendOnly = appendOnly;
        _onDispose = onDispose;
        _position = 0;

        if (_appendOnly)
        {
            _position = _file.Content.Length;
        }

        Debug.Assert(onDispose is not null);
    }

    ~InMemoryFileStream()
    {
        Dispose(false);
    }

    public override bool CanRead => _isDisposed == 0 && _canRead;

    public override bool CanSeek => _isDisposed == 0;

    public override bool CanWrite => _isDisposed == 0 && _canWrite;

    public override long Length
    {
        get
        {
            AssertNotDisposed();
            return _file.Content.Length;
        }
    }

    public override long Position
    {
        get
        {
            AssertNotDisposed();
            return _position;
        }
        set
        {
            AssertNotDisposed();

            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The position cannot be negative.");
            }

            if (_appendOnly && value < Length)
            {
                throw new IOException("Cannot seek to a position before the end of the file in append mode.");
            }

            _position = value;
            _file.Content.SetPosition(_position);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        _onDispose.Invoke();

        base.Dispose(disposing);
    }

    public override void Flush()
    {
        AssertNotDisposed();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        AssertNotDisposed();
        AssertCanRead();

        int readCount = _file.Content.Read(_position, buffer, offset, count);
        _position += readCount;
        _file.SetAccessedOn(DateTime.Now);
        return readCount;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        AssertNotDisposed();

        var newPosition = offset;

        switch (origin)
        {
            case SeekOrigin.Current:
                newPosition += _position;
                break;
            case SeekOrigin.End:
                newPosition += _file.Content.Length;
                break;
        }

        if (newPosition < 0)
        {
            throw new IOException("An attempt was made to move the file pointer before the beginning of the file.");
        }

        if (_appendOnly && newPosition < _file.Content.Length)
        {
            throw new IOException("Cannot seek to a position before the end of the file in append mode.");
        }

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
    {
        AssertNotDisposed();
        AssertCanWrite();

        _file.Content.Length = value;
        var time = DateTime.Now;
        _file.SetAccessedOn(time);
        _file.SetUpdatedOn(time);

        if (_position > value)
        {
            _position = value;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        AssertNotDisposed();
        AssertCanWrite();

        if (_appendOnly)
        {
            _position = _file.Content.Append(buffer, offset, count);
        }
        else
        {
            _file.Content.Write(_position, buffer, offset, count);
            _position += count;
        }

        var time = DateTime.Now;
        _file.SetAccessedOn(time);
        _file.SetUpdatedOn(time);
    }

    private void AssertCanRead()
    {
        if (!_canRead)
        {
            throw new NotSupportedException("Stream does not support reading.");
        }
    }

    private void AssertCanWrite()
    {
        if (!_canWrite)
        {
            throw new NotSupportedException("Stream does not support writing.");
        }
    }

    private void AssertNotDisposed()
    {
        if (_isDisposed > 0)
        {
            throw new ObjectDisposedException(nameof(InMemoryFileStream), "Cannot access a closed file.");
        }
    }
}

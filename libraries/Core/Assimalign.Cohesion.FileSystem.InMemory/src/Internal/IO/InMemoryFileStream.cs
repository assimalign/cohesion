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
    private readonly bool _isExclusive;
    private int _isDisposed;
    private long _position;

    public InMemoryFileStream(InMemoryFileSystemFile file, bool canRead, bool canWrite, bool isExclusive)
    {
        _file = file;
        _canWrite = canWrite;
        _canRead = canRead;
        _isExclusive = isExclusive;
        _position = 0;
        Debug.Assert(file.IsLocked);
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
                throw new ArgumentOutOfRangeException("The position cannot be negative");
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

        _file.Close(!_isExclusive);

        base.Dispose(disposing);
    }
    public override void Flush()
    {
        AssertNotDisposed();
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        AssertNotDisposed();
        int readCount = _file.Content.Read(_position, buffer, offset, count);
        _position += readCount;
        _file.AccessedOn = DateTime.Now;
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
            throw new IOException("An attempt was made to move the file pointer before the beginning of the file");
        }
        return _position = newPosition;
    }
    public override void SetLength(long value)
    {
        AssertNotDisposed();
        _file.Content.Length = value;
        var time = DateTime.Now;
        _file.AccessedOn = time;
        _file.UpdatedOn = time;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        AssertNotDisposed();
        _file.Content.Write(_position, buffer, offset, count);
        _position += count;
        var time = DateTime.Now;
        _file.AccessedOn = time;
        _file.UpdatedOn = time;
    }
    private void AssertNotDisposed()
    {
        if (_isDisposed > 0)
        {
            throw new ObjectDisposedException("Cannot access a closed file.");
        }
    }
}

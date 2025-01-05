using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileStream : Stream
{
    private readonly InMemoryFileSystem _fs;
    private readonly FsFileNode _fileNode;
    private readonly bool _canRead;
    private readonly bool _canWrite;
    private readonly bool _isExclusive;
    private int _isDisposed;
    private long _position;
    public InMemoryFileStream(InMemoryFileSystem fs, FsFileNode fileNode, bool canRead, bool canWrite, bool isExclusive)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _fileNode = fileNode ?? throw new ArgumentNullException(nameof(fs));
        _canWrite = canWrite;
        _canRead = canRead;
        _isExclusive = isExclusive;
        _position = 0;
        Debug.Assert(fileNode.IsLocked);
    }
    public override bool CanRead => _isDisposed == 0 && _canRead;
    public override bool CanSeek => _isDisposed == 0;
    public override bool CanWrite => _isDisposed == 0 && _canWrite;
    public override long Length
    {
        get
        {
            CheckNotDisposed();
            return _fileNode.Content.Length;
        }
    }
    public override long Position
    {
        get
        {
            CheckNotDisposed();
            return _position;
        }
        set
        {
            CheckNotDisposed();
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException("The position cannot be negative");
            }
            _position = value;
            _fileNode.Content.SetPosition(_position);
        }
    }
    ~InMemoryFileStream()
    {
        Dispose(false);
    }
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }
        if (_isExclusive)
        {
            _fs.ExitExclusive(_fileNode);
        }
        else
        {
            _fs.ExitShared(_fileNode);
        }
        base.Dispose(disposing);
    }
    public override void Flush()
    {
        CheckNotDisposed();
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckNotDisposed();
        int readCount = _fileNode.Content.Read(_position, buffer, offset, count);
        _position += readCount;
        _fileNode.LastAccessTime = DateTime.Now;
        return readCount;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckNotDisposed();
        var newPosition = offset;
        switch (origin)
        {
            case SeekOrigin.Current:
                newPosition += _position;
                break;
            case SeekOrigin.End:
                newPosition += _fileNode.Content.Length;
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
        CheckNotDisposed();
        _fileNode.Content.Length = value;
        var time = DateTime.Now;
        _fileNode.LastAccessTime = time;
        _fileNode.LastWriteTime = time;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckNotDisposed();
        _fileNode.Content.Write(_position, buffer, offset, count);
        _position += count;
        var time = DateTime.Now;
        _fileNode.LastAccessTime = time;
        _fileNode.LastWriteTime = time;
    }
    private void CheckNotDisposed()
    {
        if (_isDisposed > 0)
        {
            throw new ObjectDisposedException("Cannot access a closed file.");
        }
    }
}

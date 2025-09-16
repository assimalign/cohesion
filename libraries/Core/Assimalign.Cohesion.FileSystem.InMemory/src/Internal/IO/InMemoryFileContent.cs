using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileContent
{
    private readonly InMemoryFileSystemFile _file;
    private readonly MemoryStream _stream;
    private readonly Lock _lock;

    public InMemoryFileContent(InMemoryFileSystemFile file)
    {
        _lock = new Lock();
        _file = file;
        _stream = new MemoryStream();
    }
    public InMemoryFileContent(InMemoryFileSystemFile file, InMemoryFileContent copy)
    {
        _lock = new Lock();
        _file = file;
        var length = copy.Length;
        _stream = new MemoryStream(length <= int.MaxValue ? (int)length : int.MaxValue);
        CopyFrom(copy);
    }
    public byte[] ToArray()
    {
        lock (this)
        {
            return _stream.ToArray();
        }
    }
    public void CopyFrom(InMemoryFileContent copy)
    {
        lock (_lock)
        {
            var length = copy.Length;
            var buffer = copy.ToArray();
            _stream.Position = 0;
            _stream.Write(buffer, 0, buffer.Length);
            _stream.Position = 0;
            _stream.SetLength(length);
        }
    }
    public int Read(long position, byte[] buffer, int offset, int count)
    {
        lock (this)
        {
            _stream.Position = position;
            return _stream.Read(buffer, offset, count);
        }
    }
    public void Write(long position, byte[] buffer, int offset, int count)
    {
        lock (this)
        {
            _stream.Position = position;
            _stream.Write(buffer, offset, count);
            _file.FileSystem.IncrementSpaceUsed(count);
        }
        _file.ContentChanged();
    }
    public void SetPosition(long position)
    {
        lock (this)
        {
            _stream.Position = position;
        }
    }
    public long Length
    {
        get
        {
            lock (this)
            {
                return _stream.Length;
            }
        }
        set
        {
            lock (this)
            {
                _file.FileSystem.IncrementSpaceUsed(value - _stream.Length);
                _stream.SetLength(value);
            }
            _file.ContentChanged();
        }
    }
}

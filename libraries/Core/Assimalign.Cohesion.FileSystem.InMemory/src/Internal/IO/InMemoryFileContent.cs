using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileContent
{
    private readonly InMemoryFileSystemFile _file;
    private readonly MemoryStream _stream;

    public InMemoryFileContent(InMemoryFileSystemFile file)
    {
        _file = file;
        _stream = new MemoryStream();
    }
    public InMemoryFileContent(InMemoryFileSystemFile file, InMemoryFileContent copy)
    {
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
        lock (this)
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
                var dif = value - _stream.Length;

                _file.FileSystem.IncrementSpaceUsed(dif);

                _stream.SetLength(value);
            }
            _file.ContentChanged();
        }
    }
}

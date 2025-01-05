using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileContent
{
    private readonly FsFileNode _fileNode;
    private readonly MemoryStream _stream;
    public InMemoryFileContent(FsFileNode fileNode)
    {
        _fileNode = fileNode ?? throw new ArgumentNullException(nameof(fileNode));
        _stream = new MemoryStream();
    }
    public InMemoryFileContent(FsFileNode fileNode, InMemoryFileContent copy)
    {
        _fileNode = fileNode ?? throw new ArgumentNullException(nameof(fileNode));
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
        }
        _fileNode.ContentChanged();
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
                _stream.SetLength(value);
            }
            _fileNode.ContentChanged();
        }
    }
    public string DebuggerDisplay() => $"Size = {_stream.Length}";
}

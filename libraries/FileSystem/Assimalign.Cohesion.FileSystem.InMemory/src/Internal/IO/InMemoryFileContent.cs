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
        lock (_lock)
        {
            return _stream.ToArray();
        }
    }

    public void CopyFrom(InMemoryFileContent copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        var buffer = copy.ToArray();
        var newLength = copy.Length;

        lock (_lock)
        {
            _file.FileSystem.IncrementSpaceUsed(newLength - _stream.Length);
            _stream.Position = 0;
            _stream.SetLength(0);
            _stream.Write(buffer, 0, buffer.Length);
            _stream.Position = 0;
            _stream.SetLength(newLength);
        }

        _file.Dispatcher.RaiseEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed,
            _file.Path,
            _file.Name));
    }

    public int Read(long position, byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            _stream.Position = position;
            return _stream.Read(buffer, offset, count);
        }
    }

    public void Write(long position, byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            var endPosition = position + count;
            var growth = Math.Max(0, endPosition - _stream.Length);

            _stream.Position = position;
            _stream.Write(buffer, offset, count);

            if (growth > 0)
            {
                _file.FileSystem.IncrementSpaceUsed(growth);
            }
        }

        _file.Dispatcher.RaiseEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed,
            _file.Path,
            _file.Name));
    }

    public long Append(byte[] buffer, int offset, int count)
    {
        long position;

        lock (_lock)
        {
            _stream.Position = _stream.Length;
            _stream.Write(buffer, offset, count);
            _file.FileSystem.IncrementSpaceUsed(count);
            position = _stream.Position;
        }

        _file.Dispatcher.RaiseEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed,
            _file.Path,
            _file.Name));

        return position;
    }

    public void SetPosition(long position)
    {
        lock (_lock)
        {
            _stream.Position = position;
        }
    }

    public long Length
    {
        get
        {
            lock (_lock)
            {
                return _stream.Length;
            }
        }
        set
        {
            lock (_lock)
            {
                _file.FileSystem.IncrementSpaceUsed(value - _stream.Length);
                _stream.SetLength(value);
            }

            _file.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                _file.Path,
                _file.Name));
        }
    }
}

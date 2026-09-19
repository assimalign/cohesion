using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal sealed class InMemoryFileSystemFileHandle : IFileSystemFileHandle
{
    private readonly Lock _operationLock = new();
    private readonly InMemoryFileSystemFile _file;
    private readonly bool _canRead;
    private readonly bool _canWrite;
    private readonly Action _onDispose;
    private bool _isDisposed;

    public InMemoryFileSystemFileHandle(InMemoryFileSystemFile file, bool canRead, bool canWrite, Action onDispose)
    {
        _file = file;
        _canRead = canRead;
        _canWrite = canWrite;
        _onDispose = onDispose;
    }

    ~InMemoryFileSystemFileHandle()
    {
        Dispose();
    }

    public long Length
    {
        get
        {
            lock (_operationLock)
            {
                AssertNotDisposed();
                return _file.Content.Length;
            }
        }
    }

    public bool SupportsDurableFlush
    {
        get
        {
            lock (_operationLock)
            {
                AssertNotDisposed();
                return false;
            }
        }
    }

    public int Read(Span<byte> buffer, long offset)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            AssertCanRead();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);

            var count = _file.Content.Read(offset, buffer);
            _file.SetAccessedOn(DateTime.Now);
            return count;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span, offset));
        }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            AssertCanWrite();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);

            _file.Content.Write(offset, buffer);
            UpdateTimes();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span, offset);
            return ValueTask.CompletedTask;
        }
    }

    public void SetLength(long length)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            AssertCanWrite();
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, int.MaxValue);

            _file.Content.Length = length;
            UpdateTimes();
        }
    }

    public void Flush(bool durable)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();

            if (durable)
            {
                throw new NotSupportedException("The in-memory file system cannot flush data to durable storage.");
            }
        }
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        lock (_operationLock)
        {
            AssertNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            Flush(durable);
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        lock (_operationLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _onDispose();
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void AssertNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void AssertCanRead()
    {
        if (!_canRead)
        {
            throw new NotSupportedException("The handle does not support reading.");
        }
    }

    private void AssertCanWrite()
    {
        if (!_canWrite)
        {
            throw new NotSupportedException("The handle does not support writing.");
        }
    }

    private void UpdateTimes()
    {
        var time = DateTime.Now;
        _file.SetAccessedOn(time);
        _file.SetUpdatedOn(time);
    }
}

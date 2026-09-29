using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal sealed class PhysicalFileSystemFileHandle : IFileSystemFileHandle
{
    private readonly SafeFileHandle _handle;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhysicalFileSystemFileHandle"/> class.
    /// </summary>
    /// <param name="handle">The operating-system file handle this instance owns and disposes.</param>
    public PhysicalFileSystemFileHandle(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_handle);
        }
    }

    public bool SupportsDurableFlush
    {
        get
        {
            ThrowIfDisposed();
            return true;
        }
    }

    public int Read(Span<byte> buffer, long offset)
    {
        ThrowIfDisposed();
        return RandomAccess.Read(_handle, buffer, offset);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        ThrowIfDisposed();
        RandomAccess.Write(_handle, buffer, offset);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
    }

    public void SetLength(long length)
    {
        ThrowIfDisposed();
        RandomAccess.SetLength(_handle, length);
    }

    public void Flush(bool durable)
    {
        ThrowIfDisposed();
        // RandomAccess has no managed write buffer; only a durable flush needs an OS call.
        if (durable)
        {
            RandomAccess.FlushToDisk(_handle);
        }
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        // The runtime exposes no asynchronous FlushToDisk. Complete only after its guarantee holds.
        Flush(durable);
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _handle.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
}

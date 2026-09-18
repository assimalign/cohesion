using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal sealed class PhysicalFileSystemFileHandle(SafeFileHandle handle) : IFileSystemFileHandle
{
    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(handle);
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
        return RandomAccess.Read(handle, buffer, offset);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        ThrowIfDisposed();
        RandomAccess.Write(handle, buffer, offset);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken);
    }

    public void SetLength(long length)
    {
        ThrowIfDisposed();
        RandomAccess.SetLength(handle, length);
    }

    public void Flush(bool durable)
    {
        ThrowIfDisposed();
        // RandomAccess has no managed write buffer; only a durable flush needs an OS call.
        if (durable)
        {
            RandomAccess.FlushToDisk(handle);
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

    public void Dispose() => handle.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
}

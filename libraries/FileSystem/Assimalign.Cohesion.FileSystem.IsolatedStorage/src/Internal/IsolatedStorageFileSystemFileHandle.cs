using System;
using System.IO.IsolatedStorage;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal sealed class IsolatedStorageFileSystemFileHandle(IsolatedStorageFileStream stream) : IFileSystemFileHandle
{
    // IsolatedStorageFileStream rejects SafeFileHandle access. Keep its private cursor behind
    // one gate so each offset-addressed operation is atomic with respect to all other callers.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public long Length
    {
        get
        {
            _gate.Wait();
            try
            {
                ThrowIfDisposed();
                return stream.Length;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public bool SupportsDurableFlush
    {
        get
        {
            ThrowIfDisposed();
            // In .NET 10 Flush(bool) forwards to the isolated stream's backing FileStream.
            return true;
        }
    }

    public int Read(Span<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            stream.Position = offset;
            return stream.Read(buffer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            stream.Position = offset;
            stream.Write(buffer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetLength(long length)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            stream.SetLength(length);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Flush(bool durable)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            stream.Flush(durable);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (durable)
            {
                // There is no async durable-flush overload; never substitute FlushAsync here.
                stream.Flush(flushToDisk: true);
            }
            else
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            stream.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
}

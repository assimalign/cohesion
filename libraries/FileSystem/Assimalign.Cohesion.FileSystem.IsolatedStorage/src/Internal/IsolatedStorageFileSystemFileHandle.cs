using System;
using System.IO.IsolatedStorage;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal sealed class IsolatedStorageFileSystemFileHandle : IFileSystemFileHandle
{
    private readonly IsolatedStorageFileStream _stream;
    // IsolatedStorageFileStream rejects SafeFileHandle access. Keep its private cursor behind
    // one gate so each offset-addressed operation is atomic with respect to all other callers.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="IsolatedStorageFileSystemFileHandle"/> class.
    /// </summary>
    /// <param name="stream">The isolated-storage file stream this instance owns and disposes.</param>
    public IsolatedStorageFileSystemFileHandle(IsolatedStorageFileStream stream)
    {
        _stream = stream;
    }

    public long Length
    {
        get
        {
            _gate.Wait();
            try
            {
                ThrowIfDisposed();
                return _stream.Length;
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
            _stream.Position = offset;
            return _stream.Read(buffer);
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
            _stream.Position = offset;
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
            _stream.Position = offset;
            _stream.Write(buffer);
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
            _stream.Position = offset;
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
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
            _stream.SetLength(length);
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
            _stream.Flush(durable);
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
                _stream.Flush(flushToDisk: true);
            }
            else
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            _stream.Dispose();
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
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Serializes offset I/O over a legacy stream. A Stream has no durable-flush
/// contract, so this adapter must reject durable requests regardless of its type.
/// </summary>
internal sealed class StreamFileHandle : IFileSystemFileHandle
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamFileHandle"/> class.
    /// </summary>
    /// <param name="stream">The seekable legacy stream the handle serializes offset I/O over and owns.</param>
    public StreamFileHandle(Stream stream)
    {
        _stream = stream;
    }

    public long Length => _stream.Length;
    public bool SupportsDurableFlush => false;

    public int Read(Span<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            _stream.Position = offset;
            return _stream.Read(buffer);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            _stream.Position = offset;
            _stream.Write(buffer);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void SetLength(long length) => _stream.SetLength(length);

    public void Flush(bool durable)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }
        _stream.Flush();
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }
        return new ValueTask(_stream.FlushAsync(cancellationToken));
    }

    public void Dispose() => _stream.Dispose();
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

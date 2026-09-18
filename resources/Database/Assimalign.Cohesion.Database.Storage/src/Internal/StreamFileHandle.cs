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
internal sealed class StreamFileHandle(Stream stream) : IFileSystemFileHandle
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public long Length => stream.Length;
    public bool SupportsDurableFlush => false;

    public int Read(Span<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            stream.Position = offset;
            return stream.Read(buffer);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stream.Position = offset;
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        _gate.Wait();
        try
        {
            stream.Position = offset;
            stream.Write(buffer);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stream.Position = offset;
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void SetLength(long length) => stream.SetLength(length);

    public void Flush(bool durable)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }
        stream.Flush();
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }
        return new ValueTask(stream.FlushAsync(cancellationToken));
    }

    public void Dispose() => stream.Dispose();
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

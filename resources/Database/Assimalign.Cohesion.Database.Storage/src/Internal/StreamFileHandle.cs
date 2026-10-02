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
/// <remarks>
/// Every member that touches the stream — reads, writes, the length, growth and flushes —
/// takes the same gate, so no operation can observe or replace the stream's storage
/// while another is using it.
/// </remarks>
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

    // Length and SetLength take the gate like every read and write. Growing a MemoryStream
    // past its capacity copies its array into a new one, and a write that lands in the old
    // array after its region was copied is lost (#1157 review).
    public long Length
    {
        get
        {
            _gate.Wait();
            try
            {
                return _stream.Length;
            }
            finally { _gate.Release(); }
        }
    }

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

    public void SetLength(long length)
    {
        _gate.Wait();
        try
        {
            _stream.SetLength(length);
        }
        finally { _gate.Release(); }
    }

    public void Flush(bool durable)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }

        _gate.Wait();
        try
        {
            _stream.Flush();
        }
        finally { _gate.Release(); }
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        if (durable)
        {
            throw new NotSupportedException("A Stream does not carry a durable-flush contract. Open an IFileSystemFileHandle instead.");
        }

        return FlushGatedAsync(cancellationToken);
    }

    private async ValueTask FlushGatedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _stream.Dispose();
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

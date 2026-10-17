using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>Lets a test revoke durability before a catalog commit and restore it for a retry.</summary>
internal sealed class CatalogDurabilityHandle : IFileSystemFileHandle
{
    private readonly MemoryStream _stream;

    internal CatalogDurabilityHandle(MemoryStream stream)
    {
        _stream = stream;
    }

    internal Exception? NextDurableFlushFailure { get; set; }
    internal int FailedDurableFlushes { get; private set; }

    public bool SupportsDurableFlush { get; set; } = true;
    public long Length => _stream.Length;

    public int Read(Span<byte> buffer, long offset)
    {
        _stream.Position = offset;
        return _stream.Read(buffer);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span, offset));
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        _stream.Position = offset;
        _stream.Write(buffer);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span, offset);
        return default;
    }

    public void SetLength(long length) => _stream.SetLength(length);

    public void Flush(bool durable)
    {
        if (durable && NextDurableFlushFailure is { } failure)
        {
            NextDurableFlushFailure = null;
            FailedDurableFlushes++;
            throw failure;
        }
        if (durable && !SupportsDurableFlush)
        {
            throw new NotSupportedException("Durability revoked for the catalog rollback test.");
        }
        _stream.Flush();
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush(durable);
        return default;
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

/// <summary>
/// A stream modeling crash semantics for the index crash suites (mirrors the
/// storage test harness): writes survive a crash only when flushed, or immediately
/// in write-through mode (worst-case steal).
/// </summary>
public sealed class CrashSimulationStream : IFileSystemFileHandle
{
    private readonly MemoryStream _live = new();
    private readonly bool _writeThrough;
    private byte[] _durable = Array.Empty<byte>();

    public CrashSimulationStream(bool writeThrough = false)
    {
        _writeThrough = writeThrough;
    }

    public byte[] CaptureDurable() => (byte[])_durable.Clone();

    public bool SupportsDurableFlush => true;

    public long Length => _live.Length;

    // Preserve the fixture's prior flush-gated persistence while carrying the
    // simulated durability contract explicitly.
    public void Flush(bool durable = false) => _durable = _live.ToArray();

    public int Read(Span<byte> buffer, long offset)
    {
        _live.Position = offset;
        return _live.Read(buffer);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span, offset));
    }

    public void SetLength(long value)
    {
        _live.SetLength(value);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        _live.Position = offset;
        _live.Write(buffer);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span, offset);
        return default;
    }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush(durable);
        return default;
    }

    public void Dispose() => _live.Dispose();

    public ValueTask DisposeAsync() => _live.DisposeAsync();
}

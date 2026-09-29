using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Explicit simulated durability for harnesses that test transaction and recovery
/// behavior independently of physical persistence. This is never a production
/// claim that memory survives power loss.
/// </summary>
internal sealed class SimulatedDurableFileHandle : IFileSystemFileHandle
{
    private readonly Stream _stream;
    private readonly object _sync = new();

    internal SimulatedDurableFileHandle() : this(new MemoryStream()) { }

    internal SimulatedDurableFileHandle(Stream stream) => _stream = stream;

    public long Length { get { lock (_sync) { return _stream.Length; } } }

    public bool SupportsDurableFlush => true;

    public int Read(Span<byte> buffer, long offset)
    {
        lock (_sync)
        {
            _stream.Position = offset;
            return _stream.Read(buffer);
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span, offset));
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        lock (_sync)
        {
            _stream.Position = offset;
            _stream.Write(buffer);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span, offset);
        return default;
    }

    public void SetLength(long length) { lock (_sync) { _stream.SetLength(length); } }

    public void Flush(bool durable) { lock (_sync) { _stream.Flush(); } }

    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush(durable);
        return default;
    }

    public void Dispose() => _stream.Dispose();

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

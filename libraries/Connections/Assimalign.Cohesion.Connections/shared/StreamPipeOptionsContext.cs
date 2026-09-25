using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>Owns a shared memory pool and the reader and writer options for stream adapters.</summary>
/// <remarks>Complete every pipe using these options before disposing this context.</remarks>
// Deviates from the repo namespace-matches-assembly rule per design decision: this file is
// shared source (CohesionSharedSource), compiled into each transport driver that owns a pool.
// Every instance is created and disposed inside one driver assembly and never crosses an
// assembly boundary, so linking a private copy per driver is safe.
internal sealed class StreamPipeOptionsContext : IDisposable
{
    private readonly MemoryPool<byte> _memoryPool;
    private bool _isDisposed;

    public StreamPipeOptionsContext(
        MemoryPool<byte> memoryPool,
        StreamPipeReaderOptions readerOptions,
        StreamPipeWriterOptions writerOptions)
    {
        _memoryPool = memoryPool;
        ReaderOptions = readerOptions;
        WriterOptions = writerOptions;
    }

    /// <summary>Gets the stream reader options that use the owned memory pool.</summary>
    public StreamPipeReaderOptions ReaderOptions { get; }

    /// <summary>Gets the stream writer options that use the owned memory pool.</summary>
    public StreamPipeWriterOptions WriterOptions { get; }

    /// <summary>Releases the owned memory pool; repeated calls have no effect.</summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _memoryPool.Dispose();
    }
}

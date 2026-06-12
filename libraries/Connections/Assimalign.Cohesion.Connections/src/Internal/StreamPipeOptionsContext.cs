using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections.Internal;

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

    public StreamPipeReaderOptions ReaderOptions { get; }

    public StreamPipeWriterOptions WriterOptions { get; }

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

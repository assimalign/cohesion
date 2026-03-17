using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports.Internal;

internal sealed class TransportPipeOptionsContext : IDisposable
{
    private readonly MemoryPool<byte> _memoryPool;
    private bool _isDisposed;

    public TransportPipeOptionsContext(MemoryPool<byte> memoryPool, PipeOptions inputOptions, PipeOptions outputOptions)
    {
        _memoryPool = memoryPool;
        InputOptions = inputOptions;
        OutputOptions = outputOptions;
    }

    public PipeOptions InputOptions { get; }

    public PipeOptions OutputOptions { get; }

    public int BlockSize => _memoryPool.MaxBufferSize;

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

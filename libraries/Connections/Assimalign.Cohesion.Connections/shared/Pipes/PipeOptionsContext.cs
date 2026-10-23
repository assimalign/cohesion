using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>Owns a shared memory pool and the receive and send pipe options that use it.</summary>
/// <remarks>Complete every pipe using these options before disposing this context.</remarks>
// Deviates from the repo namespace-matches-assembly rule per design decision: this file is
// shared source (CohesionSharedSource), compiled into each transport driver that owns a pool.
// Every instance is created and disposed inside one driver assembly and never crosses an
// assembly boundary, so linking a private copy per driver is safe.
internal sealed class PipeOptionsContext : IDisposable
{
    private readonly MemoryPool<byte> _memoryPool;
    private bool _isDisposed;

    public PipeOptionsContext(MemoryPool<byte> memoryPool, PipeOptions inputOptions, PipeOptions outputOptions)
    {
        _memoryPool = memoryPool;
        InputOptions = inputOptions;
        OutputOptions = outputOptions;
    }

    /// <summary>Gets the receive pipe options.</summary>
    public PipeOptions InputOptions { get; }

    /// <summary>Gets the send pipe options.</summary>
    public PipeOptions OutputOptions { get; }

    /// <summary>Gets the maximum buffer size, in bytes, supplied by the owned memory pool.</summary>
    public int BlockSize => _memoryPool.MaxBufferSize;

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

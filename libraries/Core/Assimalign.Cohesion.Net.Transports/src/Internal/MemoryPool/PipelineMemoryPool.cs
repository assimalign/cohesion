﻿using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class PipelineMemoryPool : MemoryPool<byte>
{
    /// <summary>
    /// Thread-safe collection of blocks which are currently in the pool. A slab will pre-allocate all of the block tracking objects
    /// and add them to this collection. When memory is requested it is taken from here first, and when it is returned it is re-added.
    /// </summary>
    private readonly ConcurrentQueue<PipelineMemoryPoolBlock> blocks = new();
    /// <summary>
    /// This is part of implementing the IDisposable pattern.
    /// </summary>
    private bool isDisposed; // To detect redundant calls
    private readonly object disposeSync = new object();


    /// <summary>
    /// Max allocation block size for pooled blocks,
    /// larger values can be leased but they will be disposed after use rather than returned to the pool.
    /// </summary>
    public override int MaxBufferSize => BlockSize;
    /// <summary>
    /// The size of a block. 4096 is chosen because most operating systems use 4k pages.
    /// </summary>
    public static int BlockSize => 4096;

    /// <summary>
    /// This default value passed in to Rent to use the default value for the pool.
    /// </summary>
    private const int AnySize = -1;

    public override IMemoryOwner<byte> Rent(int size = AnySize)
    {
        if (size > BlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        if (isDisposed)
        {
            throw new ObjectDisposedException("MemoryPool");
        }
        if (blocks.TryDequeue(out var block))
        {
            // block successfully taken from the stack - return it
            return block;
        }
        return new PipelineMemoryPoolBlock(this, BlockSize);
    }

    /// <summary>
    /// Called to return a block to the pool. Once Return has been called the memory no longer belongs to the caller, and
    /// Very Bad Things will happen if the memory is read of modified subsequently. If a caller fails to call Return and the
    /// block tracking object is garbage collected, the block tracking object's finalizer will automatically re-create and return
    /// a new tracking object into the pool. This will only happen if there is a bug in the server, however it is necessary to avoid
    /// leaving "dead zones" in the slab due to lost block tracking objects.
    /// </summary>
    /// <param name="block">The block to return. It must have been acquired by calling Lease on the same memory pool instance.</param>
    internal void Return(PipelineMemoryPoolBlock block)
    {
        if (!isDisposed)
        {
            blocks.Enqueue(block);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (isDisposed)
        {
            return;
        }
        lock (disposeSync)
        {
            isDisposed = true;

            if (disposing)
            {
                // Discard blocks in pool
                while (blocks.TryDequeue(out _))
                {

                }
            }
        }
    }
    public static MemoryPool<byte> Create() => new PipelineMemoryPool();
}
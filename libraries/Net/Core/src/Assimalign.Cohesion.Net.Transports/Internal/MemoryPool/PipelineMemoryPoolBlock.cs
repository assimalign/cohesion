using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class PipelineMemoryPoolBlock : IMemoryOwner<byte>
{
    internal PipelineMemoryPoolBlock(PipelineMemoryPool pool, int length)
    {
        Pool = pool;

        var pinnedArray = GC.AllocateUninitializedArray<byte>(length, pinned: true);

        Memory = MemoryMarshal.CreateFromPinnedArray(pinnedArray, 0, pinnedArray.Length);
    }

    /// <summary>
    /// Back-reference to the memory pool which this block was allocated from. It may only be returned to this pool.
    /// </summary>
    public PipelineMemoryPool Pool { get; }

    public Memory<byte> Memory { get; }

    public void Dispose()
    {
        Pool.Return(this);
    }
}
using System.Runtime.InteropServices;
using System.Buffers;

namespace System.IO.Pipelines;

internal class PipeMemoryPoolBlock : IMemoryOwner<byte>
{
    internal PipeMemoryPoolBlock(PipeMemoryPool pool, int length)
    {
        Pool = pool;

        var pinnedArray = GC.AllocateUninitializedArray<byte>(length, pinned: true);

        Memory = MemoryMarshal.CreateFromPinnedArray(pinnedArray, 0, pinnedArray.Length);
    }

    /// <summary>
    /// Back-reference to the memory pool which this block was allocated from. It may only be returned to this pool.
    /// </summary>
    public PipeMemoryPool Pool { get; }

    public Memory<byte> Memory { get; }

    public void Dispose()
    {
        Pool.Return(this);
    }
}
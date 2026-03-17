namespace System.Buffers;

/// <summary>
/// Represents a point-in-time view of an <see cref="AdaptiveMemoryPool"/>.
/// </summary>
public readonly struct AdaptiveMemoryPoolSnapshot
{
    internal AdaptiveMemoryPoolSnapshot(
        int blockSize,
        int allocatedBlockCount,
        int inUseBlockCount,
        int retainedBlockCount,
        int peakInUseBlockCount,
        TimeSpan timeSinceLastRent)
    {
        BlockSize = blockSize;
        AllocatedBlockCount = allocatedBlockCount;
        InUseBlockCount = inUseBlockCount;
        RetainedBlockCount = retainedBlockCount;
        PeakInUseBlockCount = peakInUseBlockCount;
        TimeSinceLastRent = timeSinceLastRent;
    }

    /// <summary>
    /// Gets the fixed block size used by the pool.
    /// </summary>
    public int BlockSize { get; }

    /// <summary>
    /// Gets the total number of blocks currently managed by the pool.
    /// </summary>
    public int AllocatedBlockCount { get; }

    /// <summary>
    /// Gets the number of blocks currently checked out from the pool.
    /// </summary>
    public int InUseBlockCount { get; }

    /// <summary>
    /// Gets the number of idle blocks currently retained by the pool.
    /// </summary>
    public int RetainedBlockCount { get; }

    /// <summary>
    /// Gets the maximum number of simultaneously checked-out blocks observed by the pool.
    /// </summary>
    public int PeakInUseBlockCount { get; }

    /// <summary>
    /// Gets the elapsed time since the last successful rent operation.
    /// </summary>
    public TimeSpan TimeSinceLastRent { get; }
}

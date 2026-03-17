using System.Threading;

namespace System.Buffers;

/// <summary>
/// Defines configuration for an <see cref="AdaptiveMemoryPool"/>.
/// </summary>
public sealed class AdaptiveMemoryPoolOptions
{
    private int _blockSize = AdaptiveMemoryPool.DefaultBlockSize;
    private TimeSpan _trimInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the fixed block size used for pooled allocations.
    /// </summary>
    /// <remarks>
    /// Requested buffers larger than the configured block size are rejected.
    /// </remarks>
    public int BlockSize
    {
        get => _blockSize;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _blockSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the policy used to determine how many idle blocks are retained.
    /// </summary>
    public IAdaptiveMemoryPoolPolicy Policy { get; set; } = new AdaptiveMemoryPoolPressurePolicy();

    /// <summary>
    /// Gets or sets the time provider used for policy evaluation and scheduled trimming.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the interval used for scheduled trimming.
    /// </summary>
    /// <remarks>
    /// Set to <see cref="TimeSpan.Zero"/> to disable background trimming and use manual trimming only.
    /// </remarks>
    public TimeSpan TrimInterval
    {
        get => _trimInterval;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _trimInterval = value;
        }
    }
}

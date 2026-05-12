namespace System.Buffers;

/// <summary>
/// Retains a pressure-sensitive number of idle blocks based on recent pool activity.
/// </summary>
public sealed class AdaptiveMemoryPoolPressurePolicy : IAdaptiveMemoryPoolPolicy
{
    private int _minimumRetainedBlocks = 8;
    private int _maximumRetainedBlocks = 256;
    private double _peakRetentionRatio = 0.25;
    private TimeSpan _warmWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the minimum number of idle blocks retained by the pool.
    /// </summary>
    public int MinimumRetainedBlocks
    {
        get => _minimumRetainedBlocks;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value > _maximumRetainedBlocks)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _minimumRetainedBlocks = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of idle blocks retained by the pool.
    /// </summary>
    public int MaximumRetainedBlocks
    {
        get => _maximumRetainedBlocks;
        set
        {
            if (value < _minimumRetainedBlocks)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _maximumRetainedBlocks = value;
        }
    }

    /// <summary>
    /// Gets or sets the fraction of the observed peak in-use block count retained during the warm window.
    /// </summary>
    public double PeakRetentionRatio
    {
        get => _peakRetentionRatio;
        set
        {
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _peakRetentionRatio = value;
        }
    }

    /// <summary>
    /// Gets or sets the time window during which recent pool activity influences retention.
    /// </summary>
    public TimeSpan WarmWindow
    {
        get => _warmWindow;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _warmWindow = value;
        }
    }

    /// <inheritdoc />
    public int GetRetentionLimit(AdaptiveMemoryPoolSnapshot snapshot)
    {
        int targetRetainedBlocks = MinimumRetainedBlocks;

        if (snapshot.TimeSinceLastRent <= WarmWindow)
        {
            targetRetainedBlocks = Math.Max(
                MinimumRetainedBlocks,
                (int)Math.Ceiling(snapshot.PeakInUseBlockCount * PeakRetentionRatio));
        }

        return Math.Clamp(targetRetainedBlocks, MinimumRetainedBlocks, MaximumRetainedBlocks);
    }
}

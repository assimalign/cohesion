using System;

namespace Assimalign.Cohesion.Caching.InMemory;

/// <summary>
/// Configuration knobs for <see cref="MemoryCache"/>.
/// </summary>
public sealed class MemoryCacheOptions
{
    private TimeSpan _expirationScanFrequency = TimeSpan.FromMinutes(1);
    private long? _sizeLimit;
    private double _compactionPercentage = 0.05;

    /// <summary>
    /// Gets or sets the maximum interval between background expiration scans.
    /// </summary>
    /// <remarks>
    /// Defaults to one minute. The scan also runs lazily on every cache mutation, so this value
    /// is an upper bound rather than a guaranteed cadence. Must be greater than
    /// <see cref="TimeSpan.Zero"/>.
    /// </remarks>
    public TimeSpan ExpirationScanFrequency
    {
        get => _expirationScanFrequency;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "ExpirationScanFrequency must be greater than zero.");
            }

            _expirationScanFrequency = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum cumulative size allowed in the cache. When <see langword="null"/>,
    /// the cache imposes no size limit and entries are not required to declare a size.
    /// </summary>
    /// <remarks>
    /// When a size limit is set, every entry committed to the cache must declare
    /// <see cref="ICacheEntry.Size"/>. Entries that would push the cache over the limit trigger
    /// priority-based eviction; if the eviction cannot reclaim enough room the entry is rejected
    /// and post-eviction callbacks fire with <see cref="CacheEvictionReason.Capacity"/>.
    /// </remarks>
    public long? SizeLimit
    {
        get => _sizeLimit;
        set
        {
            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "SizeLimit must be greater than or equal to zero.");
            }

            _sizeLimit = value;
        }
    }

    /// <summary>
    /// Gets or sets the fraction of <see cref="SizeLimit"/> the cache attempts to free during a
    /// compaction pass triggered by a capacity-driven eviction.
    /// </summary>
    /// <remarks>
    /// Must be strictly greater than zero and at most one. Defaults to 0.05 (five percent).
    /// </remarks>
    public double CompactionPercentage
    {
        get => _compactionPercentage;
        set
        {
            if (value is <= 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "CompactionPercentage must be in the (0, 1] range.");
            }

            _compactionPercentage = value;
        }
    }

    /// <summary>
    /// Gets or sets the clock used for expiration and access tracking. When <see langword="null"/>,
    /// the cache uses <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }
}

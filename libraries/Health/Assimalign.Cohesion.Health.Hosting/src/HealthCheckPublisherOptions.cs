using System;

namespace Assimalign.Cohesion.Health.Hosting;

/// <summary>
/// Options for the periodic health-check publisher. All values are captured at builder time and
/// are immutable once the host starts.
/// </summary>
public sealed class HealthCheckPublisherOptions
{
    private TimeSpan _period = TimeSpan.FromSeconds(30);
    private TimeSpan _delay = TimeSpan.FromSeconds(5);
    private TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the initial delay before the first publish cycle. Acts as a startup offset so
    /// the first evaluation does not race application startup. Defaults to 5 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
    public TimeSpan Delay
    {
        get => _delay;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The publisher delay must not be negative.");
            }

            _delay = value;
        }
    }

    /// <summary>
    /// Gets or sets the interval between publish cycles. Defaults to 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a non-positive value.</exception>
    public TimeSpan Period
    {
        get => _period;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The publisher period must be positive.");
            }

            _period = value;
        }
    }

    /// <summary>
    /// Gets or sets the timeout applied to a single health-evaluation cycle. When a cycle exceeds
    /// this budget the cycle is abandoned and no report is published for it. Use
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable. Defaults to 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a non-positive value other than <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan Timeout
    {
        get => _timeout;
        set
        {
            if (value != System.Threading.Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The publisher timeout must be positive or Timeout.InfiniteTimeSpan.");
            }

            _timeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the predicate that selects which registered checks each cycle evaluates. When
    /// <see langword="null"/> (the default), every registered check runs.
    /// </summary>
    public Func<HealthCheckRegistration, bool>? Predicate { get; set; }
}

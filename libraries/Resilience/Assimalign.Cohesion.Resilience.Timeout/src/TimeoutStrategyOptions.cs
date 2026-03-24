using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public class TimeoutStrategyOptions
{
    /// <summary>
    /// 
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the default timeout.
    /// </summary>
    /// <value>
    /// This value must be greater than 10 milliseconds and less than 24 hours. The default value is 30 seconds.
    /// </value>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a timeout generator that generates the timeout for a given execution.
    /// </summary>
    /// <remarks>
    /// When generator is <see langword="null"/> then the <see cref="Timeout"/> property's value is used instead.
    /// When generator returns a <see cref="TimeSpan"/> value that is less than or equal to <see cref="TimeSpan.Zero"/>
    /// then the strategy will do nothing.
    /// <para>
    /// Return <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable the timeout for the given execution.
    /// </para>
    /// </remarks>
    /// <value>
    /// The default value is <see langword="null"/>.
    /// </value>
    public Func<TimeoutGeneratorArguments, ValueTask<TimeSpan>>? TimeoutGenerator { get; set; }

    /// <summary>
    /// Gets or sets the timeout delegate that raised when timeout occurs.
    /// </summary>
    /// <value>
    /// The default value is <see langword="null"/>.
    /// </value>
    public Func<OnTimeoutArguments, ValueTask>? OnTimeout { get; set; }
}

using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Options for the in-process infrastructure an <see cref="InProcessInfrastructureBuilder"/> builds.
/// </summary>
[DebuggerDisplay("StopTimeout = {StopTimeout}")]
public sealed class InProcessInfrastructureOptions
{
    private TimeSpan _stopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long a stopping program has to drain before it is left behind; thirty
    /// seconds unless set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan StopTimeout
    {
        get => _stopTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _stopTimeout = value;
        }
    }
}

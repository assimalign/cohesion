using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Options controlling in-process resource invocation, health probing, restart, and state.
/// </summary>
public sealed class InProcessGatewayOptions : ApplicationGatewayOptions
{
    private int _livenessFailureThreshold = 3;
    private int _maximumRestartAttempts = 5;

    /// <summary>
    /// Gets or sets the root for persisted loopback allocations and private claims. Defaults
    /// to <c>.cohesion</c> under the current working directory.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>Gets or sets how often health probes run. Defaults to one second.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the maximum duration of one probe. Defaults to five seconds.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the consecutive liveness failures that trigger a nested restart.
    /// Defaults to three.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int LivenessFailureThreshold
    {
        get => _livenessFailureThreshold;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(LivenessFailureThreshold));
            _livenessFailureThreshold = value;
        }
    }

    /// <summary>
    /// Gets or sets the first delay before a nested restart. Successive delays double up to
    /// <see cref="MaximumRestartBackoff"/>. Defaults to one second.
    /// </summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the largest delay between restart attempts. Defaults to 30 seconds.</summary>
    public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum restart attempts per member. Defaults to five; zero disables restarts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaximumRestartAttempts
    {
        get => _maximumRestartAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaximumRestartAttempts));
            _maximumRestartAttempts = value;
        }
    }

    internal void Validate()
    {
        ValidateCommon();
        ArgumentNullException.ThrowIfNull(TimeProvider);

        if (StateDirectory is not null && string.IsNullOrWhiteSpace(StateDirectory))
        {
            throw new ArgumentException(
                ErrorMessages.StateDirectoryEmpty,
                nameof(StateDirectory));
        }

        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProbeInterval),
                ErrorMessages.ProbeIntervalInvalid);
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProbeTimeout),
                ErrorMessages.ProbeTimeoutInvalid);
        }

        if (InitialRestartBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialRestartBackoff),
                ErrorMessages.InitialRestartBackoffInvalid);
        }

        if (MaximumRestartBackoff < InitialRestartBackoff)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRestartBackoff),
                ErrorMessages.MaximumRestartBackoffInvalid);
        }
    }
}

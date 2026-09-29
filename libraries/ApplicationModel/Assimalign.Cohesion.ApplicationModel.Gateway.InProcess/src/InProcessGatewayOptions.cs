using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Options controlling in-process resource invocation, health probing, restart, and state.
/// </summary>
public sealed class InProcessGatewayOptions : ApplicationGatewayOptions
{
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
    public int LivenessFailureThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets the first delay before a nested restart. Successive delays double up to
    /// <see cref="MaximumRestartBackoff"/>. Defaults to one second.
    /// </summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the largest delay between restart attempts. Defaults to 30 seconds.</summary>
    public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the maximum restart attempts per member. Defaults to five.</summary>
    public int MaximumRestartAttempts { get; set; } = 5;

    internal void Validate()
    {
        ValidateCommon();
        ArgumentNullException.ThrowIfNull(TimeProvider);

        if (StateDirectory is not null && string.IsNullOrWhiteSpace(StateDirectory))
        {
            throw new ArgumentException(
                "StateDirectory must not be empty when specified.",
                nameof(StateDirectory));
        }

        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProbeInterval),
                "ProbeInterval must be greater than zero.");
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProbeTimeout),
                "ProbeTimeout must be greater than zero.");
        }

        if (LivenessFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LivenessFailureThreshold),
                "LivenessFailureThreshold must be greater than zero.");
        }

        if (InitialRestartBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialRestartBackoff),
                "InitialRestartBackoff must be zero or greater.");
        }

        if (MaximumRestartBackoff < InitialRestartBackoff)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRestartBackoff),
                "MaximumRestartBackoff must be at least InitialRestartBackoff.");
        }

        if (MaximumRestartAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRestartAttempts),
                "MaximumRestartAttempts must be zero or greater.");
        }
    }
}

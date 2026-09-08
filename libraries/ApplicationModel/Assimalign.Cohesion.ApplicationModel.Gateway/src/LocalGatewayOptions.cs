using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Options controlling how the <see cref="LocalGateway"/> resolves, starts, probes, and stops
/// child processes.
/// </summary>
public sealed class LocalGatewayOptions : ApplicationGatewayOptions
{
    /// <summary>
    /// The directory searched for a resource's executable. Defaults to the orchestrator's
    /// <see cref="AppContext.BaseDirectory"/> when <see langword="null"/>.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// The directory that contains per-application local gateway state. Defaults to a
    /// <c>.cohesion</c> directory under the current working directory.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// How often an unsuccessful probe is retried. Defaults to 1&#160;second.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The maximum duration of one HTTP, TCP, or exec probe attempt. Defaults to 5&#160;seconds.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The number of consecutive liveness failures that requests a restart. Defaults to 3.
    /// </summary>
    public int LivenessFailureThreshold { get; set; } = 3;

    /// <summary>
    /// The first delay before restarting a failed process. Successive delays double up to
    /// <see cref="MaximumRestartBackoff"/>. Defaults to 1&#160;second.
    /// </summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The largest delay between restart attempts. Defaults to 30&#160;seconds.</summary>
    public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The maximum number of restart attempts for one resource. Defaults to 5.</summary>
    public int MaximumRestartAttempts { get; set; } = 5;

    /// <summary>
    /// How long to wait for a child process to exit during shutdown before it is force-killed.
    /// This is the fallback for manifest-less executables; manifest-backed resources use
    /// their lifecycle grace. Defaults to 30&#160;seconds.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether verified child processes left by an earlier gateway instance are
    /// gracefully stopped and relaunched instead of re-attached. Defaults to <see langword="false"/>.
    /// The <c>--restart-orphans</c> invocation option enables the same behavior.
    /// </summary>
    public bool RestartOrphans { get; set; }

    internal void Validate()
    {
        ValidateCommon();
        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeInterval), "ProbeInterval must be greater than zero.");
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout), "ProbeTimeout must be greater than zero.");
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

        if (StopGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopGrace), "StopGrace must be greater than zero.");
        }
    }
}

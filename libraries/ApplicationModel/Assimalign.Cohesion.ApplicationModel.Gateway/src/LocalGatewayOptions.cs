using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Options controlling how the <see cref="LocalGateway"/> resolves, starts, probes, and stops
/// child processes.
/// </summary>
public sealed class LocalGatewayOptions : ApplicationGatewayOptions
{
    private int _livenessFailureThreshold = 3;
    private int _maximumRestartAttempts = 5;

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
    /// The first delay before restarting a failed process. Successive delays double up to
    /// <see cref="MaximumRestartBackoff"/>. Defaults to 1&#160;second.
    /// </summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The largest delay between restart attempts. Defaults to 30&#160;seconds.</summary>
    public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum number of restart attempts for one resource. Defaults to 5; zero disables restarts.
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
            throw new ArgumentOutOfRangeException(nameof(ProbeInterval), ErrorMessages.ProbeIntervalInvalid);
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout), ErrorMessages.ProbeTimeoutInvalid);
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

        if (StopGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopGrace), ErrorMessages.StopGraceInvalid);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// An immutable registration describing one health check: its unique <see cref="Name"/>, the
/// <see cref="Factory"/> that produces the <see cref="IHealthCheck"/>, the <see cref="Tags"/>
/// used for readiness/liveness filtering, the per-check <see cref="Timeout"/>, and the
/// <see cref="FailureStatus"/> to report when the check throws or times out.
/// </summary>
/// <remarks>
/// Registrations are composed at builder time and are immutable once added to an
/// <see cref="IHealthChecksBuilder"/>. The check is produced through a factory rather than
/// stored as an instance so a host can resolve a fresh (or dependency-injected) check per
/// evaluation without the core library taking a dependency on any DI container.
/// </remarks>
public sealed class HealthCheckRegistration
{
    /// <summary>
    /// A sentinel indicating the check has no per-check timeout.
    /// </summary>
    public static readonly TimeSpan InfiniteTimeout = System.Threading.Timeout.InfiniteTimeSpan;

    private readonly HashSet<string> _tags;

    /// <summary>
    /// Initializes a new registration over a shared <see cref="IHealthCheck"/> instance.
    /// </summary>
    /// <param name="name">The unique name of the check.</param>
    /// <param name="instance">The check instance. Reused for every evaluation.</param>
    /// <param name="failureStatus">The status reported when the check throws or times out.</param>
    /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
    /// <param name="timeout">The per-check timeout, or <see langword="null"/> for no timeout.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="instance"/> is <see langword="null"/>.</exception>
    public HealthCheckRegistration(
        string name,
        IHealthCheck instance,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        : this(name, CreateInstanceFactory(instance), failureStatus, tags, timeout)
    {
    }

    /// <summary>
    /// Initializes a new registration over a factory that produces the <see cref="IHealthCheck"/>.
    /// </summary>
    /// <param name="name">The unique name of the check.</param>
    /// <param name="factory">A factory that produces the check to run.</param>
    /// <param name="failureStatus">The status reported when the check throws or times out.</param>
    /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
    /// <param name="timeout">The per-check timeout, or <see langword="null"/> for no timeout.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="timeout"/> is neither positive nor <see cref="InfiniteTimeout"/>.</exception>
    public HealthCheckRegistration(
        string name,
        Func<IHealthCheck> factory,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);

        TimeSpan effectiveTimeout = timeout ?? InfiniteTimeout;
        if (effectiveTimeout != InfiniteTimeout && effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                effectiveTimeout,
                "The health-check timeout must be positive or HealthCheckRegistration.InfiniteTimeout.");
        }

        Name = name;
        Factory = factory;
        FailureStatus = failureStatus;
        Timeout = effectiveTimeout;
        _tags = tags is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the unique name of the check.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the factory that produces the <see cref="IHealthCheck"/> to run.
    /// </summary>
    public Func<IHealthCheck> Factory { get; }

    /// <summary>
    /// Gets the status reported when the check throws or exceeds its <see cref="Timeout"/>.
    /// </summary>
    public HealthStatus FailureStatus { get; }

    /// <summary>
    /// Gets the per-check timeout, or <see cref="InfiniteTimeout"/> when the check has none.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Gets the tags associated with the check (case-insensitive). Used to select readiness
    /// vs. liveness subsets at probe time.
    /// </summary>
    public IReadOnlyCollection<string> Tags => _tags;

    /// <summary>
    /// Determines whether the registration carries the supplied tag (case-insensitive).
    /// </summary>
    /// <param name="tag">The tag to test for.</param>
    /// <returns><see langword="true"/> when the tag is present; otherwise <see langword="false"/>.</returns>
    public bool HasTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return _tags.Contains(tag);
    }

    private static Func<IHealthCheck> CreateInstanceFactory(IHealthCheck instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return () => instance;
    }
}

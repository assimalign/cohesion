using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// A minimum data-rate policy: the lowest sustained throughput, in octets per second, a peer must
/// maintain once a <see cref="GracePeriod"/> has elapsed, or the transport reclaims the exchange.
/// Used for the HTTP/1.1 request-body read (slow-body / trickle defence) and the streaming
/// response write (slow-reader defence), mirroring Kestrel's <c>MinDataRate</c> semantics.
/// </summary>
/// <remarks>
/// <para>
/// The rate is enforced as an <em>average</em> over the transfer, not instantaneously: a peer may
/// stall or burst freely inside the grace period, and the grace period restarts the allowance so a
/// peer that has already delivered a lot of data earns proportional slack. Only time actually spent
/// waiting for the peer counts against the rate — a slow application consuming an otherwise healthy
/// stream is never mistaken for a slow peer.
/// </para>
/// <para>
/// The policy is immutable; construct a new instance to change either dimension. A <c>null</c>
/// data-rate limit on <see cref="HttpConnectionListenerLimits"/> disables the check entirely.
/// </para>
/// </remarks>
public sealed class HttpMinDataRate
{
    /// <summary>
    /// Initializes a new minimum data-rate policy.
    /// </summary>
    /// <param name="bytesPerSecond">The minimum sustained rate, in octets per second, required after the grace period.</param>
    /// <param name="gracePeriod">
    /// The interval at the start of the transfer during which the rate is not yet enforced, giving a
    /// slow-starting or bursty peer time to get going before the average-rate check applies.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="bytesPerSecond"/> is not greater than zero, or when
    /// <paramref name="gracePeriod"/> is not greater than <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public HttpMinDataRate(double bytesPerSecond, TimeSpan gracePeriod)
    {
        if (bytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, "The minimum data rate must be greater than zero octets per second.");
        }

        if (gracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod), gracePeriod, "The grace period must be greater than zero.");
        }

        BytesPerSecond = bytesPerSecond;
        GracePeriod = gracePeriod;
    }

    /// <summary>
    /// Gets the minimum sustained rate, in octets per second, the peer must maintain once the
    /// <see cref="GracePeriod"/> has elapsed.
    /// </summary>
    public double BytesPerSecond { get; }

    /// <summary>
    /// Gets the interval at the start of the transfer during which the rate is not enforced.
    /// </summary>
    public TimeSpan GracePeriod { get; }
}

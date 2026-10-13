using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A minimum data-rate policy: the lowest sustained throughput, in octets per second, a peer must
/// maintain once a <see cref="GracePeriod"/> has elapsed, or the server transport reclaims the exchange.
/// A transport holds a request body to one (a slow sender trickling its body) and a response body to
/// another (a slow reader that withholds flow-control credit or stops reading), mirroring Kestrel's
/// <c>MinDataRate</c> semantics.
/// </summary>
/// <remarks>
/// <para>
/// The rate is enforced as an <em>average</em> over the transfer, not instantaneously: a peer may
/// stall or burst freely inside the grace period, and every octet it moves extends its allowance, so a
/// peer that has already moved a lot of data earns proportional slack. Only time the transport actually
/// spends waiting for the peer counts against the rate: a slow application consuming or producing an
/// otherwise healthy stream is never mistaken for a slow peer.
/// </para>
/// <para>
/// The policy is an immutable value object; construct a new instance to change either dimension. Where
/// a policy is optional, <see langword="null"/> disables the check. The server transport's listener
/// limits carry the defaults, and each request's parse context carries the values that apply to it
/// (<see cref="HttpExchangeInterceptorRequestContext.MinRequestBodyDataRate"/> and
/// <see cref="HttpExchangeInterceptorRequestContext.MinResponseDataRate"/>), which a request-parse
/// interceptor or a feature package can change for one request before the transport starts the
/// transfer.
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
        // Written as a negated comparison so NaN, which compares false either way, is refused too.
        if (!(bytesPerSecond > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, ErrorMessages.MinimumDataRateMustBeGreaterThanZeroOctetsPerSecond);
        }

        if (gracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod), gracePeriod, ErrorMessages.GracePeriodMustBeGreaterThanZero);
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

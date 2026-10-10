using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// An HTTP/2 connection's minimum request-body data rate (#1085), shared by the body readers of its
/// streams: the <see cref="HttpMinDataRate"/> each reader's <see cref="MinDataRateGate"/> enforces, and
/// the two things only the connection knows — how long its receive window has held the peer back, and
/// how a stream whose body falls below the rate is answered.
/// </summary>
/// <remarks>
/// <para>
/// One instance per connection, created only when the rate is configured; a CONNECT stream, whose DATA is
/// tunnel traffic rather than a request body (RFC 9110 §9.3.6), is given none.
/// </para>
/// <para>
/// The connection excuses a reader's wait only for the time its receive window spent too low to carry a
/// full frame, and each reader is excused for at most <see cref="MaxBackpressureExemptionTicks"/> in
/// total. A peer can pin the window by leaving one stream's body unread, so an uncapped exemption would
/// switch the rate off for every other stream of the connection.
/// </para>
/// </remarks>
internal sealed class Http2RequestBodyDataRate
{
    private readonly Func<bool> _isConnectionReceiveBackpressured;
    private readonly Func<long> _getConnectionReceiveBackpressuredTicks;
    private readonly Func<int, ValueTask> _rejectTooSlow;

    /// <summary>
    /// Initializes the policy.
    /// </summary>
    /// <param name="rate">The minimum data rate a request body must keep.</param>
    /// <param name="timeProvider">The monotonic clock the readers measure their waits on.</param>
    /// <param name="isConnectionReceiveBackpressured">
    /// Reports whether the connection-level receive window is too low to carry a full frame, so a peer may
    /// be unable to send this stream's DATA because other streams hold the window, not because it is slow.
    /// </param>
    /// <param name="getConnectionReceiveBackpressuredTicks">
    /// Returns how long, in <paramref name="timeProvider"/> ticks, the connection-level receive window has
    /// been low in total since the connection began, the current low period included. A reader samples it
    /// at the start and end of a wait, and the difference is the part of the wait the window excuses.
    /// </param>
    /// <param name="rejectTooSlow">
    /// Answers the stream whose body fell below the rate: <c>408</c> when no response has started, a
    /// reset otherwise. Never throws.
    /// </param>
    public Http2RequestBodyDataRate(
        HttpMinDataRate rate,
        TimeProvider timeProvider,
        Func<bool> isConnectionReceiveBackpressured,
        Func<long> getConnectionReceiveBackpressuredTicks,
        Func<int, ValueTask> rejectTooSlow)
    {
        Rate = rate;
        TimeProvider = timeProvider;
        MaxBackpressureExemptionTicks = (long)(rate.GracePeriod.TotalSeconds * timeProvider.TimestampFrequency);
        _isConnectionReceiveBackpressured = isConnectionReceiveBackpressured;
        _getConnectionReceiveBackpressuredTicks = getConnectionReceiveBackpressuredTicks;
        _rejectTooSlow = rejectTooSlow;
    }

    /// <summary>
    /// Gets the minimum data rate a request body must keep.
    /// </summary>
    public HttpMinDataRate Rate { get; }

    /// <summary>
    /// Gets the monotonic clock the readers measure their waits on.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the most wait time, in <see cref="TimeProvider"/> ticks, the connection's low receive window
    /// may excuse for one request body over its whole life: the rate's grace period. Past it, every wait
    /// is charged whatever the window, so a peer that pins the window with one unread stream cannot hold
    /// its other streams' bodies back for free.
    /// </summary>
    public long MaxBackpressureExemptionTicks { get; }

    /// <summary>
    /// Gets a value indicating whether the connection-level receive window is too low to carry a full
    /// frame (RFC 9113 §6.9), so a wait for DATA may say nothing about the peer's own rate.
    /// </summary>
    public bool IsConnectionReceiveBackpressured => _isConnectionReceiveBackpressured();

    /// <summary>
    /// Gets how long, in <see cref="TimeProvider"/> ticks, the connection-level receive window has been
    /// low in total, the current low period included. Only differences between two readings mean anything.
    /// </summary>
    public long ConnectionReceiveBackpressuredTicks => _getConnectionReceiveBackpressuredTicks();

    /// <summary>
    /// Answers the stream whose request body fell below the rate.
    /// </summary>
    /// <param name="streamId">The stream.</param>
    /// <returns>A task that completes once the answer is written or abandoned.</returns>
    public ValueTask RejectTooSlowAsync(int streamId) => _rejectTooSlow(streamId);
}

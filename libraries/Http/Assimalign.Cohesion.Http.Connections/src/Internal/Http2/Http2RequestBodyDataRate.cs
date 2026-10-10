using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// An HTTP/2 connection's minimum request-body data rate (#1085), shared by the body readers of its
/// streams: the <see cref="HttpMinDataRate"/> each reader's <see cref="MinDataRateGate"/> enforces, and
/// the two things only the connection knows — whether its receive window is what holds the peer back,
/// and how a stream whose body falls below the rate is answered.
/// </summary>
/// <remarks>
/// One instance per connection, created only when the rate is configured; a CONNECT stream, whose DATA is
/// tunnel traffic rather than a request body (RFC 9110 §9.3.6), is given none.
/// </remarks>
internal sealed class Http2RequestBodyDataRate
{
    private readonly Func<bool> _isConnectionReceiveBackpressured;
    private readonly Func<int, ValueTask> _rejectTooSlow;

    /// <summary>
    /// Initializes the policy.
    /// </summary>
    /// <param name="rate">The minimum data rate a request body must keep.</param>
    /// <param name="timeProvider">The monotonic clock the readers measure their waits on.</param>
    /// <param name="isConnectionReceiveBackpressured">
    /// Reports whether the connection-level receive window is low, so a peer may be unable to send this
    /// stream's DATA because other streams hold the window, not because it is slow.
    /// </param>
    /// <param name="rejectTooSlow">
    /// Answers the stream whose body fell below the rate: <c>408</c> when no response has started, a
    /// reset otherwise. Never throws.
    /// </param>
    public Http2RequestBodyDataRate(
        HttpMinDataRate rate,
        TimeProvider timeProvider,
        Func<bool> isConnectionReceiveBackpressured,
        Func<int, ValueTask> rejectTooSlow)
    {
        Rate = rate;
        TimeProvider = timeProvider;
        _isConnectionReceiveBackpressured = isConnectionReceiveBackpressured;
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
    /// Gets a value indicating whether the connection-level receive window is low (RFC 9113 §6.9), so a
    /// wait for DATA says nothing about the peer's own rate.
    /// </summary>
    public bool IsConnectionReceiveBackpressured => _isConnectionReceiveBackpressured();

    /// <summary>
    /// Answers the stream whose request body fell below the rate.
    /// </summary>
    /// <param name="streamId">The stream.</param>
    /// <returns>A task that completes once the answer is written or abandoned.</returns>
    public ValueTask RejectTooSlowAsync(int streamId) => _rejectTooSlow(streamId);
}

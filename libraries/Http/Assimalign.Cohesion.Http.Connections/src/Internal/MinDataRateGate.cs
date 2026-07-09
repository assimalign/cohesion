using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Enforces an <see cref="HttpMinDataRate"/> as an average over a transfer, measuring only the time
/// actually spent waiting for the peer. Shared by the HTTP/1.1 streaming request-body read (a slow
/// sender trickling its body) and the streaming response write (a slow reader refusing to drain).
/// </summary>
/// <remarks>
/// <para>
/// The gate is pure accounting: it owns no timer and performs no I/O. The caller bounds each
/// blocking transport operation with the delay from <see cref="TryGetOperationTimeout"/>, then
/// reports the elapsed wait and octets moved through <see cref="Record"/>. Because only the wait
/// duration of the blocking operation is recorded, an application that is slow to read (or a peer
/// slow to acknowledge) between operations never counts against the peer's rate.
/// </para>
/// <para>
/// The invariant is: the cumulative wait may not exceed <c>gracePeriod + bytes / bytesPerSecond</c>.
/// Rearranged, the allowance grows with every octet the peer delivers, so a peer that has already
/// sent a burst earns proportional slack, while one that stalls exhausts its allowance and is
/// reclaimed. All time is expressed in <see cref="TimeProvider.GetTimestamp"/> ticks so the same
/// monotonic clock drives measurement and the caller's cancellation deadline (AOT-safe:
/// integer/double arithmetic, no reflection, no <c>DateTime.UtcNow</c>).
/// </para>
/// </remarks>
internal sealed class MinDataRateGate
{
    // A per-operation wait longer than this is treated as effectively unbounded: no realistic
    // transfer needs to block a single read/write for over an hour, and clamping keeps the delay
    // inside CancellationTokenSource's accepted range.
    private static readonly TimeSpan MaxOperationTimeout = TimeSpan.FromHours(1);

    private readonly double _bytesPerSecond;
    private readonly long _graceTicks;
    private readonly long _frequency;
    private readonly TimeProvider _timeProvider;

    private long _bytesTransferred;
    private long _ticksWaited;

    /// <summary>
    /// Initializes the gate for the supplied rate policy and clock.
    /// </summary>
    /// <param name="rate">The minimum data-rate policy to enforce.</param>
    /// <param name="timeProvider">The monotonic clock used for measurement and deadline arithmetic.</param>
    public MinDataRateGate(HttpMinDataRate rate, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _bytesPerSecond = rate.BytesPerSecond;
        _timeProvider = timeProvider;
        _frequency = timeProvider.TimestampFrequency;
        _graceTicks = (long)(rate.GracePeriod.TotalSeconds * _frequency);
    }

    /// <summary>
    /// Gets the clock the caller should read timestamps from so its measured waits align with this
    /// gate's accounting and its cancellation deadline fires on the same clock.
    /// </summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Computes the maximum time the next blocking transport operation may wait before the peer
    /// falls below the configured rate.
    /// </summary>
    /// <param name="timeout">
    /// When this method returns <see langword="true"/>, the deadline for the next operation (clamped
    /// to a sane upper bound).
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the peer is still within its allowance and the operation may
    /// proceed under <paramref name="timeout"/>; <see langword="false"/> if the allowance is already
    /// exhausted and the caller must fail the transfer.
    /// </returns>
    public bool TryGetOperationTimeout(out TimeSpan timeout)
    {
        long remainingTicks = GetRemainingTicks();
        if (remainingTicks <= 0)
        {
            timeout = default;
            return false;
        }

        double seconds = remainingTicks / (double)_frequency;
        timeout = seconds >= MaxOperationTimeout.TotalSeconds
            ? MaxOperationTimeout
            : TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>
    /// Records the outcome of a blocking transport operation: the ticks it spent waiting and the
    /// octets it moved. Both extend or consume the peer's rate allowance for the next operation.
    /// </summary>
    /// <param name="ticksWaited">The wait duration, in <see cref="TimeProvider.GetTimestamp"/> ticks (never negative).</param>
    /// <param name="bytesTransferred">The octets moved by the operation.</param>
    public void Record(long ticksWaited, long bytesTransferred)
    {
        if (ticksWaited > 0)
        {
            _ticksWaited += ticksWaited;
        }

        if (bytesTransferred > 0)
        {
            _bytesTransferred += bytesTransferred;
        }
    }

    /// <summary>
    /// Gets the remaining wait allowance, in clock ticks, given the octets transferred so far. A
    /// non-positive result means the peer has fallen below the configured rate.
    /// </summary>
    /// <returns>The remaining allowance in ticks; may be negative.</returns>
    public long GetRemainingTicks()
    {
        long allowedTicks = _graceTicks + (long)(_bytesTransferred / _bytesPerSecond * _frequency);
        return allowedTicks - _ticksWaited;
    }
}

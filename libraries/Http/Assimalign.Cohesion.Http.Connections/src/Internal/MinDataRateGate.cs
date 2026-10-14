using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Enforces an <see cref="HttpMinDataRate"/> as an average over a transfer, measuring only the time
/// actually spent waiting for the peer. Shared by the request-body reads of every version (a slow
/// sender trickling its body: <c>Http1RequestBodyStream</c>, <c>Http2RequestBodyStream</c>,
/// <c>Http3RequestBodyStream</c>) and every response path of every version, buffered and streamed (a
/// slow reader refusing to drain, #1384), including the HTTP/2 connection's shared output.
/// </summary>
/// <remarks>
/// <para>
/// The gate's accounting performs no I/O. The caller bounds each blocking transport operation with
/// the delay from <see cref="TryGetOperationTimeout"/>, then reports the elapsed wait and octets moved
/// through <see cref="Record"/>. A response path does both through <see cref="MinDataRateWait"/>, which
/// borrows the gate's one reusable deadline (<see cref="RentDeadline"/>) and arms it only while an
/// operation actually waits. The gate serves one transfer, whose operations run one at a time.
/// Because only the wait duration of the blocking operation is recorded, an application that is slow
/// to read (or a peer slow to acknowledge) between operations never counts against the peer's rate.
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
    private static readonly TimeSpan _maxOperationTimeout = TimeSpan.FromHours(1);

    private readonly double _bytesPerSecond;
    private readonly long _graceTicks;
    private readonly long _frequency;
    private readonly TimeProvider _timeProvider;

    private long _bytesTransferred;
    private long _ticksWaited;

    // #1384 — the deadline MinDataRateWait bounds a pending operation with: created at the gate's first
    // bounded operation, armed only while one waits, and reset after it, so a transfer pays for one
    // source at most however many operations it runs. Replaced only after it fired, since a cancelled
    // source cannot be reset. _deadlineInUse marks it lent out.
    private CancellationTokenSource? _deadline;
    private int _deadlineInUse;

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
    /// Gets the grace period, in <see cref="TimeProvider"/> ticks, during which the rate is not yet
    /// enforced.
    /// </summary>
    public long GraceTicks => _graceTicks;

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

        timeout = ToOperationTimeout(remainingTicks);
        return true;
    }

    /// <summary>
    /// Converts a wait, in <see cref="TimeProvider"/> ticks, to the timeout a blocking transport operation
    /// is bounded by, clamped to the same sane upper bound as <see cref="TryGetOperationTimeout"/>.
    /// </summary>
    /// <param name="ticks">The wait, in clock ticks; a non-positive value yields <see cref="TimeSpan.Zero"/>.</param>
    /// <returns>The operation timeout.</returns>
    public TimeSpan ToOperationTimeout(long ticks)
    {
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = ticks / (double)_frequency;
        return seconds >= _maxOperationTimeout.TotalSeconds
            ? _maxOperationTimeout
            : TimeSpan.FromSeconds(seconds);
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

    /// <summary>
    /// Lends out the gate's reusable deadline for one operation, unarmed: the operation starts with its
    /// token, and only an operation that waits arms it. Every rental is matched by
    /// <see cref="ReturnDeadline"/>.
    /// </summary>
    /// <returns>
    /// The gate's deadline, created on first use; or, should a second operation run while it is lent out,
    /// which the transfer's one-at-a-time contract does not do, a separate deadline of its own.
    /// </returns>
    internal CancellationTokenSource RentDeadline()
    {
        if (Interlocked.Exchange(ref _deadlineInUse, 1) == 1)
        {
            return CreateDeadline();
        }

        return _deadline ??= CreateDeadline();
    }

    /// <summary>
    /// Takes back a deadline <see cref="RentDeadline"/> lent out, once its operation is over: disarmed for
    /// the next operation, or discarded when it fired, since a cancelled source cannot be reset.
    /// </summary>
    /// <param name="deadline">The deadline the operation used.</param>
    internal void ReturnDeadline(CancellationTokenSource deadline)
    {
        if (!ReferenceEquals(deadline, _deadline))
        {
            deadline.Dispose();
            return;
        }

        if (!deadline.TryReset())
        {
            _deadline = null;
            deadline.Dispose();
        }

        Volatile.Write(ref _deadlineInUse, 0);
    }

    private CancellationTokenSource CreateDeadline()
    {
        // On the system clock the timer is created by the first CancelAfter, so a transfer whose
        // operations never wait creates none; another clock needs its timer bound at construction.
        return ReferenceEquals(_timeProvider, TimeProvider.System)
            ? new CancellationTokenSource()
            : new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
    }
}

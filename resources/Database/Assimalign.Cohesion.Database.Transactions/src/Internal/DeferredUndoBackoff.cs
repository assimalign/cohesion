using System;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// The retry schedule of deferred undo (#1226 owner decision of 2026-10-04): a writer whose
/// undo failed keeps its locks until the undo completes, so the retry must not wait a whole
/// maintenance interval. The first retry is due <see cref="InitialDelay"/> after the deferral,
/// each failed retry doubles the delay up to <see cref="Limit"/>, a new deferral starts over,
/// and the schedule stops once nothing is deferred.
/// </summary>
/// <remarks>
/// Not thread-safe: the manager guards it with its own lock. Time comes from a
/// <see cref="TimeProvider"/>, so tests can drive the schedule without waiting.
/// </remarks>
internal sealed class DeferredUndoBackoff
{
    /// <summary>
    /// The delay before the first retry of a deferred undo.
    /// </summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The longest delay when no limit is configured: one default maintenance interval.
    /// </summary>
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private TimeSpan _initial = InitialDelay;
    private TimeSpan _limit = DefaultLimit;
    private TimeSpan _delay;
    private long _scheduledAt;
    private bool _scheduled;

    internal DeferredUndoBackoff(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Gets or sets the longest delay between two retries: the engine's maintenance interval,
    /// whose full pass retries deferred undo too.
    /// </summary>
    internal TimeSpan Limit
    {
        get => _limit;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _limit = value;
            if (_delay > value)
            {
                _delay = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the delay before the first retry after a deferral:
    /// <see cref="InitialDelay"/> unless set. Capped by <see cref="Limit"/>.
    /// </summary>
    internal TimeSpan Initial
    {
        get => _initial;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _initial = value;
        }
    }

    /// <summary>
    /// Gets the time until the next retry is due: zero when it is due now, null when nothing is
    /// deferred.
    /// </summary>
    internal TimeSpan? DueIn
    {
        get
        {
            if (!_scheduled)
            {
                return null;
            }

            var remaining = _delay - _time.GetElapsedTime(_scheduledAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets whether a retry is due now.
    /// </summary>
    internal bool IsDue => DueIn == TimeSpan.Zero;

    /// <summary>
    /// Starts the schedule over for a new deferral: the next retry is due after
    /// <see cref="Initial"/> (or <see cref="Limit"/>, when that is shorter).
    /// </summary>
    internal void OnDeferred() => Schedule(_initial < _limit ? _initial : _limit);

    /// <summary>
    /// Doubles the delay, up to <see cref="Limit"/>, after a retry that left a writer deferred.
    /// </summary>
    internal void OnRetryFailed()
    {
        if (_delay <= TimeSpan.Zero)
        {
            OnDeferred();
            return;
        }

        // Compared before adding, so a limit near TimeSpan.MaxValue cannot overflow.
        Schedule(_delay >= _limit / 2 ? _limit : _delay + _delay);
    }

    /// <summary>
    /// Stops the schedule: nothing is deferred any more.
    /// </summary>
    internal void OnCleared()
    {
        _scheduled = false;
        _delay = TimeSpan.Zero;
    }

    private void Schedule(TimeSpan delay)
    {
        _delay = delay;
        _scheduledAt = _time.GetTimestamp();
        _scheduled = true;
    }
}

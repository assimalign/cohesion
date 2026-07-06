using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Counts discrete events over a trailing time window and reports when the count within that
/// window exceeds a configured maximum. Backs the HTTP/2 flood detectors (rapid stream reset,
/// SETTINGS flood, PING flood) in <see cref="Http2FloodGuard"/>.
/// </summary>
/// <remarks>
/// <para>
/// The window is a genuine sliding window, not a tumbling one: each recorded event carries its
/// timestamp, and stale timestamps (older than <c>now - window</c>) are evicted before the count
/// is compared to the maximum, so a burst that straddles a fixed bucket boundary cannot slip past
/// the limit. The backing <see cref="Queue{T}"/> only ever holds the events currently inside the
/// window (at most <c>max + 1</c> before the exceed is reported), so memory tracks live traffic
/// rather than the configured maximum.
/// </para>
/// <para>
/// Not thread-safe by design: an HTTP/2 connection's frames are processed sequentially on a single
/// receive loop, which is the only caller. AOT-safe — plain queue arithmetic, no reflection.
/// </para>
/// </remarks>
internal sealed class Http2SlidingWindowCounter
{
    private readonly Queue<long> _events = new();
    private readonly int _maxEventsPerWindow;
    private readonly long _windowMilliseconds;

    /// <summary>
    /// Creates a counter that tolerates up to <paramref name="maxEventsPerWindow"/> events within
    /// any trailing <paramref name="windowMilliseconds"/>-long window.
    /// </summary>
    /// <param name="maxEventsPerWindow">The maximum number of events allowed within the window; the next event over this trips the exceed.</param>
    /// <param name="windowMilliseconds">The trailing window length in milliseconds.</param>
    public Http2SlidingWindowCounter(int maxEventsPerWindow, long windowMilliseconds)
    {
        _maxEventsPerWindow = maxEventsPerWindow;
        _windowMilliseconds = windowMilliseconds;
    }

    /// <summary>
    /// Records an event at <paramref name="nowMilliseconds"/>, evicts events that fell out of the
    /// trailing window, and returns whether the number of events still inside the window now
    /// exceeds the configured maximum.
    /// </summary>
    /// <param name="nowMilliseconds">
    /// The current monotonic timestamp in milliseconds (for example <see cref="System.Environment.TickCount64"/>).
    /// Callers pass a monotonically non-decreasing clock so eviction is well-defined.
    /// </param>
    /// <returns><see langword="true"/> when recording this event pushes the in-window count above the maximum.</returns>
    public bool RecordAndCheckExceeded(long nowMilliseconds)
    {
        _events.Enqueue(nowMilliseconds);

        long cutoff = nowMilliseconds - _windowMilliseconds;
        while (_events.Count > 0 && _events.Peek() < cutoff)
        {
            _events.Dequeue();
        }

        return _events.Count > _maxEventsPerWindow;
    }
}
